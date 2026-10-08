using System.Text.Json;
using ClassroomControl.Shared.Models;

namespace ClassroomControl.Shared.Communication.Messages;

/// <summary>Frame exchanged over the TLS channel. <c>Payload</c> is a JSON string so the signature covers its exact bytes.</summary>
public sealed class WireMessage
{
    public string ProtocolVersion { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public long Timestamp { get; set; }
    public string Payload { get; set; } = "{}";
    public string? Signature { get; set; }
}

public sealed record HelloMessage(string DeviceId, string AgentVersion, string ProtocolVersion, string ComputerName, string Nonce, long Timestamp);

public sealed record ChallengeMessage(
    string ProtocolVersion, string TeacherId, string TeacherName, string ClassroomId, string ClassroomName,
    string Challenge, long Timestamp, string ServerProof);

public sealed record AuthResponseMessage(
    string DeviceId, string ClientProof, string ComputerName, string StudentName, string LocalIp,
    string OperatingSystem, string AgentVersion);

public sealed record AgentPolicy(bool RequireAgentActive);

public sealed record AuthResultMessage(
    bool Success, string? ErrorCode, string? Message, string? SessionId,
    RegistrationState Registration, AgentPolicy? Policy, string ResultProof);

public sealed record RegistrationUpdateMessage(RegistrationState Registration, string? Message, AgentPolicy? Policy);

public sealed record HeartbeatMessage(string DeviceId, string SessionId, long Timestamp, string Status);

public sealed record CommandRequest(string CommandId, string Name, JsonElement? Parameters);

public sealed record DisconnectMessage(string Reason);

/// <summary>UDP discovery datagram (request from Student, response from Teacher). Contains no secrets.</summary>
public sealed record DiscoveryPacket(
    string Service, int Version, string Type, string Device, int Port, string Classroom,
    string TeacherId, string TeacherName, string Nonce, long Timestamp, string Signature);
