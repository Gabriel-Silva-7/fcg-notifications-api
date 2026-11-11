using FCG.Contracts;
using Xunit;
using FCG.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;
using static FCG.Notifications.Tests.Projection.ContactProjectionTests;

namespace FCG.Notifications.Tests.Projection;

[Collection("notifications")]
public sealed class WelcomeIntentTests(NotificationFixture fixture)
{
    [Fact]
    public async Task Registration_persists_a_durable_welcome_intent()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        await Apply(app, Created(Guid.NewGuid()));
        Assert.IsType<string>(await app.ScalarAsync("SELECT to_regclass('\"NotificationIntents\"')::text"));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\" WHERE \"Kind\" = 'Welcome'"));
        Assert.Equal("Ready", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.Equal("person@example.com", await app.ScalarAsync("SELECT \"RecipientEmail\" FROM \"NotificationIntents\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT \"ContactEventVersion\" FROM \"NotificationIntents\""));
        Assert.DoesNotContain("assunto:", string.Join('\n', app.Logs.Entries));
    }

    [Fact]
    public async Task Duplicate_facts_with_other_ids_and_restart_keep_one_stable_intent_and_frozen_contact()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var created = Created(Guid.NewGuid());
        await Apply(app, created);
        var intentId = await app.ScalarAsync("SELECT \"Id\" FROM \"NotificationIntents\"");
        await Apply(app, created with { EventId = Guid.NewGuid() });
        await Apply(app, Snapshot(created.UserId, 2, email: "changed@example.com"));
        await using var restarted = new NotificationFactory(app.ConnectionString, fixture.BrokerSettings); using var restartedClient = restarted.CreateClient();
        await Apply(restarted, created with { EventId = Guid.NewGuid() });
        Assert.Equal(1L, await restarted.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
        Assert.Equal(intentId, await restarted.ScalarAsync("SELECT \"Id\" FROM \"NotificationIntents\""));
        Assert.Equal("person@example.com", await restarted.ScalarAsync("SELECT \"RecipientEmail\" FROM \"NotificationIntents\""));
        Assert.Equal(1L, await restarted.ScalarAsync("SELECT \"ContactEventVersion\" FROM \"NotificationIntents\""));
    }

    [Fact]
    public async Task Snapshot_alone_never_creates_welcome_but_delayed_registration_uses_current_deleted_contact()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var id = Guid.NewGuid();
        await Apply(app, Snapshot(id, 3, IdentityState.Deleted, "latest@example.com"));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
        await Apply(app, Created(id));
        Assert.Equal(3, (await Contact(app, id)).EventVersion);
        Assert.Equal(IdentityState.Deleted, (await Contact(app, id)).State);
        Assert.Equal("latest@example.com", await app.ScalarAsync("SELECT \"RecipientEmail\" FROM \"NotificationIntents\""));
        Assert.Equal(3L, await app.ScalarAsync("SELECT \"ContactEventVersion\" FROM \"NotificationIntents\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
    }

    [Fact]
    public async Task Conflicting_registration_does_not_create_welcome()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var id = Guid.NewGuid();
        await Apply(app, Snapshot(id, 1));
        Assert.Equal(ProjectionDecision.Conflict, await Apply(app, Created(id) with { Email = "divergent@example.com" }));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"MessageConflicts\""));
    }

    [Fact]
    public async Task Six_concurrent_registration_copies_have_one_business_intent()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var id = Guid.NewGuid();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 6).Select(async _ => { await release.Task; return await Apply(app, Created(id)); }).ToArray();
        release.SetResult(); await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"UserContacts\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
        Assert.Equal(true, await app.ScalarAsync("SELECT indisunique FROM pg_index WHERE indexrelid = '\"IX_NotificationIntents_Kind_BusinessId\"'::regclass"));
    }

    [Fact]
    public async Task Failure_after_save_rolls_back_contact_and_intent_and_allows_redelivery()
    {
        var failure = new FailCommitOnce();
        await using var app = await fixture.CreateAsync(services => services.AddDbContext<NotificationsDbContext>(options => options.AddInterceptors(failure)));
        using var client = app.CreateClient(); var message = Created(Guid.NewGuid()); failure.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(app, message));
        Assert.True(failure.SawBothWrites);
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"UserContacts\""));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
        await Apply(app, message);
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
    }

    private sealed class FailCommitOnce : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool SawBothWrites { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result; Armed = false;
            await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT (SELECT count(*) FROM \"UserContacts\") = 1 AND (SELECT count(*) FROM \"NotificationIntents\") = 1";
            SawBothWrites = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
            throw new InvalidOperationException("Synthetic failure after SaveChanges.");
        }
    }

    internal static UserCreatedEvent Created(Guid id) => new()
    {
        UserId = id, EventId = Guid.NewGuid(), EventVersion = 1,
        OccurredAtUtc = DateTimeOffset.Parse("2026-10-28T11:00:00Z"), CreatedAtUtc = DateTimeOffset.Parse("2026-10-28T11:00:00Z"),
        Name = "Person", Email = "person@example.com", Role = IdentityRole.User, State = IdentityState.Active
    };
}
