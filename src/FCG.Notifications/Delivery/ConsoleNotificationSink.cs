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
    public const string WelcomeSubject = "Boas-vindas ao FIAP Cloud Games";
    public const string PurchaseSubject = "Confirmação de compra no FIAP Cloud Games";

    public Task EmitAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!logger.IsEnabled(LogLevel.Information))
            throw new InvalidOperationException("Notification emission logging is disabled.");

        if (delivery.Kind == NotificationKind.Welcome)
        {
            logger.LogInformation(
                "e-mail de boas-vindas enviado para {Recipient}, assunto: {Subject}, {Body}",
                delivery.MaskedRecipient,
                WelcomeSubject,
                "Sua conta no FIAP Cloud Games foi criada com sucesso.");
        }
        else
        {
            logger.LogInformation(
                "e-mail de confirmação de compra enviado para {Recipient}, assunto: {Subject}, {Body}",
                delivery.MaskedRecipient,
                PurchaseSubject,
                $"Sua compra foi confirmada. Pedido: {delivery.BusinessId}. Jogo: {delivery.GameId}. Preço: {delivery.Price}.");
        }

        return Task.CompletedTask;
    }
}
