using System.Security.Cryptography;
using FFMpegCore;
using FFMpegCore.Enums;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Schermpostvak.Api.Data;
using Schermpostvak.Api.Endpoints;

namespace Schermpostvak.Api.Services;

/// <summary>
/// One pass over the work queue: download new media, publish approved media,
/// expire media whose show-until has passed (items without one stay until removed), and clean up old rejected files.
/// </summary>
public class MediaProcessor(
    AppDbContext db,
    XiboClient xibo,
    IOptions<StorageOptions> storage,
    IOptions<XiboOptions> xiboOptions,
    LiveUpdates live,
    ILogger<MediaProcessor> log)
{
    private const int MaxAttempts = 5;
    private readonly string _root = storage.Value.Root;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await DownloadNewAsync(ct);
        await PublishApprovedAsync(ct);
        await ExpireAsync(ct);
        await CleanupRejectedAsync(ct);
    }

    public string Absolute(string relative) => Path.Combine(_root, relative);

    // 1. Normalise the file the webhook received and put it in the review queue
    private async Task DownloadNewAsync(CancellationToken ct)
    {
        var items = await db.MediaItems
            .Where(m => m.Status == MediaStatus.Downloading && m.Attempts < MaxAttempts)
            .OrderBy(m => m.ReceivedAt).Take(10).ToListAsync(ct);

        foreach (var item in items)
        {
            try
            {
                var incoming = Absolute(WebhookEndpoints.IncomingPath(item.Id));
                if (!File.Exists(incoming)) throw new FileNotFoundException("Het bestand uit WhatsApp is niet ontvangen.");
                var bytes = await File.ReadAllBytesAsync(incoming, ct);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));

                if (await db.MediaItems.AnyAsync(m => m.Sha256 == hash && m.Id != item.Id, ct))
                {
                    item.Sha256 = hash;
                    item.Status = MediaStatus.Rejected;
                    item.RejectReason = "Dubbel (automatisch)";
                    File.Delete(incoming);
                    await db.SaveChangesAsync(ct);
                    continue; // published after the loop
                }

                var dir = Path.Combine("items", item.Id.ToString("N"));
                Directory.CreateDirectory(Absolute(dir));

                if (item.Type == MediaType.Photo)
                {
                    using var image = new MagickImage(bytes);
                    image.AutoOrient();          // respect phone rotation
                    image.Strip();               // remove EXIF and GPS location
                    image.Resize(new MagickGeometry(3840, 3840) { Greater = true });
                    image.Quality = 90;
                    image.Format = MagickFormat.Jpeg;
                    item.FilePath = Path.Combine(dir, "media.jpg");
                    await image.WriteAsync(Absolute(item.FilePath), ct);
                }
                else
                {
                    var original = Absolute(Path.Combine(dir, "original"));
                    await File.WriteAllBytesAsync(original, bytes, ct);
                    item.FilePath = Path.Combine(dir, "media.mp4");
                    await FFMpegArguments
                        .FromFileInput(original)
                        .OutputToFile(Absolute(item.FilePath), true, o => o
                            .WithVideoCodec(VideoCodec.LibX264)
                            .WithAudioCodec(AudioCodec.Aac)
                            .WithConstantRateFactor(23)
                            .WithFastStart()
                            .WithCustomArgument("-map_metadata -1 -vf scale='min(1920,iw)':-2"))
                        .ProcessAsynchronously();
                    File.Delete(original);
                    item.DurationSeconds = (await FFProbe.AnalyseAsync(Absolute(item.FilePath))).Duration.TotalSeconds;
                }

                item.ThumbPath = Path.Combine(dir, "thumb.jpg");
                await CreateThumbnailAsync(item, ct);

                File.Delete(incoming);
                item.Sha256 = hash;
                item.Status = MediaStatus.Pending;
                item.Error = null;
            }
            catch (Exception ex)
            {
                Fail(item, ex, "download");
            }
            await db.SaveChangesAsync(ct);
        }
        if (items.Any(i => i.Status != MediaStatus.Downloading)) live.Publish(LiveUpdates.Queue);
    }

    private async Task CreateThumbnailAsync(MediaItem item, CancellationToken ct)
    {
        var thumb = Absolute(item.ThumbPath!);
        if (item.Type == MediaType.Video)
        {
            var at = TimeSpan.FromSeconds(Math.Min(1, (item.DurationSeconds ?? 0) / 2));
            await FFMpeg.SnapshotAsync(Absolute(item.FilePath!), thumb, captureTime: at);
        }
        else
        {
            File.Copy(Absolute(item.FilePath!), thumb, true);
        }

        using var image = new MagickImage(thumb);
        image.Thumbnail(new MagickGeometry(640, 640));
        image.Quality = 80;
        await image.WriteAsync(thumb, ct);
    }

    // 2. Upload approved items to Xibo and tag them, from show-from on
    private async Task PublishApprovedAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        var items = await db.MediaItems
            .Where(m => m.Status == MediaStatus.Approved && m.Attempts < MaxAttempts && m.ShowFrom <= now && (m.ShowUntil == null || m.ShowUntil > now))
            .OrderBy(m => m.ReviewedAt).Take(5).ToListAsync(ct);

        foreach (var item in items)
        {
            try
            {
                var path = Absolute(item.FilePath!);
                if (item.Type == MediaType.Photo && item.Rotation % 360 != 0)
                {
                    using var image = new MagickImage(path);
                    image.Rotate(item.Rotation);
                    await image.WriteAsync(path, ct);
                    item.Rotation = 0; // applied, don't rotate again on retry
                }

                var name = $"whatsapp-{item.Id:N}";
                if (item.XiboMediaId is null)
                {
                    item.XiboMediaId = await xibo.UploadAsync(path, name, ct);
                    await db.SaveChangesAsync(ct); // don't upload again if tagging fails
                }

                var seconds = item.Type == MediaType.Photo
                    ? item.DisplaySeconds ?? xiboOptions.Value.DefaultDisplaySeconds
                    : Math.Max(1, (int)Math.Ceiling(item.DurationSeconds ?? 0));
                await xibo.PublishMediaAsync(item.XiboMediaId.Value, name, seconds, xiboOptions.Value.Tag, item.ShowUntil, ct);

                item.Status = MediaStatus.Live;
                item.Error = null;
            }
            catch (Exception ex)
            {
                Fail(item, ex, "publish");
            }
            await db.SaveChangesAsync(ct);
        }
        if (items.Any(i => i.Status != MediaStatus.Approved)) live.Publish(LiveUpdates.Live);
    }

    // 3. Xibo deletes expired media itself; we mark the items and make sure the media is gone
    private async Task ExpireAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        var expired = await db.MediaItems
            .Where(m => (m.Status == MediaStatus.Live || m.Status == MediaStatus.Approved) && m.ShowUntil != null && m.ShowUntil < now)
            .ToListAsync(ct);

        foreach (var item in expired)
        {
            try
            {
                if (item.XiboMediaId is { } mediaId) await xibo.DeleteMediaAsync(mediaId, ct);
                item.Status = MediaStatus.Expired;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not delete expired media {Id} from Xibo", item.Id);
            }
        }
        await db.SaveChangesAsync(ct);
        if (expired.Any(i => i.Status == MediaStatus.Expired)) live.Publish(LiveUpdates.Live);
    }

    // 4. Remove files of rejected items after the retention period
    private async Task CleanupRejectedAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-storage.Value.RejectedRetentionDays);
        var old = await db.MediaItems
            .Where(m => m.Status == MediaStatus.Rejected && m.FilePath != null && m.ReviewedAt < cutoff)
            .ToListAsync(ct);

        foreach (var item in old)
        {
            var dir = Path.GetDirectoryName(Absolute(item.FilePath!));
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
            item.FilePath = null;
            item.ThumbPath = null;
        }
        await db.SaveChangesAsync(ct);
    }

    private void Fail(MediaItem item, Exception ex, string step)
    {
        item.Attempts++;
        item.Error = $"{step}: {ex.Message}";
        if (item.Attempts >= MaxAttempts) item.Status = MediaStatus.Failed;
        log.LogWarning(ex, "Step {Step} failed for {Id} (attempt {Attempt})", step, item.Id, item.Attempts);
    }
}

/// <summary>
/// Stores which Xibo widget belongs to which playlist as "playlistId:widgetId,...".
/// Older entries without a playlist id are kept under a negative key so they can still be removed.
/// </summary>
public static class WidgetIds
{
    public static Dictionary<int, int> Parse(string? value)
    {
        var result = new Dictionary<int, int>();
        var legacy = -1;
        foreach (var part in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split(':');
            if (pair.Length == 2 && int.TryParse(pair[0], out var playlist) && int.TryParse(pair[1], out var widget))
                result[playlist] = widget;
            else if (int.TryParse(part, out var old))
                result[legacy--] = old;
        }
        return result;
    }

    public static string Format(Dictionary<int, int> placed) =>
        string.Join(',', placed.Select(p => $"{p.Key}:{p.Value}"));
}
