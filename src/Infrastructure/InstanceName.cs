using System.Security.Cryptography;
using System.Text;

namespace ClassroomControl.Infrastructure;

public static class InstanceName
{
    /// <summary>A short, stable (same in every process) suffix for a data directory, used to name the single-instance mutex.
    /// <c>string.GetHashCode()</c> must not be used for this: it is randomized per process.</summary>
    public static string For(string? dataDirectory) => string.IsNullOrEmpty(dataDirectory)
        ? string.Empty
        : "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).TrimEnd('\\', '/').ToUpperInvariant())))[..12];
}
