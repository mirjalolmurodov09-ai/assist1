namespace ClassroomControl.StudentAgent.Models;

public enum ConnectionState
{
    Disconnected,
    Discovering,
    TeacherFound,
    Connecting,
    Authenticating,
    WaitingForApproval,
    Connected,
    Reconnecting,
}

public enum RegistrationState
{
    NotRegistered,
    Pending,
    Approved,
    Rejected,
}

public enum StatusIndicator { Offline, Connecting, Connected }

public sealed record AgentStatusSnapshot(
    ConnectionState State,
    RegistrationState Registration,
    string StatusText,
    string? TeacherAddress,
    string? TeacherName,
    string? ClassroomName,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset? LastConnectionUtc,
    DateTimeOffset StartedUtc,
    bool RequireAgentActive)
{
    /// <summary>Connected = green, attempting to connect = yellow, offline / waiting to retry = red.</summary>
    public StatusIndicator Indicator => State switch
    {
        ConnectionState.Connected => StatusIndicator.Connected,
        ConnectionState.Disconnected or ConnectionState.Reconnecting => StatusIndicator.Offline,
        _ => StatusIndicator.Connecting,
    };

    public static AgentStatusSnapshot Initial(DateTimeOffset now) => new(
        ConnectionState.Disconnected, RegistrationState.NotRegistered, string.Empty,
        null, null, null, null, null, null, now, false);
}
