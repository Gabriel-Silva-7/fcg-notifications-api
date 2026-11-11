using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PurchaseContracts;

public sealed class PurchaseContractTests
{
    // Missing validation, a renamed wire field or loss of temporal/decimal precision must fail these tests.
    [Theory]
    [InlineData("OrderPlacedEvent", "order-placed.json")]
    [InlineData("PaymentProcessedEvent", "payment-approved.json")]
    [InlineData("PaymentProcessedEvent", "payment-rejected.json")]
    public void Literal_payload_round_trips_with_default_and_web_serializers(string name, string file)
    {
        var type = Contract(name);
        var json = Fixture(file);
        foreach (var options in new[] { new JsonSerializerOptions(), new JsonSerializerOptions(JsonSerializerDefaults.Web) })
        {
            var value = JsonSerializer.Deserialize(json, type, options);
            Assert.NotNull(value);
            Assert.Equal(JsonNode.Parse(json)!.ToJsonString(), JsonNode.Parse(JsonSerializer.Serialize(value, type, options))!.ToJsonString());
        }
    }

    public static IEnumerable<object[]> InvalidFixtures() => Directory.GetFiles(FixtureDirectory, "invalid-*.json")
        .Select(path => new object[] { Path.GetFileName(path) });

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void Invalid_payload_fails_contract_instead_of_becoming_a_financial_result(string file)
    {
        var type = Contract(file.Split('-')[1]);
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize(Fixture(file), type));
    }

    [Theory]
    [InlineData("0.00")]
    [InlineData("500.00")]
    [InlineData("9999999999999999.99")]
    public void Exact_BRL_numeric18_2_boundaries_survive_round_trip(string amount)
    {
        var type = Contract("OrderPlacedEvent");
        var json = Fixture("order-placed.json").Replace("44.93", amount);
        var value = JsonSerializer.Deserialize(json, type);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, type));
        Assert.Equal(amount, document.RootElement.GetProperty("price").GetRawText());
    }

    [Theory]
    [InlineData("OrderPlacedEvent", "order-placed.json")]
    [InlineData("PaymentProcessedEvent", "payment-approved.json")]
    [InlineData("PaymentProcessedEvent", "payment-rejected.json")]
    public void Business_equivalence_ignores_transport_but_detects_every_changed_business_field(string name, string file)
    {
        var type = Contract(name);
        var json = Fixture(file);
        var first = JsonSerializer.Deserialize(json, type)!;
        // Distinct transport IDs do not enter these event DTOs at all.
        var envelope1 = (MessageId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Message: first);
        var envelope2 = (MessageId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), Message: JsonSerializer.Deserialize(json, type)!);
        Assert.Equal(envelope1.Message, envelope2.Message);
        Assert.Null(type.GetProperty("MessageId"));
        var changedFields = new Dictionary<string, JsonNode?>
        {
            ["orderId"] = "44444444-4444-4444-4444-444444444444",
            ["userId"] = "55555555-5555-5555-5555-555555555555",
            ["gameId"] = "66666666-6666-6666-6666-666666666666",
            ["price"] = 45.00m,
            [name == "OrderPlacedEvent" ? "placedAtUtc" : "processedAtUtc"] = "2026-10-19T12:35:00.123456Z"
        };
        foreach (var (key, value) in changedFields)
        {
            var changed = JsonNode.Parse(json)!;
            changed[key] = value;
            Assert.NotEqual(first, JsonSerializer.Deserialize(changed.ToJsonString(), type));
        }
        if (name == "PaymentProcessedEvent")
        {
            var opposite = JsonNode.Parse(Fixture(file == "payment-approved.json" ? "payment-rejected.json" : "payment-approved.json"))!;
            // Keep price identical so this assertion detects a lost status/reason comparison.
            opposite["price"] = JsonNode.Parse(json)!["price"]!.DeepClone();
            Assert.NotEqual(first, JsonSerializer.Deserialize(opposite.ToJsonString(), type));
        }
    }

    [Fact]
    public void Public_constructor_rejects_submicrosecond_and_non_UTC_instants()
    {
        var type = Contract("OrderPlacedEvent");
        var constructor = Assert.Single(type.GetConstructors());
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var instant = DateTimeOffset.Parse("2026-10-19T12:34:56.123456Z");
        foreach (var bad in new[] { instant.AddTicks(1), instant.ToOffset(TimeSpan.FromHours(1)) })
        {
            var exception = Assert.Throws<TargetInvocationException>(() => constructor.Invoke([id,id,id,44.93m,bad]));
            Assert.IsAssignableFrom<ArgumentException>(exception.InnerException);
        }
    }

    [Theory]
    [InlineData("0.000000000000000000000000000001")]
    [InlineData("44.93000000000000000000000000001")]
    public void JSON_number_must_be_exact_cents_before_decimal_parser_rounding(string amount)
    {
        var type=Contract("OrderPlacedEvent");
        Assert.ThrowsAny<Exception>(()=>JsonSerializer.Deserialize(Fixture("order-placed.json").Replace("44.93",amount),type));
    }
    [Theory]
    [InlineData("4.493e1")]
    [InlineData("44.9300")]
    [InlineData("0e99999999999999999999")]
    public void Numerically_equivalent_BRL_representation_is_valid(string amount)
    {
        var type=Contract("OrderPlacedEvent");
        var value=JsonSerializer.Deserialize(Fixture("order-placed.json").Replace("44.93",amount),type)!;
        Assert.Equal(amount.StartsWith("0e")?0m:44.93m,type.GetProperty("Price")!.GetValue(value));
    }
    [Theory]
    [InlineData("OrderPlacedEvent")]
    [InlineData("PaymentProcessedEvent")]
    public void Null_is_not_an_event(string name)
    {
        var type=Contract(name);
        Assert.ThrowsAny<Exception>(()=>JsonSerializer.Deserialize("null",type));
    }
    [Fact]
    public void Duplicate_fields_are_not_silently_last_value_wins()
    {
        var type=Contract("OrderPlacedEvent");
        Assert.ThrowsAny<Exception>(()=>JsonSerializer.Deserialize(Fixture("order-placed.json").Replace("\"price\":44.93","\"price\":0,\"price\":44.93"),type));
    }

    private static Type Contract(string name)
    {
        var type = typeof(Program).Assembly.GetType("FCG.Contracts." + name);
        Assert.NotNull(type);
        return type;
    }
    private static string SourcePath([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
    private static string FixtureDirectory => Path.Combine(SourcePath(), "fixtures");
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FixtureDirectory, name)).Trim();
}
