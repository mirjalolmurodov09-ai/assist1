using ClassroomControl.Shared.Communication.Protocol;

namespace ClassroomControl.StudentAgent.Infrastructure.Helpers;

/// <summary>Tunable behaviour. Bound from the "Agent" section of appsettings.json (scalar values only).</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public int DefaultTeacherPort { get; set; } = ProtocolConstants.DefaultTeacherPort;
    public int DiscoveryTimeoutSeconds { get; set; } = 8;
    public int DiscoveryRetryIntervalMilliseconds { get; set; } = 1000;
    public int HeartbeatIntervalSeconds { get; set; } = 5;
    public int HeartbeatTimeoutSeconds { get; set; } = 15;
    public int MaxClockSkewSeconds { get; set; } = 120;
    public int NetworkChangeDebounceMilliseconds { get; set; } = 2000;
    public int SupervisorRestartDelaySeconds { get; set; } = 5;
    public int MaxFrameBytes { get; set; } = ProtocolConstants.MaxFrameBytes;

    /// <summary>Reconnect back-off schedule; the last value repeats forever. Set in code, not in appsettings (arrays append when bound).</summary>
    public int[] ReconnectDelaysSeconds { get; set; } = [1, 2, 5, 10, 20, 30];

    /// <summary>Only the commands listed here may run (all others are rejected as disabled).</summary>
    public string[] EnabledCommands { get; set; } = [CommandNames.Ping, CommandNames.GetStatus, CommandNames.GetDeviceInfo];

    /// <summary>Test/lab only: accept a Teacher on the loopback interface. Off in production.</summary>
    public bool AllowLoopbackTeacher { get; set; }

    /// <summary>Test/lab only: explicit discovery broadcast targets instead of auto-detected subnet broadcasts.</summary>
    public string[] DiscoveryTargets { get; set; } = [];

    public TimeSpan MaxClockSkew => TimeSpan.FromSeconds(MaxClockSkewSeconds);
    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(HeartbeatIntervalSeconds);
    public TimeSpan HeartbeatTimeout => TimeSpan.FromSeconds(HeartbeatTimeoutSeconds);
}
