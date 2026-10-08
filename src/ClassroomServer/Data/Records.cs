namespace ClassroomControl.ClassroomServer.Data;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Teacher = "Teacher";
}

public sealed record UserRecord(long Id, string Username, string PasswordHash, string Role, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, bool Disabled);

public sealed record ClassroomRecord(long Id, string Name, string CodeProtected, string TeacherId, DateTimeOffset CreatedAt);

public sealed record GroupRecord(long Id, long ClassroomId, string Name);

public sealed record ComputerRecord(
    long Id,
    string DeviceId,
    long ClassroomId,
    string ComputerName,
    string DisplayName,
    string StudentName,
    string IpAddress,
    string MacAddress,
    string Cpu,
    long RamBytes,
    long? GroupId,
    RegistrationState Status,
    DateTimeOffset? LastSeen,
    DateTimeOffset CreatedAt)
{
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? ComputerName : DisplayName;
}

public sealed record LogEntry(long Id, DateTimeOffset Timestamp, string Teacher, string Computer, string Action, string Result, string Details);

public sealed record ScreenshotRecord(long Id, long? ComputerId, string FilePath, DateTimeOffset TakenAt, string StudentName, string ComputerName);

public sealed record CommandRecord(long Id, string CommandId, long? ComputerId, string Name, string Status, string? ErrorCode, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt, string RequestedBy);
