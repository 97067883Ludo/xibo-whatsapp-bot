using System.Security.Cryptography;
using FFMpegCore;
using FFMpegCore.Enums;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Schermpostvak.Api.Data;

namespace Schermpostvak.Api.Services;

/// <summary>
/// One pass over the work queue: download new media, publish approved media,
/// expire media whose show-until has passed, and clean up old rejected files.
/// </summary>
public class MediaProcessor(
    AppDbContext db,
    EvolutionClient evolution,
    XiboClient xibo,
    IOptions<StorageOptions> storage,
    IOptions<XiboOptions> xiboOptions,
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

    // 1. Fetch the file from WhatsApp, normalise it and put it in the review queue
    private async Task DownloadNewAsync(CancellationToken ct)
    {
        var items = await db.MediaItems
            .Where(m => m.Status == MediaStatus.Downloading && m.Attempts < MaxAttempts)
            .OrderBy(m => m.ReceivedAt).Take(10).ToListAsync(ct);

        foreach (var item in items)
        {
            try
            {
                var (bytes, _) = await evolution.DownloadMediaAsync(item.WaMessageId, ct);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));

                if (await db.MediaItems.AnyAsync(m => m.Sha256 == hash && m.Id != item.Id, ct))
                {
                    item.Sha256 = hash;
                    item.Status = MediaStatus.Rejected;
                    item.RejectReason = "Dubbel (automatisch)";
                    await db.SaveChangesAsync(ct);
                    continue;
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

    // 2. Upload approved items to Xibo and put them on the chosen screens
    private async Task PublishApprovedAsync(CancellationToken ct)
    {
        var items = await db.MediaItems
            .Where(m => m.Status == MediaStatus.Approved && m.Attempts < MaxAttempts)
            .Take(5).ToListAsync(ct);

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

                item.XiboMediaId ??= await xibo.UploadAsync(path, $"whatsapp-{item.Id:N}", ct);

                var widgetIds = new List<int>();
                foreach (var screen in ScreensFor(item.Screen))
                {
                    var duration = item.Type == MediaType.Photo ? item.DisplaySeconds : null;
                    var widgetId = await xibo.AssignToPlaylistAsync(screen.PlaylistId, item.XiboMediaId.Value, duration, ct);
                    await xibo.SetWidgetExpiryAsync(widgetId, item.ShowFrom!.Value, item.ShowUntil!.Value, ct);
                    widgetIds.Add(widgetId);
                }

                item.XiboWidgetIds = string.Join(',', widgetIds);
                item.Status = MediaStatus.Live;
                item.Error = null;
            }
            catch (Exception ex)
            {
                Fail(item, ex, "publish");
            }
            await db.SaveChangesAsync(ct);
        }
    }

    public IEnumerable<XiboScreen> ScreensFor(string? screen) =>
        string.IsNullOrEmpty(screen) || screen == "Alle schermen"
            ? xiboOptions.Value.Screens
            : xiboOptions.Value.Screens.Where(s => s.Name == screen);

    // 3. Xibo removes expired widgets itself; we mark them and clean up the library
    private async Task ExpireAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        var expired = await db.MediaItems
            .Where(m => m.Status == MediaStatus.Live && m.ShowUntil < now)
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
