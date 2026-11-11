using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FCG.Contracts;
using Xunit;

namespace IdentityContracts;

public class IdentityWireTests
{
    private static string Fixture(string name = "user-created.json", [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", name));

    private static string Change(string key, JsonNode? value)
    {
        var node = JsonNode.Parse(Fixture())!;
        node[key] = value;
        return node.ToJsonString();
    }

    [Fact]
    public void Created_literal_survives_serialization_with_all_required_snapshot_fields()
    {
        var created = IdentityContractJson.Read<UserCreatedEvent>(Fixture());
        Assert.Equal(Guid.Parse("22222222-2222-4222-8222-222222222222"), created.UserId);
        Assert.Equal(1, created.EventVersion);
        Assert.Equal("ana@example.test", created.Email);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Fixture()), JsonNode.Parse(IdentityContractJson.Write(created))));
    }

    [Fact]
    public void Changed_and_recovery_are_distinct_types_and_roundtrip_complete_literals()
    {
        var changedJson = Fixture("user-state-changed.json");
        var recoveryJson = Fixture("user-state-snapshot.json");
        var changed = IdentityContractJson.Read<UserStateChangedEvent>(changedJson);
        var recovered = IdentityContractJson.Read<UserStateSnapshotEvent>(recoveryJson);
        Assert.Equal(IdentityState.Blocked, changed.State);
        Assert.Equal(IdentityState.Deleted, recovered.State);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(changedJson), JsonNode.Parse(IdentityContractJson.Write(changed))));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(recoveryJson), JsonNode.Parse(IdentityContractJson.Write(recovered))));
    }

    [Theory]
    [InlineData("eventVersion", "0")]
    [InlineData("eventVersion", "-1")]
    [InlineData("eventVersion", "2")]
    [InlineData("eventId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("userId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("role", "999")]
    [InlineData("role", "\"user\"")]
    [InlineData("state", "\"active\"")]
    [InlineData("role", "\"Owner\"")]
    [InlineData("state", "\"Blocked\"")]
    [InlineData("name", "\"  \"")]
    [InlineData("name", "null")]
    [InlineData("email", "\"Ana@Example.test\"")]
    [InlineData("email", "\"invalid\"")]
    [InlineData("occurredAtUtc", "\"2026-10-19T13:34:56.123456+01:00\"")]
    [InlineData("occurredAtUtc", "\"2026-10-19T12:34:56.1234567Z\"")]
    [InlineData("createdAtUtc", "\"2026-10-19T12:34:57.123456Z\"")]
    public void Invalid_wire_input_is_rejected(string field, string literal) =>
        Assert.ThrowsAny<ArgumentException>(() => IdentityContractJson.Read<UserCreatedEvent>(Change(field, JsonNode.Parse(literal))));

    [Theory]
    [InlineData("password")]
    [InlineData("passwordHash")]
    [InlineData("accessToken")]
    [InlineData("privateKey")]
    public void Unknown_fields_cannot_smuggle_secrets(string field) =>
        Assert.ThrowsAny<ArgumentException>(() => IdentityContractJson.Read<UserCreatedEvent>(Change(field, JsonValue.Create("TEST-ONLY-NOT-A-SECRET"))));

    [Theory]
    [InlineData("eventId")]
    [InlineData("userId")]
    [InlineData("eventVersion")]
    [InlineData("occurredAtUtc")]
    [InlineData("createdAtUtc")]
    [InlineData("name")]
    [InlineData("email")]
    [InlineData("role")]
    [InlineData("state")]
    public void Missing_required_member_is_rejected(string field)
    {
        var node = JsonNode.Parse(Fixture())!.AsObject();
        node.Remove(field);
        Assert.ThrowsAny<ArgumentException>(() => IdentityContractJson.Read<UserCreatedEvent>(node.ToJsonString()));
    }

    [Fact]
    public void Producer_write_cannot_publish_invalid_snapshot()
    {
        var good = IdentityContractJson.Read<UserCreatedEvent>(Fixture());
        Assert.ThrowsAny<ArgumentException>(() => IdentityContractJson.Write(good with { EventVersion = 0 }));
    }

    [Fact]
    public void State_change_cannot_pretend_to_be_creation_version()
    {
        var node = JsonNode.Parse(Fixture("user-state-changed.json"))!;
        node["eventVersion"] = 1;
        Assert.ThrowsAny<ArgumentException>(() => IdentityContractJson.Read<UserStateChangedEvent>(node.ToJsonString()));
    }

    [Fact]
    public void Duplicate_member_is_rejected_before_ambiguous_version_can_be_applied()
    {
        var json = Fixture().Replace("\"eventVersion\": 1", "\"eventVersion\": 2, \"eventVersion\": 1");
        Assert.ThrowsAny<ArgumentException>(() => IdentityContractJson.Read<UserCreatedEvent>(json));
    }

    internal static UserStateSnapshotEvent Snapshot() => IdentityContractJson.Read<UserStateSnapshotEvent>(Fixture("user-state-snapshot.json"));
}

public class IdentityProjectionTests
{
    [Theory]
    [InlineData(1, IdentityState.Active, ProjectionDecision.Ignore)]
    [InlineData(2, IdentityState.Blocked, ProjectionDecision.Ignore)]
    [InlineData(3, IdentityState.Deleted, ProjectionDecision.Ignore)]
    [InlineData(3, IdentityState.Blocked, ProjectionDecision.Conflict)]
    [InlineData(4, IdentityState.Active, ProjectionDecision.Conflict)]
    [InlineData(4, IdentityState.Blocked, ProjectionDecision.Conflict)]
    [InlineData(4, IdentityState.Deleted, ProjectionDecision.Apply)]
    public void Deleted_projection_never_regresses_or_resurrects(long version, IdentityState state, ProjectionDecision expected)
    {
        var current = IdentityWireTests.Snapshot();
        var incoming = current with { EventVersion = version, State = state, EventId = Guid.NewGuid() };
        Assert.Equal(expected, IdentityProjectionPolicy.Evaluate(current, incoming, ProjectionAudience.Notifications));
    }

    [Fact]
    public void Catalog_equivalence_ignores_contact_but_notifications_detects_divergence()
    {
        var current = IdentityWireTests.Snapshot();
        var incoming = current with { Name = "Ana Nova", Email = "nova@example.test", OccurredAtUtc = current.OccurredAtUtc.AddSeconds(1), EventId = Guid.NewGuid() };
        Assert.Equal(ProjectionDecision.Ignore, IdentityProjectionPolicy.Evaluate(current, incoming, ProjectionAudience.Catalog));
        Assert.Equal(ProjectionDecision.Conflict, IdentityProjectionPolicy.Evaluate(current, incoming, ProjectionAudience.Notifications));
    }

    [Fact]
    public void Missing_projection_accepts_complete_v3_without_intermediates()
    {
        Assert.Equal(ProjectionDecision.Apply, IdentityProjectionPolicy.Evaluate(null, IdentityWireTests.Snapshot(), ProjectionAudience.Catalog));
    }

    [Fact]
    public void Invalid_snapshot_never_creates_permissive_projection()
    {
        var incoming = IdentityWireTests.Snapshot() with { EventVersion = 0 };
        Assert.ThrowsAny<ArgumentException>(() => IdentityProjectionPolicy.Evaluate(null, incoming, ProjectionAudience.Catalog));
    }
}
