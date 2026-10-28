using System.Text.Json;
using System.Text.Json.Serialization;
using FCG.Contracts;

namespace FCG.Notifications.Messaging;

public sealed class IdentityEventConverter<T> : JsonConverter<T> where T : IdentitySnapshot
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return IdentityContractJson.Read<T>(document.RootElement.GetRawText());
    }
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        using var document = JsonDocument.Parse(IdentityContractJson.Write(value));
        document.RootElement.WriteTo(writer);
    }
}
