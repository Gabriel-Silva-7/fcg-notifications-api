using FCG.Contracts;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FCG.Notifications.Persistence;
using FCG.Notifications.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using static FCG.Notifications.Tests.Projection.ContactProjectionTests;

namespace FCG.Notifications.Tests.Projection;

[Collection("notifications")]
public sealed class PaymentNotificationTests(NotificationFixture fixture)
{
    [Fact]
    public async Task Approved_over_real_transport_persists_observation_and_confirmation()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var user = Guid.NewGuid();
        await Apply(app, Snapshot(user, 3, IdentityState.Deleted, "latest@example.com"));
        using var scope = app.Services.CreateScope();
        var message = Payment(user);
        await scope.ServiceProvider.GetRequiredService<IPublishEndpoint>().Publish(message);
        await Eventually(async () => await app.ScalarAsync("SELECT to_regclass('\"PaymentObservations\"')::text") is string);
        await Eventually(async () => Equals(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\"")));
        Assert.Equal("PurchaseConfirmation", await app.ScalarAsync("SELECT \"Kind\" FROM \"NotificationIntents\""));
        Assert.Equal("latest@example.com", await app.ScalarAsync("SELECT \"RecipientEmail\" FROM \"NotificationIntents\""));
        Assert.Equal(3L, await app.ScalarAsync("SELECT \"ContactEventVersion\" FROM \"NotificationIntents\""));
    }

    [Fact]
    public async Task Duplicate_after_restart_keeps_first_observation_id_and_recipient()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var user = Guid.NewGuid();
        await Apply(app, Snapshot(user, 1)); var message = Payment(user); await Handle(app, message);
        var id = await app.ScalarAsync("SELECT \"Id\" FROM \"NotificationIntents\"");
        await Apply(app, Snapshot(user, 2, email: "changed@example.com"));
        await using var restarted = new NotificationFactory(app.ConnectionString, fixture.BrokerSettings); using var restartedClient = restarted.CreateClient();
        Assert.Equal(ProjectionDecision.Ignore, await Handle(restarted, message));
        Assert.Equal(1L, await restarted.ScalarAsync("SELECT count(*) FROM \"PaymentObservations\""));
        Assert.Equal(1L, await restarted.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
        Assert.Equal(id, await restarted.ScalarAsync("SELECT \"Id\" FROM \"NotificationIntents\""));
        Assert.Equal("person@example.com", await restarted.ScalarAsync("SELECT \"RecipientEmail\" FROM \"NotificationIntents\""));
        Assert.Equal(message, await Observation(restarted));
    }

    [Theory]
    [InlineData("Rejected", "Approved")]
    [InlineData("Approved", "Rejected")]
    public async Task Divergent_status_preserves_first_in_either_order(string first, string next)
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var message = Payment(Guid.NewGuid(), first);
        await Handle(app, message);
        var divergent = new PaymentProcessedEvent(message.OrderId, message.UserId, message.GameId, message.Price,
            next, next == "Rejected" ? "AmountAboveLimit" : null, message.ProcessedAtUtc);
        Assert.Equal(ProjectionDecision.Conflict, await Handle(app, divergent));
        await Handle(app, divergent);
        Assert.Equal(message, await Observation(app));
        Assert.Equal(first == "Approved" ? 1L : 0L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.Equal(2, await app.ScalarAsync("SELECT \"Observations\" FROM \"MessageConflicts\""));
        Assert.Equal("PaymentProcessedEvent", await app.ScalarAsync("SELECT \"MessageType\" FROM \"MessageConflicts\""));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("game")]
    [InlineData("price")]
    [InlineData("time")]
    public async Task Divergent_approved_fields_never_retarget_existing_intent(string field)
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var message = Payment(Guid.NewGuid());
        await Apply(app, Snapshot(message.UserId, 1)); await Handle(app, message);
        var divergent = new PaymentProcessedEvent(message.OrderId, field == "user" ? Guid.NewGuid() : message.UserId,
            field == "game" ? Guid.NewGuid() : message.GameId, field == "price" ? 50m : message.Price,
            "Approved", null, field == "time" ? message.ProcessedAtUtc.AddSeconds(1) : message.ProcessedAtUtc);
        Assert.Equal(ProjectionDecision.Conflict, await Handle(app, divergent));
        Assert.Equal(message, await Observation(app));
        Assert.Equal(message.UserId, await app.ScalarAsync("SELECT \"UserId\" FROM \"NotificationIntents\""));
        Assert.Equal(message.GameId, await app.ScalarAsync("SELECT \"GameId\" FROM \"NotificationIntents\""));
        Assert.Equal(message.Price, await app.ScalarAsync("SELECT \"Price\" FROM \"NotificationIntents\""));
        Assert.Equal("person@example.com", await app.ScalarAsync("SELECT \"RecipientEmail\" FROM \"NotificationIntents\""));
    }

    [Fact]
    public async Task Late_identity_prepares_pending_confirmation_using_current_contact_without_welcome()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var message = Payment(Guid.NewGuid());
        await Handle(app, message);
        Assert.Equal("PendingContact", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.Equal(DBNull.Value, await app.ScalarAsync("SELECT \"RecipientEmail\" FROM \"NotificationIntents\""));
        await Apply(app, Snapshot(message.UserId, 3, IdentityState.Deleted, "latest@example.com"));
        Assert.Equal("Ready", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.Equal("latest@example.com", await app.ScalarAsync("SELECT \"RecipientEmail\" FROM \"NotificationIntents\""));
        Assert.Equal(3L, await app.ScalarAsync("SELECT \"ContactEventVersion\" FROM \"NotificationIntents\""));
        await Apply(app, Snapshot(message.UserId, 4, IdentityState.Deleted, "changed@example.com"));
        Assert.Equal("latest@example.com", await app.ScalarAsync("SELECT \"RecipientEmail\" FROM \"NotificationIntents\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
    }

    [Fact]
    public async Task Contact_commits_between_absence_read_and_pending_commit_then_reconciliation_recovers_after_restart()
    {
        var boundary = new PauseBeforeCommit();
        await using var app = await fixture.CreateAsync(services => services.AddDbContext<NotificationsDbContext>(options => options.AddInterceptors(boundary)));
        using var client = app.CreateClient(); var message = Payment(Guid.NewGuid()); boundary.Armed = true;
        var processing = Handle(app, message); await boundary.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Apply(app, Snapshot(message.UserId, 3, IdentityState.Deleted, "latest@example.com"));
        boundary.Resume.SetResult(); await processing;
        Assert.Equal("PendingContact", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        await using var restarted = new NotificationFactory(app.ConnectionString, fixture.BrokerSettings); using var restartedClient = restarted.CreateClient();
        using var scope = restarted.Services.CreateScope(); var reconciler = scope.ServiceProvider.GetRequiredService<PendingContactReconciler>();
        Assert.Equal(1, await reconciler.ReconcileAsync(CancellationToken.None));
        Assert.Equal(0, await reconciler.ReconcileAsync(CancellationToken.None));
        Assert.Equal("Ready", await restarted.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.Equal("latest@example.com", await restarted.ScalarAsync("SELECT \"RecipientEmail\" FROM \"NotificationIntents\""));
    }

    [Fact]
    public async Task Concurrent_copies_create_one_observation_and_intent()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var message = Payment(Guid.NewGuid());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Enumerable.Range(0,6).Select(async _ => { await gate.Task; await Handle(app, message); }).ToArray();
        gate.SetResult(); await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"PaymentObservations\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
    }

    [Fact]
    public async Task Failed_commit_rolls_back_observation_and_intent_before_redelivery()
    {
        var boundary = new PauseBeforeCommit { Fail = true };
        await using var app = await fixture.CreateAsync(services => services.AddDbContext<NotificationsDbContext>(options => options.AddInterceptors(boundary)));
        using var client = app.CreateClient(); var message = Payment(Guid.NewGuid()); boundary.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handle(app, message));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"PaymentObservations\""));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
        await Handle(app, message);
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
    }

    [Fact]
    public async Task Failed_diagnostic_commit_rolls_back_and_redelivery_records_conflict()
    {
        var boundary = new PauseBeforeCommit { Fail = true };
        await using var app = await fixture.CreateAsync(services => services.AddDbContext<NotificationsDbContext>(options => options.AddInterceptors(boundary)));
        using var client = app.CreateClient(); var first = Payment(Guid.NewGuid(), "Rejected"); await Handle(app, first);
        var next = new PaymentProcessedEvent(first.OrderId, first.UserId, first.GameId, first.Price, "Approved", null, first.ProcessedAtUtc);
        boundary.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handle(app, next));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.Equal(first, await Observation(app));
        Assert.Equal(ProjectionDecision.Conflict, await Handle(app, next));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
    }

    [Fact]
    public async Task Invalid_payment_wire_payload_goes_to_error_without_financial_observation()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var settings = fixture.BrokerSettings;
        var factory = new ConnectionFactory { HostName = settings["RabbitMq:Host"]!, Port = int.Parse(settings["RabbitMq:Port"]!),
            VirtualHost = "/fcg", UserName = settings["RabbitMq:Username"]!, Password = settings["RabbitMq:Password"]! };
        await using var connection = await factory.CreateConnectionAsync(); await using var channel = await connection.CreateChannelAsync();
        var errorQueue = app.PaymentsQueue + "_error";
        await channel.QueueDeclareAsync(errorQueue, durable: true, exclusive: false, autoDelete: false);
        var json = JsonSerializer.Serialize(Payment(Guid.NewGuid())).Replace("Approved", "Unknown");
        var envelope = JsonSerializer.Serialize(new { messageId = Guid.NewGuid(), messageType = new[] { "urn:message:FCG.Contracts:PaymentProcessedEvent" },
            message = JsonSerializer.Deserialize<JsonElement>(json), headers = new { } });
        await channel.BasicPublishAsync("FCG.Contracts:PaymentProcessedEvent", "", mandatory: true,
            basicProperties: new BasicProperties { ContentType = "application/vnd.masstransit+json" }, body: Encoding.UTF8.GetBytes(envelope));
        BasicGetResult? failed = null;
        await Eventually(async () => (failed = await channel.BasicGetAsync(errorQueue, autoAck: false)) is not null);
        await channel.BasicAckAsync(failed!.DeliveryTag, multiple: false);
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"PaymentObservations\""));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
        Assert.DoesNotContain("   at ", string.Join('\n', app.Logs.Entries));
    }

    [Fact]
    public async Task Identity_diagnostics_keep_concrete_event_type()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var id = Guid.NewGuid();
        await Apply(app, Snapshot(id, 1));
        var registration = WelcomeIntentTests.Created(id) with { Email = "divergent@example.com" };
        await Apply(app, registration);
        Assert.Equal("UserCreatedEvent", await app.ScalarAsync("SELECT \"MessageType\" FROM \"MessageConflicts\""));
    }

    [Fact]
    public async Task Unresolved_contacts_cannot_starve_later_recoverable_pending_intents()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var id = Guid.NewGuid();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var old = DateTimeOffset.Parse("2026-10-28T10:00:00Z");
            db.NotificationIntents.AddRange(Enumerable.Range(0,100).Select(_ => new NotificationIntent
            { Id = Guid.NewGuid(), Kind = NotificationKind.PurchaseConfirmation, BusinessId = Guid.NewGuid(), UserId = Guid.NewGuid(), CreatedAtUtc = old }));
            db.NotificationIntents.Add(new NotificationIntent { Id = Guid.NewGuid(), Kind = NotificationKind.PurchaseConfirmation,
                BusinessId = Guid.NewGuid(), UserId = id, CreatedAtUtc = old.AddSeconds(1) });
            var contact = new UserContact(); contact.Apply(Snapshot(id, 1)); db.UserContacts.Add(contact); await db.SaveChangesAsync();
        }
        using var reconciliation = app.Services.CreateScope();
        Assert.Equal(1, await reconciliation.ServiceProvider.GetRequiredService<PendingContactReconciler>().ReconcileAsync(CancellationToken.None));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\" WHERE \"State\" = 'Ready'"));
    }

    private sealed class PauseBeforeCommit : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Fail { get; init; }
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result; Armed = false;
            if (Fail) throw new InvalidOperationException("Synthetic failure after save.");
            Reached.SetResult(); await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); return result;
        }
    }

    private static async Task<PaymentProcessedEvent> Observation(NotificationFactory app)
    {
        using var scope = app.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().PaymentObservations.AsNoTracking().SingleAsync()).Snapshot();
    }
    internal static async Task<ProjectionDecision> Handle(NotificationFactory app, PaymentProcessedEvent message)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentNotificationHandler>().HandleAsync(message, Guid.NewGuid(), CancellationToken.None);
    }

    internal static PaymentProcessedEvent Payment(Guid user, string status = "Approved", Guid? order = null) =>
        new(order ?? Guid.NewGuid(), user, Guid.NewGuid(), 49.90m, status, status == "Rejected" ? "AmountAboveLimit" : null,
            DateTimeOffset.Parse("2026-10-28T12:00:00Z"));
}
