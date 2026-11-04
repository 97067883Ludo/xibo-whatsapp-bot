using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Schermpostvak.Api.Services;

/// <summary>Caches the Xibo access token across requests.</summary>
public class XiboTokenStore
{
    public readonly SemaphoreSlim Lock = new(1, 1);
    public string? Token;
    public DateTime ExpiresAt;
}

/// <summary>
/// Minimal Xibo CMS API client (client-credentials grant).
/// Endpoint names differ slightly between Xibo versions: verify them in your CMS at /swagger.json.
/// </summary>
public class XiboClient(HttpClient http, IOptions<XiboOptions> options, XiboTokenStore tokens)
{
    private readonly XiboOptions _o = options.Value;

    private async Task AuthorizeAsync(CancellationToken ct)
    {
        await tokens.Lock.WaitAsync(ct);
        try
        {
            if (tokens.Token is null || DateTime.UtcNow >= tokens.ExpiresAt)
            {
                var res = await http.PostAsync("authorize/access_token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _o.ClientId,
                    ["client_secret"] = _o.ClientSecret
                }), ct);
                res.EnsureSuccessStatusCode();
                var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
                tokens.Token = body.GetProperty("access_token").GetString();
                var seconds = body.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
                tokens.ExpiresAt = DateTime.UtcNow.AddSeconds(seconds - 60);
            }
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Token);
        }
        finally
        {
            tokens.Lock.Release();
        }
    }

    /// <summary>Uploads a file to the library and returns its mediaId.</summary>
    public async Task<int> UploadAsync(string path, string name, CancellationToken ct)
    {
        await AuthorizeAsync(ct);
        await using var stream = File.OpenRead(path);
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue(path.EndsWith(".mp4") ? "video/mp4" : "image/jpeg");
        form.Add(file, "files", Path.GetFileName(path));
        form.Add(new StringContent(name), "name");

        var res = await http.PostAsync("library", form, ct);
        res.EnsureSuccessStatusCode();
        var body = Unwrap(await res.Content.ReadFromJsonAsync<JsonElement>(ct));
        var first = body.GetProperty("files")[0];
        if (first.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            throw new InvalidOperationException($"Xibo rejected the upload: {err.GetString()}");
        return first.GetProperty("mediaId").GetInt32();
    }

    /// <summary>Adds the media to a playlist and returns the id of the widget Xibo created for it.</summary>
    public async Task<int> AssignToPlaylistAsync(int playlistId, int mediaId, int? durationSeconds, CancellationToken ct)
    {
        await AuthorizeAsync(ct);
        var fields = new List<KeyValuePair<string, string>> { new("media[]", mediaId.ToString()) };
        if (durationSeconds is { } d)
        {
            fields.Add(new("duration", d.ToString()));
            fields.Add(new("useDuration", "1"));
        }
        var res = await http.PostAsync($"playlist/library/assign/{playlistId}", new FormUrlEncodedContent(fields), ct);
        res.EnsureSuccessStatusCode();

        // The response is the playlist; find the widget that holds our media.
        var playlist = Unwrap(await res.Content.ReadFromJsonAsync<JsonElement>(ct));
        foreach (var widget in playlist.GetProperty("widgets").EnumerateArray().Reverse())
        {
            if (widget.TryGetProperty("mediaIds", out var ids) &&
                ids.EnumerateArray().Any(i => i.ValueKind == JsonValueKind.Number && i.GetInt32() == mediaId))
                return widget.GetProperty("widgetId").GetInt32();
        }
        throw new InvalidOperationException($"Widget for media {mediaId} not found in playlist {playlistId}");
    }

    /// <summary>Sets when the widget starts and stops showing; Xibo deletes it after expiry.</summary>
    public async Task SetWidgetExpiryAsync(int widgetId, DateTime from, DateTime until, CancellationToken ct)
    {
        await AuthorizeAsync(ct);
        var res = await http.PutAsync($"playlist/widget/{widgetId}/expiry", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["fromDt"] = from.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["toDt"] = until.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["deleteOnExpiry"] = "1"
        }), ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task DeleteWidgetAsync(int widgetId, CancellationToken ct)
    {
        await AuthorizeAsync(ct);
        var res = await http.DeleteAsync($"playlist/widget/{widgetId}", ct);
        if (res.StatusCode != System.Net.HttpStatusCode.NotFound) res.EnsureSuccessStatusCode();
    }

    public async Task DeleteMediaAsync(int mediaId, CancellationToken ct)
    {
        await AuthorizeAsync(ct);
        var res = await http.DeleteAsync($"library/{mediaId}?forceDelete=1", ct);
        if (res.StatusCode != System.Net.HttpStatusCode.NotFound) res.EnsureSuccessStatusCode();
    }

    // Older Xibo versions wrap responses in { "data": ... }
    private static JsonElement Unwrap(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("data", out var d) ? d : e;
}
