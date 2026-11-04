using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Schermpostvak.Api.Services;

/// <summary>
/// Talks to the Evolution API WhatsApp gateway.
/// Endpoints follow Evolution API v2; check them against the version you run.
/// </summary>
public class EvolutionClient(HttpClient http, IOptions<EvolutionOptions> options, ILogger<EvolutionClient> log)
{
    private string Instance => Uri.EscapeDataString(options.Value.Instance);

    public async Task<(byte[] Data, string MimeType)> DownloadMediaAsync(string messageId, CancellationToken ct)
    {
        var res = await http.PostAsJsonAsync($"chat/getBase64FromMediaMessage/{Instance}",
            new { message = new { key = new { id = messageId } }, convertToMp4 = false }, ct);
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        var base64 = body.GetProperty("base64").GetString() ?? throw new InvalidOperationException("Empty media");
        var comma = base64.IndexOf(',');
        if (base64.StartsWith("data:") && comma > 0) base64 = base64[(comma + 1)..];

        var mime = body.TryGetProperty("mimetype", out var m) ? m.GetString() ?? "" : "";
        return (Convert.FromBase64String(base64), mime);
    }

    public async Task<string?> GetGroupSubjectAsync(string groupJid, CancellationToken ct)
    {
        try
        {
            var body = await http.GetFromJsonAsync<JsonElement>(
                $"group/findGroupInfos/{Instance}?groupJid={Uri.EscapeDataString(groupJid)}", ct);
            return body.TryGetProperty("subject", out var s) ? s.GetString() : null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not fetch group name for {Jid}", groupJid);
            return null;
        }
    }

    /// <summary>Returns "open" when WhatsApp is connected.</summary>
    public async Task<string> GetConnectionStateAsync(CancellationToken ct)
    {
        try
        {
            var body = await http.GetFromJsonAsync<JsonElement>($"instance/connectionState/{Instance}", ct);
            return body.GetProperty("instance").GetProperty("state").GetString() ?? "unknown";
        }
        catch
        {
            return "unreachable";
        }
    }
}
