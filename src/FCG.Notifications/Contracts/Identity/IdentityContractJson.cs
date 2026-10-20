using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FCG.Contracts;

/// <summary>Closed camelCase payloads, named enums and canonical UTC timestamps with six fractional digits.</summary>
public static class IdentityContractJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false), new UtcMicrosecondsConverter() }
    };

    public static T Read<T>(string json) where T : IdentitySnapshot
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
                    != document.RootElement.EnumerateObject().Count())
                throw new JsonException("Identity payload must be an object without duplicate members.");
            var payload = document.RootElement;
            if (!payload.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String
                || (role.GetString() != "User" && role.GetString() != "Administrator")
                || !payload.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String
                || (state.GetString() != "Active" && state.GetString() != "Blocked" && state.GetString() != "Deleted"))
                throw new JsonException("Identity role and state require exact contract names.");
            var snapshot = JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Missing identity snapshot.");
            IdentitySnapshotValidation.Validate(snapshot);
            return snapshot;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Invalid identity wire contract.", nameof(json), exception);
        }
    }

    public static string Write<T>(T snapshot) where T : IdentitySnapshot
    {
        IdentitySnapshotValidation.Validate(snapshot);
        return JsonSerializer.Serialize(snapshot, Options);
    }

    private sealed class UtcMicrosecondsConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || !DateTimeOffset.TryParseExact(reader.GetString(), Format,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
                throw new JsonException("Expected a UTC timestamp with six fractional digits and Z suffix.");
            return value;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
    }
}
