using FCG.Notifications.Persistence;

namespace FCG.Notifications.Delivery;

public sealed record NotificationDelivery(Guid IntentId, Guid AttemptId, long LeaseVersion, NotificationKind Kind,
    Guid BusinessId, Guid UserId, Guid? GameId, decimal? Price, string MaskedRecipient);

public interface INotificationSink
{
    Task EmitAsync(NotificationDelivery delivery, CancellationToken cancellationToken);
}

public sealed class ConsoleNotificationSink(ILogger<ConsoleNotificationSink> logger) : INotificationSink
{
    public Task EmitAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!logger.IsEnabled(LogLevel.Information))
            throw new InvalidOperationException("Notification emission logging is disabled.");
        logger.LogInformation("[E-MAIL SIMULADO] {Kind} IntentId={IntentId} AttemptId={AttemptId} UserId={UserId} OrderId={OrderId} GameId={GameId} Price={Price} Para={Recipient}",
            delivery.Kind, delivery.IntentId, delivery.AttemptId, delivery.UserId,
            delivery.Kind == NotificationKind.PurchaseConfirmation ? delivery.BusinessId : (Guid?)null,
            delivery.GameId, delivery.Price, delivery.MaskedRecipient);
        return Task.CompletedTask;
    }
}
