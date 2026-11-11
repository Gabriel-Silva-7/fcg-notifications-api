using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using FCG.Contracts;
using FCG.Notifications.Delivery;
using FCG.Notifications.Persistence;
using FCG.Notifications.Processing;
using FCG.Notifications.Tests.Projection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static FCG.Notifications.Tests.Projection.ContactProjectionTests;

namespace FCG.Notifications.Tests.Recovery;

[Collection("notifications")]
public sealed class NotificationRecoveryTests(NotificationFixture fixture)
{
    [Fact]
    public async Task Payment_conflict_stays_terminal_until_owner_restore_then_resolves_once_with_intent_and_audit()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var id = await PaymentConflict(app);
        var before = await Fingerprint(app);
        Assert.Equal("Quarantined", (await Preview(app, id)).Outcome); Assert.Equal(before, await Fingerprint(app));
        Assert.Equal("Quarantined", (await Replay(app, id)).Outcome);
        Assert.Equal("Rejected", await app.ScalarAsync("SELECT \"Status\" FROM \"PaymentObservations\""));
        await RestoreBeforeObservation(app); before = await Fingerprint(app);
        var preview = await Preview(app, id); Assert.Equal("Apply", preview.Outcome); Assert.Contains("CreatePurchaseConfirmationIntent", preview.PlannedEffects!);
        Assert.Equal(before, await Fingerprint(app));
        Assert.Equal("Apply", (await Replay(app, id)).Outcome); Assert.Equal("AlreadyResolved", (await Replay(app, id)).Outcome);
        Assert.Equal("Approved", await app.ScalarAsync("SELECT \"Status\" FROM \"PaymentObservations\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\" WHERE \"Kind\"='PurchaseConfirmation'"));
        Assert.Equal(3L, await app.ScalarAsync("SELECT count(*) FROM \"RecoveryAttempts\""));
        Assert.Equal(true, await app.ScalarAsync("SELECT \"Resolved\" FROM \"MessageConflicts\""));
        Assert.DoesNotContain(app.Logs.Entries, line => line.Contains("[E-MAIL SIMULADO]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Identity_replay_preserves_registration_fact_and_snapshot_never_creates_welcome(bool registration)
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var user = Guid.NewGuid();
        await Apply(app, Snapshot(user, 1, email: "first@example.com"));
        IdentitySnapshot incoming = registration ? WelcomeIntentTests.Created(user) : Snapshot(user, 1, email: "second@example.com");
        Assert.Equal(ProjectionDecision.Conflict, await Apply(app, incoming));
        var id = Assert.IsType<Guid>(await app.ScalarAsync("SELECT \"Id\" FROM \"MessageConflicts\""));
        // Owner-authorized partial projection restore, separate from normal replay and broker persistence cases.
        await Execute(app, "DELETE FROM \"UserContacts\"");
        var before = await Fingerprint(app); var preview = await Preview(app, id);
        Assert.Equal("Apply", preview.Outcome); Assert.Equal(registration, preview.PlannedEffects!.Contains("CreateWelcomeIntent"));
        Assert.Equal(before, await Fingerprint(app));
        Assert.Equal("Apply", (await Replay(app, id)).Outcome); Assert.Equal("AlreadyResolved", (await Replay(app, id)).Outcome);
        Assert.Equal(registration ? 1L : 0L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\" WHERE \"Kind\"='Welcome'"));
        Assert.Equal(incoming.Email, await app.ScalarAsync("SELECT \"Email\" FROM \"UserContacts\""));
    }

