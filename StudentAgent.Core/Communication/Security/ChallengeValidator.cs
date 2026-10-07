using ClassroomControl.StudentAgent.Communication.Messages;
using ClassroomControl.StudentAgent.Communication.Protocol;

namespace ClassroomControl.StudentAgent.Communication.Security;

public sealed record ChallengeValidation(bool IsValid, string? ErrorCode, string? Message, ProtocolVersion Negotiated = default)
{
    public static ChallengeValidation Ok(ProtocolVersion negotiated) => new(true, null, null, negotiated);
    public static ChallengeValidation Fail(string code, string message) => new(false, code, message);
}

/// <summary>Student-side verification of the Teacher challenge: version, freshness, replay, and the Teacher's proof
/// of knowing the classroom key (which also proves Teacher identity and classroom membership).</summary>
public sealed class ChallengeValidator
{
    private readonly ReplayGuard _replay;
    private readonly TimeProvider _time;
    private readonly TimeSpan _maxSkew;

    public ChallengeValidator(ReplayGuard replay, TimeProvider time, TimeSpan maxSkew)
    {
        _replay = replay;
        _time = time;
        _maxSkew = maxSkew;
    }

    public ChallengeValidation Validate(ChallengeMessage challenge, HelloMessage hello, byte[] classroomKey, string certFingerprint)
    {
        if (!ProtocolVersion.TryParse(challenge.ProtocolVersion, out var teacherVersion)
            || !ProtocolVersion.TryNegotiate(ProtocolConstants.Current, teacherVersion, out var negotiated))
            return ChallengeValidation.Fail(ErrorCodes.VersionMismatch,
                $"Teacher protocol versiyasi ({challenge.ProtocolVersion}) bu Student Agent ({ProtocolConstants.Current}) bilan mos emas. Dasturni yangilang.");

        if (!HandshakeCrypto.IsValidNonce(challenge.Challenge) || string.IsNullOrEmpty(challenge.TeacherId)
            || string.IsNullOrEmpty(challenge.ClassroomId) || string.IsNullOrEmpty(challenge.ServerProof))
            return ChallengeValidation.Fail(ErrorCodes.InvalidMessage, "Teacher challenge noto‘g‘ri formatda.");

        var now = _time.GetUtcNow();
        var issued = DateTimeOffset.FromUnixTimeMilliseconds(challenge.Timestamp);
        if (now - issued > _maxSkew)
            return ChallengeValidation.Fail(ErrorCodes.ExpiredChallenge, "Teacher challenge muddati o'tgan.");
        if (issued - now > _maxSkew)
            return ChallengeValidation.Fail(ErrorCodes.InvalidTimestamp, "Teacher vaqti Student vaqtidan juda farq qiladi.");

        var expected = HandshakeCrypto.ServerProof(classroomKey, hello.DeviceId, hello.Nonce, challenge.Challenge,
            certFingerprint, challenge.Timestamp, challenge.TeacherId, challenge.ClassroomId);
        if (!HandshakeCrypto.ProofEquals(expected, challenge.ServerProof))
            return ChallengeValidation.Fail(ErrorCodes.InvalidClassroomCode, "Classroom code noto‘g‘ri.");

        // Registered only after the proof is valid so a forged challenge cannot poison the replay cache.
        if (!_replay.TryRegister(challenge.Challenge, now))
            return ChallengeValidation.Fail(ErrorCodes.ReplayDetected, "Challenge takrorlandi (replay).");

        return ChallengeValidation.Ok(negotiated);
    }
}
