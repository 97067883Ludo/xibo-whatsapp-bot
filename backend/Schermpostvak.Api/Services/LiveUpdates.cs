using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Schermpostvak.Api.Services;

/// <summary>
/// Pushes "something changed" notices to every open dashboard over a WebSocket (/ws).
/// Messages are small: { "topics": ["queue", "live", "chats"] }; the dashboard then reloads what it shows.
/// </summary>
public class LiveUpdates(ILogger<LiveUpdates> log)
{
    public const string Queue = "queue"; // review queue and failed downloads
    public const string Live = "live";   // items on the screens and failed placements
    public const string Chats = "chats";

    private sealed record Client(WebSocket Socket, SemaphoreSlim SendLock);
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();

    /// <summary>Keeps the connection open until the dashboard closes it.</summary>
    public async Task HandleAsync(WebSocket socket, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _clients[id] = new Client(socket, new SemaphoreSlim(1, 1));
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct); // the dashboard sends nothing; this just waits for close
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // connection dropped; the dashboard reconnects by itself
        }
        finally
        {
            _clients.TryRemove(id, out _);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
                catch { /* already gone */ }
            }
        }
    }

    /// <summary>Tells all dashboards which parts changed. Never throws and never blocks the caller.</summary>
    public void Publish(params string[] topics)
    {
        if (topics.Length == 0 || _clients.IsEmpty) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { topics }));
        foreach (var (id, client) in _clients)
            _ = SendAsync(id, client, bytes);
    }

    private async Task SendAsync(Guid id, Client client, byte[] bytes)
    {
        await client.SendLock.WaitAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Dropping dashboard connection {Id}", id);
            _clients.TryRemove(id, out _);
        }
        finally
        {
            client.SendLock.Release();
        }
    }
}