    [Fact]
    public async Task Concurrent_replays_have_one_effect_and_independent_audited_attempts()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var id = await PaymentConflict(app); await RestoreBeforeObservation(app);
        var result = await Task.WhenAll(Replay(app, id), Replay(app, id));
        Assert.Equal(new[] { "AlreadyResolved", "Apply" }, result.Select(x => x.Outcome).Order());
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
        Assert.Equal(2L, await app.ScalarAsync("SELECT count(*) FROM \"RecoveryAttempts\""));
    }

    [Fact]
    public async Task Effect_resolution_and_operator_history_roll_back_together_on_commit_failure()
    {
        var fault = new FailRecoveryCommit();
        await using var app = await fixture.CreateAsync(services => services.AddDbContext<NotificationsDbContext>(options => options.AddInterceptors(fault)));
        using var client = app.CreateClient(); var id = await PaymentConflict(app); await RestoreBeforeObservation(app); fault.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Replay(app, id)); Assert.True(fault.SawAtomicWrites);
        Assert.Equal(false, await app.ScalarAsync("SELECT \"Resolved\" FROM \"MessageConflicts\""));
        foreach (var table in new[] { "PaymentObservations", "NotificationIntents", "RecoveryAttempts" }) Assert.Equal(0L, await app.ScalarAsync($"SELECT count(*) FROM \"{table}\""));
        Assert.Equal("Apply", (await Replay(app, id)).Outcome);
    }

    [Theory]
    [InlineData("type", "UnsupportedMessageType")]
    [InlineData("hash", "InvalidStoredPayload")]
    [InlineData("metadata", "InvalidStoredPayload")]
    public async Task Unsupported_or_tampered_payload_never_overwrites_state(string change, string expected)
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var id = await PaymentConflict(app);
        await Execute(app, change == "type" ? "UPDATE \"MessageConflicts\" SET \"MessageType\"='Unsupported'" : change == "hash"
            ? "UPDATE \"MessageConflicts\" SET \"PayloadHash\"='tampered'" : $"UPDATE \"MessageConflicts\" SET \"BusinessId\"='{Guid.NewGuid()}'");
        var before = await Fingerprint(app); Assert.Equal(expected, (await Preview(app, id)).Reason); Assert.Equal(before, await Fingerprint(app));
        Assert.Equal(expected, (await Replay(app, id)).Reason);
        Assert.Equal("Rejected", await app.ScalarAsync("SELECT \"Status\" FROM \"PaymentObservations\""));
        Assert.Equal(false, await app.ScalarAsync("SELECT \"Resolved\" FROM \"MessageConflicts\""));
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("Rejected")]
    public async Task Missing_observation_with_existing_confirmation_is_inconsistent_restore_and_cannot_select_another_terminal(string status)
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        var original = PaymentNotificationTests.Payment(Guid.NewGuid());
        await PaymentNotificationTests.Handle(app, original);
        await PaymentNotificationTests.Handle(app, new PaymentProcessedEvent(original.OrderId, original.UserId, original.GameId, original.Price, status, status == "Rejected" ? "AmountAboveLimit" : null, original.ProcessedAtUtc.AddSeconds(1)));
        var id = Assert.IsType<Guid>(await app.ScalarAsync("SELECT \"Id\" FROM \"MessageConflicts\""));
        await RestoreBeforeObservation(app);
        var before = await Fingerprint(app); var preview = await Preview(app, id);
        Assert.Equal("Quarantined", preview.Outcome); Assert.Equal("ExistingIntentWithoutPaymentObservation", preview.Reason);
        Assert.Equal(before, await Fingerprint(app));
        var applied = await Replay(app, id); Assert.Equal("Quarantined", applied.Outcome); Assert.Equal("ExistingIntentWithoutPaymentObservation", applied.Reason);
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"PaymentObservations\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
        Assert.Equal(false, await app.ScalarAsync("SELECT \"Resolved\" FROM \"MessageConflicts\" ORDER BY \"FirstSeenAtUtc\" LIMIT 1"));
    }

    [Fact]
    public async Task Resume_advances_backoff_but_cannot_steal_active_lease_or_reemit_sent_intent()
    {
        var clock = new ManualClock(); await using var app = await fixture.CreateAsync(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        using var client = app.CreateClient(); await Apply(app, WelcomeIntentTests.Created(Guid.NewGuid()));
        var id = Assert.IsType<Guid>(await app.ScalarAsync("SELECT \"Id\" FROM \"NotificationIntents\""));
        using (var scope = app.Services.CreateScope())
        { var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>(); (await db.NotificationIntents.SingleAsync()).NextAttemptAtUtc = clock.GetUtcNow().AddHours(1); await db.SaveChangesAsync(); }
        var before = await Fingerprint(app); Assert.Equal("Rescheduled", (await Resume(app, id, false)).Outcome); Assert.Equal(before, await Fingerprint(app));
        Assert.Equal("Rescheduled", (await Resume(app, id, true)).Outcome);
        using var worker = app.Services.CreateScope(); var dispatcher = worker.ServiceProvider.GetRequiredService<NotificationDispatcher>();
        var delivery = Assert.IsType<NotificationDelivery>(await dispatcher.ClaimAsync(CancellationToken.None));
        var intentBefore = await IntentFingerprint(app);
        Assert.Equal("LeaseActive", (await Resume(app, id, true)).Outcome); Assert.Equal(intentBefore, await IntentFingerprint(app));
        await worker.ServiceProvider.GetRequiredService<INotificationSink>().EmitAsync(delivery, CancellationToken.None);
        Assert.True(await dispatcher.CompleteAsync(delivery, CancellationToken.None)); intentBefore = await IntentFingerprint(app);
        Assert.Equal("AlreadySent", (await Resume(app, id, true)).Outcome); Assert.Equal(intentBefore, await IntentFingerprint(app));
        Assert.Single(app.Logs.Entries, line => line.Contains("[E-MAIL SIMULADO]", StringComparison.Ordinal));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationAttempts\""));
    }

    [Fact]
    public async Task Expired_lease_remains_for_dispatcher_to_record_abandoned_and_old_owner_cannot_finish()
    {
        var clock = new ManualClock(); await using var app = await fixture.CreateAsync(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        using var client = app.CreateClient(); await Apply(app, WelcomeIntentTests.Created(Guid.NewGuid()));
        using var worker = app.Services.CreateScope(); var dispatcher = worker.ServiceProvider.GetRequiredService<NotificationDispatcher>();
        var old = Assert.IsType<NotificationDelivery>(await dispatcher.ClaimAsync(CancellationToken.None)); clock.Advance(TimeSpan.FromSeconds(31));
        var before = await IntentFingerprint(app); Assert.Equal("AlreadyEligible", (await Resume(app, old.IntentId, true)).Outcome); Assert.Equal(before, await IntentFingerprint(app));
        Assert.False(await dispatcher.CompleteAsync(old, CancellationToken.None));
        var next = Assert.IsType<NotificationDelivery>(await dispatcher.ClaimAsync(CancellationToken.None));
        Assert.False(await dispatcher.CompleteAsync(old, CancellationToken.None));
        Assert.Equal("Abandoned", await app.ScalarAsync($"SELECT \"Result\" FROM \"NotificationAttempts\" WHERE \"Id\"='{old.AttemptId}'"));
        await worker.ServiceProvider.GetRequiredService<INotificationSink>().EmitAsync(next, CancellationToken.None);
        Assert.True(await dispatcher.CompleteAsync(next, CancellationToken.None));
    }

    [Fact]
    public async Task Pending_contact_can_resume_from_owner_projection_restore_without_new_event_or_early_sent()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var user = Guid.NewGuid();
        await PaymentNotificationTests.Handle(app, PaymentNotificationTests.Payment(user));
        var id = Assert.IsType<Guid>(await app.ScalarAsync("SELECT \"Id\" FROM \"NotificationIntents\""));
        Assert.Equal("WaitingForContact", (await Resume(app, id, false)).Outcome);
        Assert.Equal("WaitingForContact", (await Resume(app, id, true)).Outcome);
        using (var scope = app.Services.CreateScope())
        { var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>(); var contact = new UserContact(); contact.Apply(Snapshot(user, 3, IdentityState.Deleted, "retained@example.com")); db.UserContacts.Add(contact); await db.SaveChangesAsync(); }
        var before = await Fingerprint(app); Assert.Equal("Prepared", (await Resume(app, id, false)).Outcome); Assert.Equal(before, await Fingerprint(app));
        Assert.Equal("Prepared", (await Resume(app, id, true)).Outcome);
        Assert.Equal("Ready", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.Equal(3L, await app.ScalarAsync("SELECT \"ContactEventVersion\" FROM \"NotificationIntents\""));
        Assert.DoesNotContain(app.Logs.Entries, line => line.Contains("[E-MAIL SIMULADO]", StringComparison.Ordinal));
        using var worker = app.Services.CreateScope(); Assert.True(await worker.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchNextAsync(CancellationToken.None));
        Assert.Equal("Sent", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.DoesNotContain("retained@example.com", string.Join('\n', app.Logs.Entries));
    }

    internal static async Task<Guid> PaymentConflict(NotificationFactory app)
    {
        var original = PaymentNotificationTests.Payment(Guid.NewGuid(), "Rejected");
        await PaymentNotificationTests.Handle(app, original);
        await PaymentNotificationTests.Handle(app, new PaymentProcessedEvent(original.OrderId, original.UserId, original.GameId, original.Price, "Approved", null, original.ProcessedAtUtc));
        return Assert.IsType<Guid>(await app.ScalarAsync("SELECT \"Id\" FROM \"MessageConflicts\""));
    }
    internal static Task RestoreBeforeObservation(NotificationFactory app) => Execute(app, "DELETE FROM \"PaymentObservations\"");
    internal static async Task Execute(NotificationFactory app, string sql)
    { using var scope = app.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.ExecuteSqlRawAsync(sql); }
    internal static async Task<NotificationRecoveryResult> Preview(NotificationFactory app, Guid id)
    { using var scope = app.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<NotificationRecoveryService>().PreviewConflictAsync(id, CancellationToken.None); }
    internal static async Task<NotificationRecoveryResult> Replay(NotificationFactory app, Guid id)
    { using var scope = app.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<NotificationRecoveryService>().ReplayConflictAsync(id, CancellationToken.None); }
    internal static async Task<NotificationRecoveryResult> Resume(NotificationFactory app, Guid id, bool apply)
    { using var scope = app.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<NotificationRecoveryService>().ResumeIntentAsync(id, apply, CancellationToken.None); }
    internal static async Task<string> Fingerprint(NotificationFactory app)
    {
        var values = new List<string>();
        foreach (var table in new[] { "UserContacts", "PaymentObservations", "NotificationIntents", "NotificationAttempts", "MessageConflicts", "RecoveryAttempts" })
            values.Add(Assert.IsType<string>(await app.ScalarAsync($"SELECT coalesce(json_agg(t)::text,'[]') FROM (SELECT *,xmin::text row_token FROM \"{table}\" ORDER BY ctid)t")));
        return string.Join('|', values);
    }
    private static async Task<string> IntentFingerprint(NotificationFactory app) => Assert.IsType<string>(await app.ScalarAsync("SELECT json_agg(t)::text FROM (SELECT *,xmin::text row_token FROM \"NotificationIntents\" ORDER BY ctid)t"));
    private sealed class ManualClock : TimeProvider
    { private DateTimeOffset now = DateTimeOffset.Parse("2030-01-01T00:00:00Z"); public override DateTimeOffset GetUtcNow() => now; public void Advance(TimeSpan delta) => now += delta; }
    private sealed class FailRecoveryCommit : DbTransactionInterceptor
    {
        public bool Armed { get; set; } public bool SawAtomicWrites { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT (SELECT count(*) FROM \"RecoveryAttempts\")=1 AND (SELECT count(*) FROM \"MessageConflicts\" WHERE \"Resolved\")=1 AND (SELECT count(*) FROM \"PaymentObservations\" WHERE \"Status\"='Approved')=1 AND (SELECT count(*) FROM \"NotificationIntents\")=1";
            if (!(bool)(await command.ExecuteScalarAsync(cancellationToken))!) return result;
            Armed = false; SawAtomicWrites = true; throw new InvalidOperationException("PRIVATE recovery commit fault.");
        }
    }
}
