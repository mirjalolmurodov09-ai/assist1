using System.Security.Cryptography;
using System.Text;

namespace ClassroomControl.StudentAgent.Communication.Security;

/// <summary>Encrypts secrets (classroom code) before they are written to the settings file.</summary>
public interface ISecretProtector
{
    string Protect(string plainText);
    /// <summary>Returns null when the value cannot be decrypted (different user/machine, corrupted).</summary>
    string? Unprotect(string protectedText);
}

/// <summary>Windows DPAPI, scope CurrentUser: only the same Windows account can decrypt.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClassroomControl.StudentAgent.Settings.v1");

    public string Protect(string plainText)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requires Windows.");
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser));
    }

    public string? Unprotect(string protectedText)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requires Windows.");
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>AES-GCM with a per-installation random key kept in a user-only file. Used where DPAPI is unavailable.</summary>
public sealed class AesFileSecretProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private readonly byte[] _key;

    public AesFileSecretProtector(string keyFilePath)
    {
        if (File.Exists(keyFilePath))
        {
            _key = File.ReadAllBytes(keyFilePath);
            if (_key.Length != KeySize) throw new InvalidDataException("Secret key file is corrupted.");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyFilePath))!);
            _key = RandomNumberGenerator.GetBytes(KeySize);
            File.WriteAllBytes(keyFilePath, _key);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public string Protect(string plainText)
    {
        var plain = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string? Unprotect(string protectedText)
    {
        try
        {
            var blob = Convert.FromBase64String(protectedText);
            if (blob.Length < NonceSize + TagSize) return null;
            var plain = new byte[blob.Length - NonceSize - TagSize];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
