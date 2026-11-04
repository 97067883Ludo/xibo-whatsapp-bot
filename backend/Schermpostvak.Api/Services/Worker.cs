namespace Schermpostvak.Api.Services;

/// <summary>Runs the media processor every few seconds.</summary>
public class Worker(IServiceScopeFactory scopes, ILogger<Worker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
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
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
