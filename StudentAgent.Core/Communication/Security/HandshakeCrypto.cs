using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ClassroomControl.StudentAgent.Communication.Security;

public sealed record SessionKeys(byte[] ClientToTeacher, byte[] TeacherToClient);

/// <summary>HMAC-SHA256 proofs used during mutual authentication. Both Student and Teacher use the same helpers.
/// Every proof is bound to both nonces, the device id and the TLS certificate fingerprint (channel binding),
/// so a man-in-the-middle presenting a different certificate cannot relay the handshake.</summary>
public static class HandshakeCrypto
{
    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(Protocol.ProtocolConstants.NonceBytes));

    public static bool IsValidNonce(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce) || nonce.Length > 64) return false;
        Span<byte> buffer = stackalloc byte[48];
        return Convert.TryFromBase64String(nonce, buffer, out var written) && written == Protocol.ProtocolConstants.NonceBytes;
    }

    public static string ServerProof(byte[] key, string deviceId, string nonceStudent, string nonceTeacher,
        string certFingerprint, long timestamp, string teacherId, string classroomId) =>
        Mac(key, "SRV1", deviceId, nonceStudent, nonceTeacher, certFingerprint, Num(timestamp), teacherId, classroomId);

    public static string ClientProof(byte[] key, string deviceId, string nonceStudent, string nonceTeacher,
        string certFingerprint, long timestamp) =>
        Mac(key, "CLI1", deviceId, nonceTeacher, nonceStudent, certFingerprint, Num(timestamp));

    public static string ResultProof(byte[] key, string deviceId, string nonceStudent, string nonceTeacher,
        string sessionId, string registration, bool success) =>
        Mac(key, "RES1", deviceId, nonceStudent, nonceTeacher, sessionId, registration, success ? "1" : "0");

    /// <summary>Per-session, per-direction signing keys. Never transmitted. Distinct keys per direction prevent reflection.</summary>
    public static SessionKeys DeriveSessionKeys(byte[] classroomKey, string nonceStudent, string nonceTeacher, string sessionId) => new(
        Raw(classroomKey, "SESSION-C2T", nonceStudent, nonceTeacher, sessionId),
        Raw(classroomKey, "SESSION-T2C", nonceStudent, nonceTeacher, sessionId));

    public static bool ProofEquals(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(expected), Convert.FromBase64String(actual));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Sign(byte[] key, params string[] parts) => Convert.ToBase64String(Raw(key, parts));

    private static string Mac(byte[] key, params string[] parts) => Convert.ToBase64String(Raw(key, parts));

    private static string Num(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] Raw(byte[] key, params string[] parts)
    {
        using var hmac = new HMACSHA256(key);
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part ?? string.Empty);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hmac.TransformBlock(length.ToArray(), 0, 4, null, 0);
            hmac.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        hmac.TransformFinalBlock([], 0, 0);
        return hmac.Hash!;
    }
}

public static class CertificateFingerprint
{
    public static string Of(System.Security.Cryptography.X509Certificates.X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
}
