using System.Text.Json;
using System.Text.Json.Serialization;
using ClassroomControl.StudentAgent.Communication.Messages;

namespace ClassroomControl.StudentAgent.Communication.Protocol;

public static class MessageSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) where T : class
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, Options);
            return value ?? throw new ProtocolException(ErrorCodes.InvalidMessage, "Empty message.");
        }
        catch (JsonException ex)
        {
            throw new ProtocolException(ErrorCodes.InvalidMessage, "Malformed message.", ex);
        }
    }

    public static WireMessage DeserializeWire(string json)
    {
        var message = Deserialize<WireMessage>(json);
        if (string.IsNullOrEmpty(message.Type) || string.IsNullOrEmpty(message.ProtocolVersion)
            || string.IsNullOrEmpty(message.MessageId) || message.Payload is null)
            throw new ProtocolException(ErrorCodes.InvalidMessage, "Message is missing required fields.");
        return message;
    }
}
