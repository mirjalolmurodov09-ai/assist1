using System.Net;
using ClassroomControl.ClassroomServer.Data;

namespace ClassroomControl.ClassroomServer;

/// <summary>Deliberately breaks the protocol so tests can prove the Student Agent defends itself. All off in production.</summary>
public sealed class ServerFaultInjection
{
    public string? ProtocolVersion { get; set; }
    public TimeSpan ChallengeTimeOffset { get; set; }
    public bool AckHeartbeats { get; set; } = true;
    public bool RecordFrames { get; set; }
}

public sealed class ClassroomServerOptions
{
    public string DataDirectory { get; set; } = string.Empty;
    public int TcpPort { get; set; } = ProtocolConstants.DefaultTeacherPort;
    public int DiscoveryPort { get; set; } = ProtocolConstants.DefaultDiscoveryPort;
    public IPAddress BindAddress { get; set; } = IPAddress.Any;
    public string ClassroomName { get; set; } = "Sinf";
    public string TeacherName { get; set; } = Environment.MachineName;
    /// <summary>Lab/test only: accept clients on the loopback interface.</summary>
    public bool AllowLoopbackClients { get; set; }
    public int HeartbeatTimeoutSeconds { get; set; } = 15;
    public int HandshakeTimeoutSeconds { get; set; } = 10;
    public int MaxConnections { get; set; } = 200;
    public int MaxClockSkewSeconds { get; set; } = 120;
    public int CommandTimeoutSeconds { get; set; } = 15;
    public ServerFaultInjection? Faults { get; set; }

    public string DatabasePath => Path.Combine(DataDirectory, "classroom.db");
    public string CertificatePath => Path.Combine(DataDirectory, "teacher-tls.pfx");
    public string ScreenshotDirectory => Path.Combine(DataDirectory, "Screenshots");
}

/// <summary>What the Teacher UI knows about one computer right now.</summary>
public sealed record DeviceSnapshot(
    ComputerRecord Computer,
    bool Online,
    string? SessionId,
    StatusUpdateMessage? Status)
{
    public string DeviceId => Computer.DeviceId;
    public bool IsApproved => Computer.Status == RegistrationState.Approved;
    public bool Locked => Status?.Locked ?? false;
    public bool Streaming => Status?.Streaming ?? false;
    public bool RemoteControlActive => Status?.RemoteControlActive ?? false;
    public int CpuPercent => Status?.CpuPercent ?? 0;
    public int PingMilliseconds => Status?.PingMilliseconds ?? 0;
}

public sealed class FrameReceivedEventArgs : EventArgs
{
    public FrameReceivedEventArgs(string deviceId, ScreenFrameMessage frame)
    {
        DeviceId = deviceId;
        Frame = frame;
    }

    public string DeviceId { get; }
    public ScreenFrameMessage Frame { get; }
}

public sealed record CommandOutcome(string DeviceId, string CommandName, bool Success, string? ErrorCode, string Message, CommandResult? Result)
{
    public static CommandOutcome Failure(string deviceId, string name, string code, string message) => new(deviceId, name, false, code, message, null);
}

/// <summary>Teacher-wide tunables kept in the Settings table.</summary>
public sealed class ServerSettings
{
    private readonly ClassroomStore _store;

    public ServerSettings(ClassroomStore store) => _store = store;

    public int MonitoringFps { get => Int(nameof(MonitoringFps), 3, 1, 10); set => Set(nameof(MonitoringFps), value); }
    public int MonitoringQuality { get => Int(nameof(MonitoringQuality), 50, 10, 95); set => Set(nameof(MonitoringQuality), value); }
    public int MonitoringMaxWidth { get => Int(nameof(MonitoringMaxWidth), 480, 160, 1920); set => Set(nameof(MonitoringMaxWidth), value); }
    public int FullScreenFps { get => Int(nameof(FullScreenFps), 15, 1, 30); set => Set(nameof(FullScreenFps), value); }
    public int FullScreenQuality { get => Int(nameof(FullScreenQuality), 70, 10, 95); set => Set(nameof(FullScreenQuality), value); }
    public int FullScreenMaxWidth { get => Int(nameof(FullScreenMaxWidth), 1280, 320, 3840); set => Set(nameof(FullScreenMaxWidth), value); }
    public int TeacherScreenFps { get => Int(nameof(TeacherScreenFps), 8, 1, 30); set => Set(nameof(TeacherScreenFps), value); }
    public int TeacherScreenQuality { get => Int(nameof(TeacherScreenQuality), 60, 10, 95); set => Set(nameof(TeacherScreenQuality), value); }
    public int TeacherScreenMaxWidth { get => Int(nameof(TeacherScreenMaxWidth), 1280, 320, 3840); set => Set(nameof(TeacherScreenMaxWidth), value); }
    public bool RequireAgentActive { get => Int(nameof(RequireAgentActive), 0, 0, 1) == 1; set => Set(nameof(RequireAgentActive), value ? 1 : 0); }
    public int AutoUnlockSeconds { get => Int(nameof(AutoUnlockSeconds), 30, 0, 3600); set => Set(nameof(AutoUnlockSeconds), value); }
    public string Theme { get => Text(nameof(Theme), "Light"); set => _store.SetSetting(nameof(Theme), value); }
    public string Language { get => Text(nameof(Language), "uz"); set => _store.SetSetting(nameof(Language), value); }
    public int TcpPort { get => Int(nameof(TcpPort), ProtocolConstants.DefaultTeacherPort, 1, 65535); set => Set(nameof(TcpPort), value); }
    public int DiscoveryPort { get => Int(nameof(DiscoveryPort), ProtocolConstants.DefaultDiscoveryPort, 1, 65535); set => Set(nameof(DiscoveryPort), value); }

    private string Text(string key, string fallback) => _store.GetSetting(key) ?? fallback;

    private int Int(string key, int fallback, int min, int max) =>
        int.TryParse(_store.GetSetting(key), out var v) ? Math.Clamp(v, min, max) : fallback;

    private void Set(string key, int value) => _store.SetSetting(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
