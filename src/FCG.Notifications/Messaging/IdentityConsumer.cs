using FCG.Contracts;
using FCG.Notifications.Processing;
using MassTransit;

namespace FCG.Notifications.Messaging;

public sealed class IdentityConsumer<T>(IdentityProjectionHandler handler) : IConsumer<T> where T : IdentitySnapshot
{
    public async Task Consume(ConsumeContext<T> context) =>
        await handler.HandleAsync(context.Message, context.MessageId, context.CancellationToken);
}
