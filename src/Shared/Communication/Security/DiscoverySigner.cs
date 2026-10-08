using System.Globalization;
using ClassroomControl.Shared.Communication.Messages;

namespace ClassroomControl.Shared.Communication.Security;

/// <summary>Signs / verifies discovery datagrams with the classroom key. The packet itself carries no secret.</summary>
public static class DiscoverySigner
{
    public static string Sign(byte[] classroomKey, DiscoveryPacket p) => HandshakeCrypto.Sign(classroomKey,
        "DISC1", p.Service, p.Version.ToString(CultureInfo.InvariantCulture), p.Type, p.Device,
        p.Port.ToString(CultureInfo.InvariantCulture), p.Classroom ?? string.Empty, p.TeacherId ?? string.Empty,
        p.TeacherName ?? string.Empty, p.Nonce, p.Timestamp.ToString(CultureInfo.InvariantCulture));

    public static DiscoveryPacket WithSignature(byte[] classroomKey, DiscoveryPacket p) => p with { Signature = Sign(classroomKey, p) };

    public static bool Verify(byte[] classroomKey, DiscoveryPacket p) => HandshakeCrypto.ProofEquals(Sign(classroomKey, p), p.Signature);
}
