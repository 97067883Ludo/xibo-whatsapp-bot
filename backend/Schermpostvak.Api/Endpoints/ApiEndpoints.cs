using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Schermpostvak.Api.Data;
using Schermpostvak.Api.Services;

namespace Schermpostvak.Api.Endpoints;

public record ItemDto(
    Guid Id, string Type, string Status,
    string SenderName, string SenderJid,
    string ChatJid, string ChatName, bool ChatIsGroup,
    string Caption, DateTime ReceivedAt, double? DurationSeconds,
    string ThumbUrl, string FileUrl,
    string? Screen, DateTime? ShowFrom, DateTime? ShowUntil, int? DisplaySeconds,
    string? ReviewedBy, string? Error);

/// <summary>ShowUntil is optional: without it the item stays on the screens until a reviewer removes it.</summary>
public record ApproveRequest(DateTime ShowFrom, DateTime? ShowUntil, int? DisplaySeconds, int Rotation);
public record RejectRequest(string Reason);
public record ChatDto(string Jid, string Name, bool IsGroup, bool Enabled, DateTime FirstSeenAt);
public record EnableRequest(bool Enabled);
public record SettingsRequest(int? XiboFolderId);

/// <summary>JSON API used by the dashboard.</summary>
public static class ApiEndpoints
{
    // TODO: replace with the signed-in reviewer once login (e.g. Microsoft Entra ID) is added.
    private const string Reviewer = "Beoordelaar";

