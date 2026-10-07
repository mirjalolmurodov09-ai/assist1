using System.Text.Json;

namespace ClassroomControl.StudentAgent.Models;

public enum CommandStatus { Success, Failed, Rejected, NotImplemented }

public sealed record CommandResult(
    string CommandId,
    string DeviceId,
    CommandStatus Status,
    DateTimeOffset Timestamp,
    string? ErrorCode,
    string? Message,
    JsonElement? Payload);
