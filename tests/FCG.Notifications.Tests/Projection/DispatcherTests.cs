using Xunit;
using FCG.Notifications.Delivery;
using FCG.Notifications.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using MassTransit;
using static FCG.Notifications.Tests.Projection.ContactProjectionTests;

namespace FCG.Notifications.Tests.Projection;

[Collection("notifications")]
public sealed class DispatcherTests(NotificationFixture fixture)
{
    [Fact]
    public async Task Hosted_dispatcher_emits_welcome_and_marks_sent_after_console()
    {
        await using var app = await fixture.CreateAsync(); app.RunDispatcher = true; using var client = app.CreateClient();
        await Apply(app, WelcomeIntentTests.Created(Guid.NewGuid()));
        await Eventually(() => Task.FromResult(app.Logs.Entries.Any(x => x.Contains("[E-MAIL SIMULADO]"))));
        await Eventually(async () => Equals("Sent", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\"")));
        Assert.Equal("Sent", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.DoesNotContain("person@example.com", string.Join('\n', app.Logs.Entries));
    }
    [Fact]
    public async Task Hosted_worker_recovers_pending_contact_on_restart_without_another_identity_event()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var user = Guid.NewGuid();
        await PaymentNotificationTests.Handle(app, PaymentNotificationTests.Payment(user));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var contact = new UserContact(); contact.Apply(Snapshot(user, 3, FCG.Contracts.IdentityState.Deleted, "latest@example.com"));
            db.UserContacts.Add(contact); await db.SaveChangesAsync();
        }
        await using var restarted = new NotificationFactory(app.ConnectionString, fixture.BrokerSettings) { RunDispatcher = true };
        using var restartedClient = restarted.CreateClient();
        await Eventually(async () => Equals("Sent", await restarted.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\"")));
        var emitted = Assert.Single(restarted.Logs.Entries, x => x.Contains("[E-MAIL SIMULADO]"));
        Assert.Contains("PurchaseConfirmation", emitted); Assert.Contains("l***@example.com", emitted);
        Assert.DoesNotContain("latest@example.com", emitted);
        Assert.Equal(3L, await restarted.ScalarAsync("SELECT \"ContactEventVersion\" FROM \"NotificationIntents\""));
    }

    [Fact]
    public async Task Concurrent_workers_only_claim_one_valid_lease_and_old_owner_cannot_complete_new_lease()
    {
        var clock = new ManualClock(); await using var app = await fixture.CreateAsync(services => UseClock(services, clock)); using var client = app.CreateClient();
        await Apply(app, WelcomeIntentTests.Created(Guid.NewGuid()));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 6).Select(async _ => { await gate.Task; using var scope = app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().ClaimAsync(CancellationToken.None); }).ToArray();
        gate.SetResult(); var leases = await Task.WhenAll(tasks); var old = Assert.Single(leases.OfType<NotificationDelivery>());
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationAttempts\""));
        clock.Advance(TimeSpan.FromSeconds(31));
        using var nextScope = app.Services.CreateScope(); var next = nextScope.ServiceProvider.GetRequiredService<NotificationDispatcher>();
        Assert.False(await next.CompleteAsync(old, CancellationToken.None));
        var current = Assert.IsType<NotificationDelivery>(await next.ClaimAsync(CancellationToken.None));
        Assert.Equal(old.IntentId, current.IntentId); Assert.NotEqual(old.AttemptId, current.AttemptId); Assert.Equal(2, current.LeaseVersion);
        Assert.False(await next.CompleteAsync(old, CancellationToken.None));
        Assert.Equal("Sending", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.True(await next.CompleteAsync(current, CancellationToken.None));
        Assert.Equal("Abandoned", await app.ScalarAsync($"SELECT \"Result\" FROM \"NotificationAttempts\" WHERE \"Id\" = '{old.AttemptId}'"));
        Assert.Equal("Sent", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.Null(await next.ClaimAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Crash_after_actual_console_before_progress_can_repeat_same_intent_after_lease_expiry()
    {
        var clock = new ManualClock(); var failure = new FailCompletionOnce();
        await using var app = await fixture.CreateAsync(services =>
        {
            UseClock(services, clock); services.AddDbContext<NotificationsDbContext>(options => options.AddInterceptors(failure));
            services.AddSingleton<INotificationSink>(sp => new AfterConsoleFailure(
                new ConsoleNotificationSink(sp.GetRequiredService<ILogger<ConsoleNotificationSink>>()), failure));
        });
        using var client = app.CreateClient(); await Apply(app, WelcomeIntentTests.Created(Guid.NewGuid()));
        using (var scope = app.Services.CreateScope())
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchNextAsync(CancellationToken.None));
        Assert.Single(app.Logs.Entries, x => x.Contains("[E-MAIL SIMULADO]"));
        Assert.Equal("Sending", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.Equal(DBNull.Value, await app.ScalarAsync("SELECT \"SentAtUtc\" FROM \"NotificationIntents\""));
        var stableId = await app.ScalarAsync("SELECT \"Id\" FROM \"NotificationIntents\"");
        clock.Advance(TimeSpan.FromSeconds(31));
        using (var scope = app.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchNextAsync(CancellationToken.None));
        var logs = app.Logs.Entries.Where(x => x.Contains("[E-MAIL SIMULADO]")).ToArray(); Assert.Equal(2, logs.Length);
        Assert.All(logs, entry => Assert.Contains(stableId!.ToString()!, entry)); Assert.NotEqual(logs[0], logs[1]);
        Assert.Equal("Sent", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.Equal(2L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationAttempts\""));
    }

    [Fact]
    public async Task Failure_before_console_keeps_safe_backoff_then_retries_with_frozen_recipient()
    {
        var clock = new ManualClock();
        await using var app = await fixture.CreateAsync(services =>
        {
            UseClock(services, clock);
            services.AddSingleton<INotificationSink>(sp => new FailBeforeConsoleOnce(
                new ConsoleNotificationSink(sp.GetRequiredService<ILogger<ConsoleNotificationSink>>())));
        });
        using var client = app.CreateClient(); var user = Guid.NewGuid(); await Apply(app, WelcomeIntentTests.Created(user));
        using var scope = app.Services.CreateScope(); var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();
        Assert.True(await dispatcher.DispatchNextAsync(CancellationToken.None));
        Assert.DoesNotContain(app.Logs.Entries, x => x.Contains("[E-MAIL SIMULADO]"));
        Assert.Equal("Ready", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.Equal("ConsoleEmissionFailed", await app.ScalarAsync("SELECT \"LastFailureCode\" FROM \"NotificationIntents\""));
        Assert.False(await dispatcher.DispatchNextAsync(CancellationToken.None));
        await Apply(app, Snapshot(user, 3, FCG.Contracts.IdentityState.Deleted, "latest@example.com"));
        clock.Advance(TimeSpan.FromSeconds(6)); Assert.True(await dispatcher.DispatchNextAsync(CancellationToken.None));
        var emitted = Assert.Single(app.Logs.Entries, x => x.Contains("[E-MAIL SIMULADO]"));
        Assert.Contains("p***@example.com", emitted); Assert.DoesNotContain("l***@example.com", emitted);
        Assert.DoesNotContain("secret", string.Join('\n', app.Logs.Entries));
        Assert.DoesNotContain("person@example.com", string.Join('\n', app.Logs.Entries));
        Assert.Equal("Sent", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
    }

    [Fact]
    public async Task Cancellation_after_claim_leaves_recoverable_lease_without_fictitious_sent()
    {
        var clock = new ManualClock(); using var stop = new CancellationTokenSource();
        await using var app = await fixture.CreateAsync(services =>
        {
            UseClock(services, clock); services.AddSingleton<INotificationSink>(new CancelBeforeConsole(stop));
        });
        using var client = app.CreateClient(); await Apply(app, WelcomeIntentTests.Created(Guid.NewGuid()));
        using (var scope = app.Services.CreateScope())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchNextAsync(stop.Token));
        Assert.Equal("Sending", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.DoesNotContain(app.Logs.Entries, x => x.Contains("[E-MAIL SIMULADO]"));
        clock.Advance(TimeSpan.FromSeconds(31));
        await using var restarted = new NotificationFactory(app.ConnectionString, fixture.BrokerSettings, services => UseClock(services, clock));
        using var restartedClient = restarted.CreateClient(); using var retry = restarted.Services.CreateScope();
        Assert.True(await retry.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchNextAsync(CancellationToken.None));
        Assert.Equal("Sent", await restarted.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
    }

    [Fact]
    public async Task Disabled_emission_logging_cannot_falsely_mark_intent_sent()
    {
        await using var app = await fixture.CreateAsync(services => services.AddLogging(logging =>
            logging.AddFilter("FCG.Notifications.Delivery.ConsoleNotificationSink", LogLevel.None)));
        using var client = app.CreateClient(); await Apply(app, WelcomeIntentTests.Created(Guid.NewGuid()));
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchNextAsync(CancellationToken.None);
        Assert.Equal("Ready", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.Equal(DBNull.Value, await app.ScalarAsync("SELECT \"SentAtUtc\" FROM \"NotificationIntents\""));
    }

    [Theory]
    [InlineData("gabriel@example.com", "g***@example.com")]
    [InlineData("b@gmail.com", "***@gmail.com")]
    [InlineData("a@example.com\nInjected", "***")]
    [InlineData("invalid", "***")]
    [InlineData(null, "***")]
    public void Mask_preserves_inherited_policy_and_never_echoes_untrusted_input(string? email, string expected) =>
        Assert.Equal(expected, SensitiveDataMasker.MaskEmail(email));

    [Theory]
    [InlineData(0, 30, 5)]
    [InlineData(1, 0, 5)]
    [InlineData(1, 30, 0)]
    [InlineData(61, 30, 5)]
    public void Invalid_intervals_are_rejected(int poll, int lease, int backoff) =>
        Assert.Throws<InvalidOperationException>(() => new DispatcherOptions { PollSeconds = poll, LeaseSeconds = lease, BackoffSeconds = backoff }.Validate());

    private static void UseClock(IServiceCollection services, TimeProvider clock)
    { services.RemoveAll<TimeProvider>(); services.AddSingleton(clock); }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-28T13:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
    private sealed class FailCompletionOnce : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed) { Armed = false; throw new InvalidOperationException("Synthetic failure after console."); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class AfterConsoleFailure(INotificationSink inner, FailCompletionOnce failure) : INotificationSink
    {
        private bool _first = true;
        public async Task EmitAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
        {
            await inner.EmitAsync(delivery, cancellationToken);
            if (_first) { _first = false; failure.Armed = true; }
        }
    }
    private sealed class FailBeforeConsoleOnce(INotificationSink inner) : INotificationSink
    {
        private bool _first = true;
        public Task EmitAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
        {
            if (_first) { _first = false; throw new InvalidOperationException("secret person@example.com"); }
            return inner.EmitAsync(delivery, cancellationToken);
        }
    }
    private sealed class CancelBeforeConsole(CancellationTokenSource stop) : INotificationSink
    {
        public Task EmitAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
        { stop.Cancel(); cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