    public static void MapApiEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", async (AppDbContext db, WahaClient waha, CancellationToken ct) => new
        {
            WhatsApp = await waha.GetConnectionStateAsync(ct),
            Pending = await db.MediaItems.CountAsync(m => m.Status == MediaStatus.Pending, ct),
            Live = await db.MediaItems.CountAsync(m => m.Status == MediaStatus.Live || m.Status == MediaStatus.Approved, ct),
            FailedDownloads = await db.MediaItems.CountAsync(m => m.Status == MediaStatus.Failed && m.ReviewedAt == null, ct),
            FailedPlacements = await db.MediaItems.CountAsync(m => m.Status == MediaStatus.Failed && m.ReviewedAt != null, ct),
            NewChats = await db.Chats.CountAsync(c => !c.Enabled, ct),
            EnabledChats = await db.Chats.CountAsync(c => c.Enabled, ct)
        });

        api.MapGet("/config", (IOptions<XiboOptions> xibo) => new
        {
            xibo.Value.DefaultDisplaySeconds,
            xibo.Value.Tag
        });

        // Review queue
        api.MapGet("/items", async (string? status, AppDbContext db, CancellationToken ct) =>
        {
            var wanted = Enum.TryParse<MediaStatus>(status, true, out var s) ? s : MediaStatus.Pending;
            var items = await db.MediaItems.Include(m => m.Chat)
                .Where(m => m.Status == wanted)
                // Failed placements (already reviewed) are listed under /live; here only failed downloads
                .Where(m => wanted != MediaStatus.Failed || m.ReviewedAt == null)
                .OrderByDescending(m => m.ReceivedAt)
                .ToListAsync(ct);
            return items.Select(ToDto);
        });

        api.MapGet("/items/{id:guid}/thumb", async (Guid id, AppDbContext db, MediaProcessor files) =>
        {
            var item = await db.MediaItems.FindAsync(id);
            return item?.ThumbPath is null ? Results.NotFound() : Results.File(files.Absolute(item.ThumbPath), "image/jpeg");
        });

        api.MapGet("/items/{id:guid}/file", async (Guid id, AppDbContext db, MediaProcessor files) =>
        {
            var item = await db.MediaItems.FindAsync(id);
            if (item?.FilePath is null) return Results.NotFound();
            var mime = item.Type == MediaType.Video ? "video/mp4" : "image/jpeg";
            return Results.File(files.Absolute(item.FilePath), mime, enableRangeProcessing: true);
        });

        api.MapPost("/items/{id:guid}/approve", async (Guid id, ApproveRequest req, AppDbContext db, IOptions<XiboOptions> xibo, LiveUpdates live, WorkerSignal worker, CancellationToken ct) =>
        {
            var item = await db.MediaItems.FindAsync([id], ct);
            if (item is null) return Results.NotFound();
            if (item.Status != MediaStatus.Pending) return Results.Conflict("Dit item is al beoordeeld.");
            if (req.ShowUntil is { } until && (until <= req.ShowFrom || until <= DateTime.Now))
                return Results.BadRequest("De einddatum moet na de startdatum en in de toekomst liggen.");

            item.Status = MediaStatus.Approved;
            item.ShowFrom = req.ShowFrom;
            item.ShowUntil = req.ShowUntil;
            item.DisplaySeconds = req.DisplaySeconds ?? xibo.Value.DefaultDisplaySeconds;
            item.Rotation = ((req.Rotation % 360) + 360) % 360;
            item.ReviewedBy = Reviewer;
            item.ReviewedAt = DateTime.UtcNow;
            item.Attempts = 0;
            await db.SaveChangesAsync(ct);
            live.Publish(LiveUpdates.Queue, LiveUpdates.Live);
            worker.Wake();
            return Results.NoContent();
        });

        api.MapPost("/items/{id:guid}/reject", async (Guid id, RejectRequest req, AppDbContext db, LiveUpdates live, CancellationToken ct) =>
        {
            var item = await db.MediaItems.FindAsync([id], ct);
            if (item is null) return Results.NotFound();
            var failedDownload = item.Status == MediaStatus.Failed && item.ReviewedAt == null;
            if (item.Status != MediaStatus.Pending && !failedDownload) return Results.Conflict("Dit item is al beoordeeld.");

            item.Status = MediaStatus.Rejected;
            item.RejectReason = req.Reason;
            item.ReviewedBy = Reviewer;
            item.ReviewedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            live.Publish(LiveUpdates.Queue);
            return Results.NoContent();
        });

        // Live on screens
        api.MapGet("/live", async (AppDbContext db, CancellationToken ct) =>
        {
            var items = await db.MediaItems.Include(m => m.Chat)
                .Where(m => m.Status == MediaStatus.Live || m.Status == MediaStatus.Approved
                         || (m.Status == MediaStatus.Failed && m.ReviewedAt != null))
                .OrderByDescending(m => m.ShowFrom)
                .ToListAsync(ct);
            return items.Select(ToDto);
        });

        // Takes an item off the screens by removing its tag; the media stays in the Xibo library.
        api.MapPost("/live/{id:guid}/remove", async (Guid id, AppDbContext db, XiboClient xibo, IOptions<XiboOptions> xiboOptions, LiveUpdates live, CancellationToken ct) =>
        {
            var item = await db.MediaItems.FindAsync([id], ct);
            if (item is null) return Results.NotFound();

            try
            {
                // Items placed by older versions sit in playlists as widgets instead of being tagged
                foreach (var widgetId in WidgetIds.Parse(item.XiboWidgetIds).Values)
                    await xibo.DeleteWidgetAsync(widgetId, ct);
                if (item.XiboMediaId is { } mediaId) await xibo.UntagMediaAsync(mediaId, xiboOptions.Value.Tag, ct);
            }
            catch (Exception ex)
            {
                return Results.Text($"Xibo gaf een fout: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }

            item.Status = MediaStatus.Removed;
            await db.SaveChangesAsync(ct);
            live.Publish(LiveUpdates.Live);
            return Results.NoContent();
        });

        // Try a failed item again: approved items are placed again, others are downloaded again.
        api.MapPost("/items/{id:guid}/retry", async (Guid id, AppDbContext db, LiveUpdates live, WorkerSignal worker, CancellationToken ct) =>
        {
            var item = await db.MediaItems.FindAsync([id], ct);
            if (item is null) return Results.NotFound();
            if (item.Status != MediaStatus.Failed) return Results.Conflict("Dit item is niet mislukt.");
            if (item.ReviewedAt != null && item.ShowUntil <= DateTime.Now)
                return Results.Conflict("De einddatum van dit item is al voorbij. Haal het item weg en keur het opnieuw goed.");

            item.Status = item.ReviewedAt != null ? MediaStatus.Approved : MediaStatus.Downloading;
            item.Attempts = 0;
            item.Error = null;
            await db.SaveChangesAsync(ct);
            live.Publish(LiveUpdates.Queue, LiveUpdates.Live);
            worker.Wake();
            return Results.NoContent();
        });

        // Settings
        api.MapGet("/settings", (SettingsStore settings) => settings.Get());

        api.MapPut("/settings", async (SettingsRequest req, SettingsStore settings, XiboClient xibo, CancellationToken ct) =>
        {
            string? folderName = null;
            if (req.XiboFolderId is { } folderId)
            {
                List<XiboFolder> folders;
                try { folders = await xibo.GetFoldersAsync(ct); }
                catch (Exception ex) { return Results.Text($"Xibo gaf een fout: {ex.Message}", statusCode: StatusCodes.Status502BadGateway); }
                var folder = folders.FirstOrDefault(f => f.Id == folderId);
                if (folder is null) return Results.BadRequest("Deze map bestaat niet (meer) in Xibo.");
                folderName = folder.Path;
            }
            var updated = settings.Get() with { XiboFolderId = req.XiboFolderId, XiboFolderName = folderName };
            settings.Save(updated);
            return Results.Ok(updated);
        });

        api.MapGet("/xibo/folders", async (XiboClient xibo, CancellationToken ct) =>
        {
            try { return Results.Ok(await xibo.GetFoldersAsync(ct)); }
            catch (Exception ex) { return Results.Text($"Kan de mappen niet ophalen uit Xibo: {ex.Message}", statusCode: StatusCodes.Status502BadGateway); }
        });

        // Chats
        api.MapGet("/chats", async (AppDbContext db, CancellationToken ct) =>
            await db.Chats.OrderBy(c => c.Enabled).ThenBy(c => c.Name)
                .Select(c => new ChatDto(c.Jid, c.Name, c.IsGroup, c.Enabled, DateTime.SpecifyKind(c.FirstSeenAt, DateTimeKind.Utc)))
                .ToListAsync(ct));

        api.MapPut("/chats/{jid}/enabled", async (string jid, EnableRequest req, AppDbContext db, LiveUpdates live, CancellationToken ct) =>
        {
            var chat = await db.Chats.FindAsync([jid], ct);
            if (chat is null) return Results.NotFound();
            chat.Enabled = req.Enabled;
            await db.SaveChangesAsync(ct);
            live.Publish(LiveUpdates.Chats);
            return Results.NoContent();
        });
    }

    private static ItemDto ToDto(MediaItem m) => new(
        m.Id, m.Type.ToString(), m.Status.ToString(),
        m.SenderName, m.SenderJid,
        m.ChatJid, m.Chat?.Name ?? m.ChatJid, m.Chat?.IsGroup ?? false,
        m.Caption, DateTime.SpecifyKind(m.ReceivedAt, DateTimeKind.Utc), m.DurationSeconds,
        $"/api/items/{m.Id}/thumb", $"/api/items/{m.Id}/file",
        m.Screen, m.ShowFrom, m.ShowUntil, m.DisplaySeconds,
        m.ReviewedBy, m.Error);
}
