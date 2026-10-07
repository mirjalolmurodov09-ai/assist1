using System.Text.RegularExpressions;

namespace ClassroomControl.StudentAgent.Infrastructure.Logging;

/// <summary>Removes secrets from any text before it is written to a log.</summary>
public static partial class SensitiveDataRedactor
{
    public const string Mask = "***";

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|token|secret|private[ _-]?key|classroom[ _-]?code|api[ _-]?key|signature|proof)\b(\s*[:=]\s*)(""[^""]*""|'[^']*'|\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"\bCLASS-[A-Za-z0-9]{4}-[0-9]{4}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Code();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PemKey();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var result = PemKey().Replace(text, Mask);
        result = KeyValue().Replace(result, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Mask}");
        return Code().Replace(result, Mask);
    }
}
