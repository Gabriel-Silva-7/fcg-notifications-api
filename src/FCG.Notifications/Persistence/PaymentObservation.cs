using FCG.Contracts;

namespace FCG.Notifications.Persistence;

public sealed class PaymentObservation
{
    public Guid OrderId { get; set; }
    public Guid UserId { get; set; }
    public Guid GameId { get; set; }
    public decimal Price { get; set; }
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
    public PaymentProcessedEvent Snapshot() => new(OrderId, UserId, GameId, Price, Status, Reason, ProcessedAtUtc);
    public static PaymentObservation From(PaymentProcessedEvent message) => new()
    { OrderId = message.OrderId, UserId = message.UserId, GameId = message.GameId, Price = message.Price,
      Status = message.Status, Reason = message.Reason, ProcessedAtUtc = message.ProcessedAtUtc };
}
