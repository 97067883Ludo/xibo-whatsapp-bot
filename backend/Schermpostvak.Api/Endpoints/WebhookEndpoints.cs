using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Schermpostvak.Api.Data;
using Schermpostvak.Api.Services;

namespace Schermpostvak.Api.Endpoints;

/// <summary>Receives messages from Evolution API and queues photos and videos for download.</summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/webhook/evolution", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        JsonElement payload,
        string? token,
        AppDbContext db,
        EvolutionClient evolution,
        IOptions<WebhookOptions> options,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.Value.Token) || token != options.Value.Token)
            return Results.Unauthorized();

        var evt = Str(payload, "event")?.ToLowerInvariant().Replace('_', '.');
        if (evt != "messages.upsert" || !payload.TryGetProperty("data", out var data))
            return Results.Ok();

        List<JsonElement> messages = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToList() : [data];
        foreach (var message in messages)
            await StoreAsync(message, db, evolution, ct);

        return Results.Ok();
    }

    private static async Task StoreAsync(JsonElement msg, AppDbContext db, EvolutionClient evolution, CancellationToken ct)
    {
        if (msg.ValueKind != JsonValueKind.Object || !msg.TryGetProperty("key", out var key)) return;
        if (key.TryGetProperty("fromMe", out var fromMe) && fromMe.ValueKind == JsonValueKind.True) return;

        var chatJid = Str(key, "remoteJid");
        var messageId = Str(key, "id");
        if (chatJid is null || messageId is null || chatJid == "status@broadcast") return;
        if (!msg.TryGetProperty("message", out var content)) return;

        var (type, media) = FindMedia(content);
        if (type is null) return;

        var isGroup = chatJid.EndsWith("@g.us");
        var pushName = Str(msg, "pushName") ?? "";

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
                    ? await evolution.GetGroupSubjectAsync(chatJid, ct) ?? chatJid
                    : pushName != "" ? pushName : chatJid
            };
            db.Chats.Add(chat);
            await db.SaveChangesAsync(ct);
        }
        if (!chat.Enabled) return;

        if (await db.MediaItems.AnyAsync(m => m.WaMessageId == messageId, ct)) return;

        db.MediaItems.Add(new MediaItem
        {
            WaMessageId = messageId,
            ChatJid = chatJid,
            SenderJid = isGroup ? Str(key, "participant") ?? "" : chatJid,
            SenderName = pushName,
            Caption = Str(media, "caption") ?? "",
            Type = type.Value,
            DurationSeconds = media.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetDouble() : null
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Finds a photo or video in a WhatsApp message. Stickers, voice notes and view-once media are skipped.</summary>
    private static (MediaType? Type, JsonElement Media) FindMedia(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return (null, default);

        foreach (var wrapper in new[] { "ephemeralMessage", "documentWithCaptionMessage" })
            if (message.TryGetProperty(wrapper, out var w) && w.TryGetProperty("message", out var inner))
                return FindMedia(inner);

        if (message.TryGetProperty("imageMessage", out var image)) return (MediaType.Photo, image);
        if (message.TryGetProperty("videoMessage", out var video)) return (MediaType.Video, video);
        if (message.TryGetProperty("documentMessage", out var doc))
        {
            var mime = Str(doc, "mimetype") ?? "";
            if (mime.StartsWith("image/")) return (MediaType.Photo, doc);
            if (mime.StartsWith("video/")) return (MediaType.Video, doc);
        }
        return (null, default);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
