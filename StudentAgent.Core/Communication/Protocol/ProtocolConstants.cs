namespace ClassroomControl.StudentAgent.Communication.Protocol;

public static class ProtocolConstants
{
    public const string ServiceName = "ClassroomControl";
    /// <summary>Integer version carried in discovery datagrams ("VERSION = 1").</summary>
    public const int DiscoveryVersion = 1;
    public static readonly ProtocolVersion Current = new(1, 0);

    public const int DefaultDiscoveryPort = 39500;
    public const int DefaultTeacherPort = 39501;
    public const int NonceBytes = 16;
    public const int MaxFrameBytes = 256 * 1024;
    public const int MaxDatagramBytes = 2048;

    public const string DeviceTeacher = "TEACHER";
    public const string DeviceStudent = "STUDENT";
    public const string TlsTargetName = "classroomcontrol.teacher";
}

public static class MessageTypes
{
    public const string DiscoveryRequest = "DISCOVERY_REQUEST";
    public const string DiscoveryResponse = "DISCOVERY_RESPONSE";

    public const string Hello = "HELLO";
    public const string Challenge = "CHALLENGE";
    public const string AuthResponse = "AUTH_RESPONSE";
    public const string AuthResult = "AUTH_RESULT";

    public const string Heartbeat = "HEARTBEAT";
    public const string HeartbeatAck = "HEARTBEAT_ACK";
    public const string Command = "COMMAND";
    public const string CommandResponse = "COMMAND_RESPONSE";
    public const string RegistrationUpdate = "REGISTRATION_UPDATE";
    public const string Disconnect = "DISCONNECT";
}

public static class ErrorCodes
{
    public const string InvalidClassroomCode = "INVALID_CLASSROOM_CODE";
    public const string VersionMismatch = "VERSION_MISMATCH";
    public const string AuthenticationFailed = "AUTHENTICATION_FAILED";
    public const string ExpiredChallenge = "EXPIRED_CHALLENGE";
    public const string ReplayDetected = "REPLAY_DETECTED";
    public const string InvalidSignature = "INVALID_SIGNATURE";
    public const string InvalidTimestamp = "INVALID_TIMESTAMP";
    public const string SessionMismatch = "SESSION_MISMATCH";
    public const string InvalidMessage = "INVALID_MESSAGE";
    public const string NotRegistered = "NOT_REGISTERED";
    public const string UnknownCommand = "UNKNOWN_COMMAND";
    public const string CommandNotImplemented = "COMMAND_NOT_IMPLEMENTED";
    public const string CommandDisabled = "COMMAND_DISABLED";
    public const string CommandFailed = "COMMAND_FAILED";
    public const string Rejected = "REGISTRATION_REJECTED";
    public const string UntrustedCertificate = "UNTRUSTED_CERTIFICATE";
    public const string UntrustedNetwork = "UNTRUSTED_NETWORK";
}

public sealed class ProtocolException : Exception
{
    public string ErrorCode { get; }
    public ProtocolException(string errorCode, string message) : base(message) => ErrorCode = errorCode;
    public ProtocolException(string errorCode, string message, Exception inner) : base(message, inner) => ErrorCode = errorCode;
}
