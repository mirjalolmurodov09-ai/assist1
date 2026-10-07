using ClassroomControl.StudentAgent.Communication.Protocol;

namespace ClassroomControl.StudentAgent.Models;

/// <summary>In-memory student configuration. The classroom code is held in plain text only in memory;
/// on disk it is stored encrypted (see <c>SettingsService</c>).</summary>
public sealed class StudentSettings
{
    public const int DefaultConnectionTimeoutSeconds = 15;

    public string DeviceId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string StudentName { get; set; } = string.Empty;
    public string ClassroomCode { get; set; } = string.Empty;
    public string ClassroomName { get; set; } = string.Empty;
    public string TeacherAddress { get; set; } = string.Empty;
    public int TeacherPort { get; set; }
    public int DiscoveryPort { get; set; } = ProtocolConstants.DefaultDiscoveryPort;
    public int ConnectionTimeoutSeconds { get; set; } = DefaultConnectionTimeoutSeconds;
    public bool StartWithWindows { get; set; } = true;
    /// <summary>SHA-256 fingerprint (hex) of the Teacher TLS certificate pinned after the first authenticated connection.</summary>
    public string PinnedTeacherCertificate { get; set; } = string.Empty;
    public DateTimeOffset? LastConnectionUtc { get; set; }

    public StudentSettings Clone() => (StudentSettings)MemberwiseClone();
}
