using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Schermpostvak.Api.Services;

/// <summary>
/// Talks to WAHA (WhatsApp HTTP API). Endpoints follow the WAHA docs (waha.devlike.pro);
/// check them against the version you run in its Swagger UI at /.
/// </summary>
public class WahaClient(HttpClient http, IOptions<WahaOptions> options, ILogger<WahaClient> log)
{
    private string Session => options.Value.Session;

    /// <summary>
    /// Downloads a file WAHA stored for a received message (payload.media.url).
    /// Only the path of the URL is used, so it always goes to the configured BaseUrl.
    /// </summary>
    public async Task<byte[]> DownloadAsync(string mediaUrl, CancellationToken ct)
    {
        var path = Uri.TryCreate(mediaUrl, UriKind.Absolute, out var abs) ? abs.PathAndQuery : mediaUrl;
        return await http.GetByteArrayAsync(path.TrimStart('/'), ct);
    }

    public async Task<string?> GetGroupSubjectAsync(string groupJid, CancellationToken ct)
    {
        try
        {
            var body = await http.GetFromJsonAsync<JsonElement>(
                $"api/{Uri.EscapeDataString(Session)}/groups/{Uri.EscapeDataString(groupJid)}", ct);
            // The response shape depends on the engine
            return FirstString(body, "subject", "name")
                ?? (body.TryGetProperty("groupMetadata", out var meta) ? FirstString(meta, "subject") : null);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not fetch group name for {Jid}", groupJid);
            return null;
        }
    }

    /// <summary>Name of a contact: the name saved on the bot's phone, else the name they set themselves.</summary>
    public async Task<string?> GetContactNameAsync(string jid, CancellationToken ct)
    {
        try
        {
            var body = await http.GetFromJsonAsync<JsonElement>(
                $"api/contacts?contactId={Uri.EscapeDataString(jid)}&session={Uri.EscapeDataString(Session)}", ct);
            return FirstString(body, "name", "pushname", "shortName");
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not fetch contact name for {Jid}", jid);
            return null;
        }
    }

    /// <summary>Session status: WORKING when WhatsApp is connected, otherwise e.g. SCAN_QR_CODE, STOPPED, FAILED.</summary>
    public async Task<string> GetConnectionStateAsync(CancellationToken ct)
    {
        try
        {
            var body = await http.GetFromJsonAsync<JsonElement>($"api/sessions/{Uri.EscapeDataString(Session)}", ct);
            return FirstString(body, "status") ?? "unknown";
        }
        catch
        {
            return "unreachable";
        }
    }

    private static string? FirstString(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString();
        return null;
    }
}
