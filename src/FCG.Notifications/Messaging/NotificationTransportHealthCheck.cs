using MassTransit;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace FCG.Notifications.Messaging;

public sealed class NotificationTransportHealthCheck(IBusControl bus, RabbitMqOptions settings) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (bus.CheckHealth().Status != BusHealthStatus.Healthy)
                return Unavailable();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var factory = new ConnectionFactory
            {
                HostName = settings.Host, Port = settings.Port, VirtualHost = settings.VirtualHost,
                UserName = settings.Username, Password = settings.Password, AutomaticRecoveryEnabled = false,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(2), HandshakeContinuationTimeout = TimeSpan.FromSeconds(2)
            };
            await using var connection = await factory.CreateConnectionAsync(timeout.Token);
            return connection.IsOpen ? HealthCheckResult.Healthy() : Unavailable();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
    }

    private static HealthCheckResult Unavailable() => HealthCheckResult.Unhealthy("Notifications message transport is unavailable.");
}
