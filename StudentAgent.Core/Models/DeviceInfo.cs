namespace ClassroomControl.StudentAgent.Models;

public sealed record DeviceInfo(
    string DeviceId,
    string ComputerName,
    string WindowsUser,
    string LocalIp,
    string MacAddress,
    string OperatingSystem,
    string OsVersion,
    string Cpu,
    long RamBytes,
    string AgentVersion,
    string AgentStatus,
    DateTimeOffset? LastConnectionTime);
