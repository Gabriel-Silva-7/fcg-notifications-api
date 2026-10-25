using FCG.Contracts;
using Xunit;

namespace IdentityContracts;

public class IdentityEmailCompatibilityTests
{
    // Registration preserves the inherited Email value object (MailAddress),
    // which accepts a single-label domain and quoted local parts.
    [Theory]
    [InlineData("player@localhost")]
    [InlineData("player@example.test")]
    [InlineData("\"ana maria\"@localhost")]
    public void Registered_normalized_email_survives_event_serialization(string email)
    {
        var snapshot = IdentityWireTests.Snapshot() with { Email = email };
        var json = IdentityContractJson.Write(snapshot);
        Assert.Equal(email, IdentityContractJson.Read<UserStateSnapshotEvent>(json).Email);
    }

    [Theory]
    [InlineData("ana..silva@example.test")]
    [InlineData(".ana@example.test")]
    [InlineData("ana@example..test")]
    [InlineData("ana<ana@example.test>")]
    [InlineData("<ana@example.test>")]
    [InlineData("Ana@Example.test")]
    [InlineData(" player@example.test ")]
    public void Noncanonical_mailbox_is_rejected_by_producer_and_consumer(string email)
    {
        var snapshot = IdentityWireTests.Snapshot() with { Email = email };
        Assert.ThrowsAny<ArgumentException>(() => IdentityContractJson.Write(snapshot));
        var json = System.Text.Json.Nodes.JsonNode.Parse(IdentityContractJson.Write(IdentityWireTests.Snapshot()))!;
        json["email"] = email;
        Assert.ThrowsAny<ArgumentException>(() => IdentityContractJson.Read<UserStateSnapshotEvent>(json.ToJsonString()));
    }

    [Fact]
    public void Email_longer_than_registration_limit_is_rejected()
    {
        var snapshot = IdentityWireTests.Snapshot() with { Email = new string('a', 245) + "@example.test" };
        Assert.Equal(258, snapshot.Email.Length);
        Assert.ThrowsAny<ArgumentException>(() => IdentityContractJson.Write(snapshot));
    }
}
