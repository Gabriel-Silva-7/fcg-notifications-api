using Xunit;
using FCG.Contracts;
using FCG.Notifications.Persistence;
using FCG.Notifications.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MassTransit;
using System.Net;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace FCG.Notifications.Tests.Projection;

[Collection("notifications")]
public sealed class ContactProjectionTests(NotificationFixture fixture)
{
    [Fact]
    public async Task Own_database_has_durable_contacts_schema()
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        Assert.IsType<string>(await app.ScalarAsync("SELECT to_regclass('\"UserContacts\"')::text"));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"UserContacts\""));
    }

    [Fact]
    public async Task Newest_state_survives_old_events_duplicates_conflicts_and_restart()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        var id = Guid.NewGuid();
        var latest = Snapshot(id, 3, IdentityState.Deleted, "latest@example.com");
        Assert.Equal(ProjectionDecision.Apply, await Apply(app, latest));
        Assert.Equal(ProjectionDecision.Ignore, await Apply(app, Snapshot(id, 2)));
        Assert.Equal(ProjectionDecision.Ignore, await Apply(app, latest with { EventId = Guid.NewGuid() }));
        var restore = Snapshot(id, 4);
        Assert.Equal(ProjectionDecision.Conflict, await Apply(app, restore));
        Assert.Equal(ProjectionDecision.Conflict, await Apply(app, restore));
        await using var restarted = new NotificationFactory(app.ConnectionString, fixture.BrokerSettings);
        using var restartedClient = restarted.CreateClient();
        var contact = await Contact(restarted, id);
        Assert.Equal(3, contact.EventVersion); Assert.Equal(IdentityState.Deleted, contact.State);
        Assert.Equal("latest@example.com", contact.Email);
        Assert.Equal(1L, await restarted.ScalarAsync("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.Equal(2, await restarted.ScalarAsync("SELECT \"Observations\" FROM \"MessageConflicts\""));
        var logs = string.Join('\n', app.Logs.Entries);
        Assert.Contains("IdentityProjectionConflict", logs);
        Assert.DoesNotContain("latest@example.com", logs); Assert.DoesNotContain("person@example.com", logs);
    }

    [Fact]
    public async Task Valid_contract_name_is_preserved_without_an_extra_storage_length_limit()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        var incoming = Snapshot(Guid.NewGuid(), 2) with { Name = new string('n', 121) };
        IdentitySnapshotValidation.Validate(incoming);
        Assert.Equal(ProjectionDecision.Apply, await Apply(app, incoming));
        Assert.Equal(incoming.Name, (await Contact(app, incoming.UserId)).Name);
    }

    [Fact]
    public async Task Equal_version_with_different_contact_keeps_original_and_records_durable_conflict()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var id = Guid.NewGuid();
        var original = Snapshot(id, 2);
        await Apply(app, original);
        Assert.Equal(ProjectionDecision.Conflict, await Apply(app, original with { EventId = Guid.NewGuid(), Email = "other@example.com" }));
        Assert.Equal(original.Email, (await Contact(app, id)).Email);
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"MessageConflicts\" WHERE \"Reason\" = 'DivergentIdentityVersion'"));
    }

    [Fact]
    public async Task Divergent_deleted_contact_of_same_version_is_not_misdiagnosed_as_restoration()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var id = Guid.NewGuid();
        var deleted = Snapshot(id, 3, IdentityState.Deleted);
        await Apply(app, deleted);
        await Apply(app, deleted with { EventId = Guid.NewGuid(), Email = "other@example.com" });
        Assert.Equal("DivergentIdentityVersion", await app.ScalarAsync("SELECT \"Reason\" FROM \"MessageConflicts\""));
        Assert.Equal(IdentityState.Deleted, (await Contact(app, id)).State);
    }

    [Fact]
    public async Task Concurrent_real_contexts_always_preserve_largest_version()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var id = Guid.NewGuid();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ids = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        async Task ApplyConcurrent(long version)
        {
            await using var scope = app.Services.CreateAsyncScope();
            ids.Add(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().ContextId.InstanceId);
            await release.Task;
            await scope.ServiceProvider.GetRequiredService<IdentityProjectionHandler>().HandleAsync(Snapshot(id, version), Guid.NewGuid(), CancellationToken.None);
        }
        var second = ApplyConcurrent(2); var third = ApplyConcurrent(3); release.SetResult();
        await Task.WhenAll(second, third).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Equal(3, (await Contact(app, id)).EventVersion);
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"UserContacts\""));
    }

    [Fact]
    public async Task Real_transport_consumes_canonical_created_and_changed_events()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var id = Guid.NewGuid();
        var snapshot = Snapshot(id, 1);
        var created = new UserCreatedEvent
        {
            EventId = snapshot.EventId, UserId = id, EventVersion = 1, OccurredAtUtc = snapshot.CreatedAtUtc,
            CreatedAtUtc = snapshot.CreatedAtUtc, Name = snapshot.Name, Email = snapshot.Email,
            Role = snapshot.Role, State = IdentityState.Active
        };
        var bus = app.Services.GetRequiredService<IBus>();
        await bus.Publish(created);
        await Eventually(async () => (long)(await app.ScalarAsync("SELECT count(*) FROM \"UserContacts\""))! == 1);
        var change = new UserStateChangedEvent
        {
            EventId = Guid.NewGuid(), UserId = id, EventVersion = 3, OccurredAtUtc = snapshot.OccurredAtUtc,
            CreatedAtUtc = snapshot.CreatedAtUtc, Name = snapshot.Name, Email = "new@example.com",
            Role = IdentityRole.User, State = IdentityState.Deleted
        };
        await bus.Publish(change);
        await Eventually(async () => (await Contact(app, id)).EventVersion == 3);
        Assert.Equal("new@example.com", (await Contact(app, id)).Email);
        Assert.Equal(IdentityState.Deleted, (await Contact(app, id)).State);
        Assert.DoesNotContain("new@example.com", string.Join('\n', app.Logs.Entries));
    }

    [Fact]
    public async Task Database_outage_is_not_ready_but_liveness_and_recovery_work()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        await fixture.PauseDatabaseAsync();
        try
        {
            using var ready = await client.GetAsync("/health/ready"); using var live = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode); Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }
        finally { await fixture.UnpauseDatabaseAsync(); }
        using var recovered = await client.GetAsync("/health/ready"); Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.DoesNotContain("   at ", string.Join('\n', app.Logs.Entries));
    }

    [Theory]
    [InlineData("role")]
    [InlineData("email")]
    public async Task Invalid_wire_payload_is_isolated_in_error_queue_without_projecting(string invalidField)
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        var settings = fixture.BrokerSettings;
        var factory = new ConnectionFactory
        {
            HostName = settings["RabbitMq:Host"]!, Port = int.Parse(settings["RabbitMq:Port"]!),
            VirtualHost = "/fcg", UserName = settings["RabbitMq:Username"]!, Password = settings["RabbitMq:Password"]!
        };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var errorQueue = app.IdentityQueue + "_error";
        await channel.QueueDeclareAsync(errorQueue, durable: true, exclusive: false, autoDelete: false);
        var snapshot = Snapshot(Guid.NewGuid(), 2);
        var json = IdentityContractJson.Write(snapshot);
        json = invalidField == "role" ? json.Replace("\"role\":\"User\"", "\"role\":\"Owner\"")
            : json.Replace("person@example.com", "invalid-contact");
        var envelope = JsonSerializer.Serialize(new
        {
            messageId = snapshot.EventId, messageType = new[] { "urn:message:FCG.Contracts:UserStateSnapshotEvent" },
            message = JsonSerializer.Deserialize<JsonElement>(json), headers = new { }
        });
        await channel.BasicPublishAsync("FCG.Contracts:UserStateSnapshotEvent", "", mandatory: true,
            basicProperties: new BasicProperties { ContentType = "application/vnd.masstransit+json", MessageId = snapshot.EventId.ToString() },
            body: Encoding.UTF8.GetBytes(envelope));
        BasicGetResult? failed = null;
        await Eventually(async () => (failed = await channel.BasicGetAsync(errorQueue, autoAck: false)) is not null);
        Assert.NotNull(failed);
        Assert.Contains(snapshot.EventId.ToString(), Encoding.UTF8.GetString(failed!.Body.Span));
        await channel.BasicAckAsync(failed.DeliveryTag, multiple: false);
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"UserContacts\""));
        Assert.DoesNotContain("invalid-contact", string.Join('\n', app.Logs.Entries));
        Assert.DoesNotContain("   at ", string.Join('\n', app.Logs.Entries));
    }

    internal static UserStateSnapshotEvent Snapshot(Guid id, long version, IdentityState state = IdentityState.Active, string email = "person@example.com") => new()
    {
        EventId = Guid.NewGuid(), UserId = id, EventVersion = version, OccurredAtUtc = DateTimeOffset.Parse("2026-10-28T12:00:00Z"),
        CreatedAtUtc = DateTimeOffset.Parse("2026-10-28T11:00:00Z"), Name = "Person", Email = email, Role = IdentityRole.User, State = state
    };
    internal static async Task<ProjectionDecision> Apply(NotificationFactory app, IdentitySnapshot snapshot)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityProjectionHandler>().HandleAsync(snapshot, Guid.NewGuid(), CancellationToken.None);
    }
    internal static async Task<UserContact> Contact(NotificationFactory app, Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().UserContacts.AsNoTracking().SingleAsync(x => x.UserId == id);
    }
    internal static async Task Eventually(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await condition()) await Task.Delay(100, timeout.Token);
    }
}
