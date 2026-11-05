namespace Schermpostvak.Api.Services;

/// <summary>Lets the webhook and the dashboard start a worker pass right away instead of waiting for the next tick.</summary>
public class WorkerSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* a pass is already pending */ }
    }

    /// <summary>Returns when woken or after the timeout, whichever comes first.</summary>
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>Runs the media processor every few seconds, or immediately when woken.</summary>
public class Worker(IServiceScopeFactory scopes, WorkerSignal signal, ILogger<Worker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MediaProcessor>().RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Worker pass failed");
            }

            try { await signal.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
