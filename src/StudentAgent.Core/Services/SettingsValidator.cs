using System.Net;
using ClassroomControl.Shared.Communication.Security;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.StudentAgent.Models;

namespace ClassroomControl.StudentAgent.Services;

public static class SettingsValidator
{
    public const int MaxNameLength = 64;
    public const int MinTimeoutSeconds = 3;
    public const int MaxTimeoutSeconds = 120;

    public static IReadOnlyList<string> Validate(StudentSettings s, bool allowLoopback = false)
    {
        var errors = new List<string>();
        if (!Guid.TryParse(s.DeviceId, out _)) errors.Add("DeviceId noto‘g‘ri.");
        if (s.StudentName.Length > MaxNameLength) errors.Add("Student name juda uzun.");
        if (s.ComputerName.Length > MaxNameLength) errors.Add("Computer name juda uzun.");
        if (s.ClassroomName.Length > MaxNameLength) errors.Add("Classroom name juda uzun.");
        if (!string.IsNullOrEmpty(s.ClassroomCode) && !ClassroomCode.IsValidFormat(s.ClassroomCode))
            errors.Add("Classroom code noto‘g‘ri.");
        if (s.TeacherPort is < 0 or > 65535) errors.Add("Teacher port 0-65535 oralig'ida bo'lishi kerak.");
        if (s.DiscoveryPort is < 1 or > 65535) errors.Add("Discovery port 1-65535 oralig'ida bo'lishi kerak.");
        if (s.ConnectionTimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
            errors.Add($"Connection timeout {MinTimeoutSeconds}-{MaxTimeoutSeconds} soniya bo'lishi kerak.");
        if (!string.IsNullOrWhiteSpace(s.TeacherAddress) && !IsAcceptableTeacherHost(s.TeacherAddress, allowLoopback))
            errors.Add("Teacher IP lokal (private) tarmoq manzili yoki kompyuter nomi bo'lishi kerak.");
        return errors;
    }

    /// <summary>An IP literal must be a private/link-local address; a host name is resolved and re-checked at connect time.</summary>
    public static bool IsAcceptableTeacherHost(string host, bool allowLoopback)
    {
        host = host.Trim();
        if (IPAddress.TryParse(host, out var ip)) return AddressPolicy.IsPermitted(ip, allowLoopback);
        return Uri.CheckHostName(host) is UriHostNameType.Dns && host.Length <= 253;
    }
}
