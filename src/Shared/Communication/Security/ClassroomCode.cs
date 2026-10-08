using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassroomControl.Shared.Communication.Security;

/// <summary>Classroom code format (<c>CLASS-8F4K-2026</c>) and key derivation.
/// The code is a shared secret: it is never transmitted, only used to derive an HMAC key.</summary>
public static partial class ClassroomCode
{
    private const int KeyBytes = 32;
    private const int Pbkdf2Iterations = 100_000;
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("ClassroomControl/v1/classroom-key");

    [GeneratedRegex("^CLASS-[A-Z0-9]{4,8}-[0-9]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex FormatRegex();

    public static string Normalize(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int GeneratedChars = 8;

    /// <summary>Creates a new random code such as <c>CLASS-7KQ2M9XD-2026</c> (40 random bits + the current year).</summary>
    public static string Generate(DateTimeOffset now)
    {
        var chars = new char[GeneratedChars];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return $"CLASS-{new string(chars)}-{now.Year:0000}";
    }

    public static bool IsValidFormat(string? code) => FormatRegex().IsMatch(Normalize(code));

    public static byte[] DeriveKey(string code)
    {
        var normalized = Normalize(code);
        if (!FormatRegex().IsMatch(normalized))
            throw new ArgumentException("Invalid classroom code format.", nameof(code));
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(normalized), Salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);
    }
}

public interface IClassroomKeyProvider
{
    /// <summary>Returns the key for the currently configured classroom code, or null when no valid code is configured.</summary>
    byte[]? GetKey(string? classroomCode);
}

public sealed class ClassroomKeyProvider : IClassroomKeyProvider
{
    private readonly object _gate = new();
    private string? _cachedCode;
    private byte[]? _cachedKey;

    public byte[]? GetKey(string? classroomCode)
    {
        var normalized = ClassroomCode.Normalize(classroomCode);
        if (!ClassroomCode.IsValidFormat(normalized)) return null;
        lock (_gate)
        {
            if (_cachedCode != normalized)
            {
                _cachedKey = ClassroomCode.DeriveKey(normalized);
                _cachedCode = normalized;
            }
            return _cachedKey;
        }
    }
}
