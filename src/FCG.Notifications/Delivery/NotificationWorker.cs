using FCG.Notifications.Processing;

namespace FCG.Notifications.Delivery;

public sealed class NotificationWorker(IServiceScopeFactory scopes, DispatcherOptions options, TimeProvider clock,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PendingContactReconciler>().ReconcileAsync(stoppingToken);
                var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();
                for (var i = 0; i < 25 && await dispatcher.DispatchNextAsync(stoppingToken); i++) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("NotificationDispatcherUnavailable"); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
