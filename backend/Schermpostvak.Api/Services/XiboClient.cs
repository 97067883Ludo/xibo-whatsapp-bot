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

    /// <summary>
    /// Puts the media on the screens: tags it so the dynamic playlists pick it up, sets how long
    /// it is shown per turn, and lets Xibo delete it itself at show-until.
    /// </summary>
    public async Task PublishMediaAsync(int mediaId, string name, int durationSeconds, string tag, DateTime until, CancellationToken ct)
    {
        await AuthorizeAsync(ct);
        var res = await http.PutAsync($"library/{mediaId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = name,
            ["duration"] = durationSeconds.ToString(CultureInfo.InvariantCulture),
            ["retired"] = "0",
            ["tags"] = tag,
            ["updateInLayouts"] = "1",
            ["expires"] = until.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["deleteOnExpiry"] = "1"
        }), ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Xibo could not tag media {mediaId}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>Only used to clean up items placed by older versions, which used playlist widgets.</summary>
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
