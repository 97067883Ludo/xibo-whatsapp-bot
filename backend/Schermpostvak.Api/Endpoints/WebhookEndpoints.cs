using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Schermpostvak.Api.Data;
using Schermpostvak.Api.Services;

namespace Schermpostvak.Api.Endpoints;

/// <summary>Receives messages from WAHA and stores photos and videos for processing.</summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/webhook/waha", HandleAsync);
    }

    /// <summary>Where the webhook leaves the downloaded file; the worker picks it up from there.</summary>
    // One message at a time, so the same message arriving twice ("message" and "message.any")
    // can't slip past the duplicate check. Traffic is low, so this costs nothing.
    private static readonly SemaphoreSlim StoreLock = new(1, 1);

    public static string IncomingPath(Guid itemId) => Path.Combine("items", itemId.ToString("N"), "incoming");

    private static async Task<IResult> HandleAsync(
        JsonElement body,
        string? token,
        AppDbContext db,
        WahaClient waha,
        IOptions<WebhookOptions> options,
        IOptions<WahaOptions> wahaOptions,
        IOptions<StorageOptions> storage,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.Value.Token) || token != options.Value.Token)
            return Results.Unauthorized();

        // "message" = incoming only, "message.any" = also what the bot's own phone sends. Both are collected.
        var evt = Str(body, "event");
        if (evt is not ("message" or "message.any") || !body.TryGetProperty("payload", out var msg)) return Results.Ok();
        if (Str(body, "session") is { } session && session != wahaOptions.Value.Session) return Results.Ok();
        if (!Bool(msg, "hasMedia")) return Results.Ok();

        // For our own messages "from" is the bot's number and "to" is the chat
        var fromMe = Bool(msg, "fromMe");
        var chatJid = fromMe ? Str(msg, "to") : Str(msg, "from");
        var messageId = Str(msg, "id");
        if (chatJid is null || messageId is null) return Results.Ok();
        if (chatJid == "status@broadcast" || chatJid.EndsWith("@newsletter")) return Results.Ok();

        if (!msg.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object) return Results.Ok();
        var mime = Str(media, "mimetype") ?? "";
        MediaType? type = mime switch
        {
            "image/webp" => null, // stickers
            _ when mime.StartsWith("image/") => MediaType.Photo,
            _ when mime.StartsWith("video/") => MediaType.Video,
            _ => null
        };
        if (type is null) return Results.Ok();

        var log = loggers.CreateLogger("Schermpostvak.Webhook");
        var url = Str(media, "url");
        if (url is null)
        {
            log.LogWarning("WAHA did not download media for {Id}: {Error}", messageId,
                media.TryGetProperty("error", out var e) ? e.ToString() : "no url (check WAHA_EVENTS_DOWNLOAD_MEDIA)");
            return Results.Ok();
        }

        var isGroup = chatJid.EndsWith("@g.us");
        string senderJid, senderName;
        if (fromMe)
        {
            body.TryGetProperty("me", out var me);
            senderJid = Str(me, "id") ?? Str(msg, "from") ?? "";
            senderName = Str(me, "pushName") ?? SenderName(msg) ?? "";
        }
        else
        {
            senderJid = isGroup ? Str(msg, "participant") ?? "" : chatJid;
            senderName = SenderName(msg) ?? (senderJid != "" ? await waha.GetContactNameAsync(senderJid, ct) : null) ?? "";
        }

        await StoreLock.WaitAsync(ct);
        try
        {
            return await StoreAsync(msg, chatJid, messageId, isGroup, senderJid, senderName, type.Value, url,
                db, waha, storage.Value, log, ct);
        }
        finally
        {
            StoreLock.Release();
        }
    }

    private static async Task<IResult> StoreAsync(
        JsonElement msg, string chatJid, string messageId, bool isGroup, string senderJid, string senderName,
        MediaType type, string url, AppDbContext db, WahaClient waha, StorageOptions storage, ILogger log, CancellationToken ct)
    {
        // Every chat is registered and collected from automatically; a reviewer can switch a chat off to block it.
        var chat = await db.Chats.FindAsync([chatJid], ct);
        if (chat is null)
        {
            chat = new Chat
            {
                Jid = chatJid,
                IsGroup = isGroup,
                Enabled = true,
                Name = isGroup
                    ? await waha.GetGroupSubjectAsync(chatJid, ct) ?? chatJid
                    : senderJid == chatJid && senderName != "" ? senderName
                        : await waha.GetContactNameAsync(chatJid, ct) ?? chatJid
            };
            db.Chats.Add(chat);
            await db.SaveChangesAsync(ct);
        }
        if (!chat.Enabled) return Results.Ok();

        if (await db.MediaItems.AnyAsync(m => m.WaMessageId == messageId, ct)) return Results.Ok();

        // WAHA deletes its copy after a while, so fetch the file now and keep it until it is processed.
        byte[] bytes;
        try
        {
            bytes = await waha.DownloadAsync(url, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not download media {Id} from WAHA", messageId);
            return Results.StatusCode(StatusCodes.Status502BadGateway); // WAHA retries the webhook
        }

        var item = new MediaItem
        {
            WaMessageId = messageId,
            ChatJid = chatJid,
            SenderJid = senderJid,
            SenderName = senderName,
            Caption = Str(msg, "body") ?? "",
            Type = type
        };
        var incoming = Path.Combine(storage.Root, IncomingPath(item.Id));
        Directory.CreateDirectory(Path.GetDirectoryName(incoming)!);
        await File.WriteAllBytesAsync(incoming, bytes, ct);

        db.MediaItems.Add(item);
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    /// <summary>The sender's own WhatsApp name, if the engine includes it in the message.</summary>
    private static string? SenderName(JsonElement msg)
    {
        if (!msg.TryGetProperty("_data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        return Str(data, "notifyName") ?? Str(data, "pushName") ?? Str(data, "PushName")
            ?? (data.TryGetProperty("Info", out var info) ? Str(info, "PushName") : null);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
