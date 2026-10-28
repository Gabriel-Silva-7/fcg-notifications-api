using FCG.Contracts;
using FCG.Notifications.Processing;
using MassTransit;

namespace FCG.Notifications.Messaging;

public sealed class PaymentNotificationConsumer(PaymentNotificationHandler handler) : IConsumer<PaymentProcessedEvent>
{
    public async Task Consume(ConsumeContext<PaymentProcessedEvent> context) =>
        await handler.HandleAsync(context.Message, context.MessageId, context.CancellationToken);
}
