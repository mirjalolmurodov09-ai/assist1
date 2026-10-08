using ClassroomControl.Shared.Communication.Messages;
using ClassroomControl.Shared.Communication.Protocol;
using ClassroomControl.Shared.Communication.Security;
using Xunit;

namespace ClassroomControl.StudentAgent.Tests;

public sealed class AuthenticationTests
{
    private static readonly byte[] Key = ClassroomCode.DeriveKey("CLASS-8F4K-2026");
    private const string Device = "7c9c9d3e-6e0b-4a43-9f8a-2f7e8c1b9d21";
    private const string CertFp = "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899";

    private readonly ManualTimeProvider _time = new();

    private ChallengeValidator NewValidator() => new(new ReplayGuard(TimeSpan.FromMinutes(10)), _time, TimeSpan.FromMinutes(2));

    private HelloMessage Hello() => new(Device, "1.0.0", "1.0", "PC-01", HandshakeCrypto.NewNonce(), _time.GetUtcNow().ToUnixTimeMilliseconds());

    private ChallengeMessage Challenge(HelloMessage hello, byte[]? key = null, string? version = null, TimeSpan? age = null, string? certFp = null, string? nonce = null)
    {
        var teacherNonce = nonce ?? HandshakeCrypto.NewNonce();
        var timestamp = (_time.GetUtcNow() - (age ?? TimeSpan.Zero)).ToUnixTimeMilliseconds();
        return new ChallengeMessage(version ?? "1.0", "teacher-pc", "Teacher PC", "8-A", "8-A", teacherNonce, timestamp,
            HandshakeCrypto.ServerProof(key ?? Key, hello.DeviceId, hello.Nonce, teacherNonce, certFp ?? CertFp, timestamp, "teacher-pc", "8-A"));
    }

    [Fact]
    public void Valid_challenge_is_accepted()
    {
        var hello = Hello();
        var result = NewValidator().Validate(Challenge(hello), hello, Key, CertFp);
        Assert.True(result.IsValid);
        Assert.Equal(new ProtocolVersion(1, 0), result.Negotiated);
    }

    [Fact]
    public void Challenge_signed_with_a_wrong_classroom_code_is_rejected()
    {
        var hello = Hello();
        var otherKey = ClassroomCode.DeriveKey("CLASS-ZZZZ-9999");
        var result = NewValidator().Validate(Challenge(hello, otherKey), hello, Key, CertFp);
        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.InvalidClassroomCode, result.ErrorCode);
        Assert.Equal("Classroom code noto‘g‘ri.", result.Message);
    }

    [Fact]
    public void Challenge_relayed_through_a_different_tls_certificate_is_rejected()
    {
        var hello = Hello();
        var challenge = Challenge(hello, certFp: new string('0', 64)); // Teacher believed it presented another certificate
        var result = NewValidator().Validate(challenge, hello, Key, CertFp);
        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.InvalidClassroomCode, result.ErrorCode);
    }

    [Fact]
    public void Challenge_answering_a_different_hello_is_rejected()
    {
        var hello = Hello();
        var challenge = Challenge(Hello());
        Assert.False(NewValidator().Validate(challenge, hello, Key, CertFp).IsValid);
    }

    [Fact]
    public void Expired_challenge_is_rejected()
    {
        var hello = Hello();
        var result = NewValidator().Validate(Challenge(hello, age: TimeSpan.FromMinutes(10)), hello, Key, CertFp);
        Assert.Equal(ErrorCodes.ExpiredChallenge, result.ErrorCode);
    }

    [Fact]
    public void Challenge_from_the_far_future_is_rejected()
    {
        var hello = Hello();
        var result = NewValidator().Validate(Challenge(hello, age: TimeSpan.FromMinutes(-10)), hello, Key, CertFp);
        Assert.Equal(ErrorCodes.InvalidTimestamp, result.ErrorCode);
    }

    [Fact]
    public void Replayed_challenge_is_rejected_the_second_time()
    {
        var hello = Hello();
        var challenge = Challenge(hello);
        var validator = NewValidator();
        Assert.True(validator.Validate(challenge, hello, Key, CertFp).IsValid);
        var replay = validator.Validate(challenge, hello, Key, CertFp);
        Assert.Equal(ErrorCodes.ReplayDetected, replay.ErrorCode);
    }

    [Fact]
    public void Forged_challenge_does_not_poison_the_replay_cache()
    {
        var hello = Hello();
        var validator = NewValidator();
        var nonce = HandshakeCrypto.NewNonce();
        var forged = Challenge(hello, ClassroomCode.DeriveKey("CLASS-ZZZZ-9999"), nonce: nonce);
        Assert.False(validator.Validate(forged, hello, Key, CertFp).IsValid);
        Assert.True(validator.Validate(Challenge(hello, nonce: nonce), hello, Key, CertFp).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("AAAA")]
    public void Malformed_challenge_nonce_is_rejected(string nonce)
    {
        var hello = Hello();
        var result = NewValidator().Validate(Challenge(hello, nonce: nonce), hello, Key, CertFp);
        Assert.Equal(ErrorCodes.InvalidMessage, result.ErrorCode);
    }

    [Fact]
    public void Incompatible_major_version_gives_a_clear_message()
    {
        var hello = Hello();
        var result = NewValidator().Validate(Challenge(hello, version: "2.0"), hello, Key, CertFp);
        Assert.Equal(ErrorCodes.VersionMismatch, result.ErrorCode);
        Assert.Contains("2.0", result.Message, StringComparison.Ordinal);
        Assert.Contains("1.0", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Newer_minor_version_is_negotiated_down()
    {
        var hello = Hello();
        var result = NewValidator().Validate(Challenge(hello, version: "1.3"), hello, Key, CertFp);
        Assert.True(result.IsValid);
        Assert.Equal(new ProtocolVersion(1, 0), result.Negotiated);
    }

    [Fact]
    public void Proofs_are_deterministic_and_input_sensitive()
    {
        var a = HandshakeCrypto.ClientProof(Key, Device, "n1", "n2", CertFp, 1);
        Assert.Equal(a, HandshakeCrypto.ClientProof(Key, Device, "n1", "n2", CertFp, 1));
        Assert.NotEqual(a, HandshakeCrypto.ClientProof(Key, Device, "n2", "n1", CertFp, 1));
        Assert.NotEqual(a, HandshakeCrypto.ClientProof(Key, Device + "x", "n1", "n2", CertFp, 1));
        Assert.False(HandshakeCrypto.ProofEquals(a, "%%%"));
        Assert.False(HandshakeCrypto.ProofEquals(a, null));
    }

    [Theory]
    [InlineData("CLASS-8F4K-2026", true)]
    [InlineData("class-8f4k-2026", true)]
    [InlineData("  CLASS-8F4K-2026 ", true)]
    [InlineData("CLASS-8F4-2026", false)]
    [InlineData("KLASS-8F4K-2026", false)]
    [InlineData("CLASS-8F4K-20X6", false)]
    [InlineData("", false)]
    public void Classroom_code_format(string code, bool valid) => Assert.Equal(valid, ClassroomCode.IsValidFormat(code));

    [Fact]
    public void Replay_guard_forgets_ids_after_the_window()
    {
        var guard = new ReplayGuard(TimeSpan.FromMinutes(1));
        var t = _time.GetUtcNow();
        Assert.True(guard.TryRegister("a", t));
        Assert.False(guard.TryRegister("a", t.AddSeconds(30)));
        Assert.True(guard.TryRegister("a", t.AddMinutes(2)));
    }

    // ---- Session-level (post-authentication) protection ----

    private (SecureChannel student, SecureChannel teacher, MemoryChannelPair pipe) Session(string sessionId = "session-1")
    {
        var keys = HandshakeCrypto.DeriveSessionKeys(Key, "ns", "nt", sessionId);
        var pipe = new MemoryChannelPair();
        var skew = TimeSpan.FromMinutes(2);
        var student = new SecureChannel(pipe.A, sessionId, keys.ClientToTeacher, keys.TeacherToClient, _time, skew, new ProtocolVersion(1, 0));
        var teacher = new SecureChannel(pipe.B, sessionId, keys.TeacherToClient, keys.ClientToTeacher, _time, skew, new ProtocolVersion(1, 0));
        return (student, teacher, pipe);
    }

    [Fact]
    public async Task Signed_messages_roundtrip_in_both_directions()
    {
        var (student, teacher, _) = Session();
        await student.SendAsync(MessageTypes.Heartbeat, new HeartbeatMessage(Device, "session-1", 1, "Approved"), default);
        var received = await teacher.ReceiveAsync(default);
        Assert.Equal(MessageTypes.Heartbeat, received!.Type);
        Assert.Contains(Device, received.Payload, StringComparison.Ordinal);

        await teacher.SendAsync(MessageTypes.HeartbeatAck, new { }, default);
        Assert.Equal(MessageTypes.HeartbeatAck, (await student.ReceiveAsync(default))!.Type);
    }

    [Fact]
    public async Task Tampered_payload_fails_signature_check()
    {
        var (student, teacher, pipe) = Session();
        await student.SendAsync(MessageTypes.Command, new { Name = "Ping" }, default);
        var wire = MessageSerializer.DeserializeWire((await pipe.B.ReadFrameAsync(default))!);
        wire.Payload = wire.Payload.Replace("Ping", "Pong", StringComparison.Ordinal);
        await pipe.A.WriteFrameAsync(MessageSerializer.Serialize(wire), default);

        var ex = await Assert.ThrowsAsync<ProtocolException>(() => teacher.ReceiveAsync(default));
        Assert.Equal(ErrorCodes.InvalidSignature, ex.ErrorCode);
    }

    [Fact]
    public async Task Replayed_message_is_detected()
    {
        var (student, teacher, pipe) = Session();
        await student.SendAsync(MessageTypes.Heartbeat, new HeartbeatMessage(Device, "session-1", 1, "x"), default);
        var frame = await pipe.B.ReadFrameAsync(default);
        // Deliver the very same frame twice.
        await pipe.A.WriteFrameAsync(frame!, default);
        await pipe.A.WriteFrameAsync(frame!, default);
        Assert.NotNull(await teacher.ReceiveAsync(default));
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => teacher.ReceiveAsync(default));
        Assert.Equal(ErrorCodes.ReplayDetected, ex.ErrorCode);
    }

    [Fact]
    public async Task Message_for_another_session_is_rejected()
    {
        var (student, _, pipe) = Session("session-1");
        var (_, otherTeacher, otherPipe) = Session("session-2");
        await student.SendAsync(MessageTypes.Heartbeat, new HeartbeatMessage(Device, "session-1", 1, "x"), default);
        await otherPipe.A.WriteFrameAsync((await pipe.B.ReadFrameAsync(default))!, default);

        var ex = await Assert.ThrowsAsync<ProtocolException>(() => otherTeacher.ReceiveAsync(default));
        Assert.Equal(ErrorCodes.SessionMismatch, ex.ErrorCode);
    }

    [Fact]
    public async Task Message_with_a_stale_timestamp_is_rejected()
    {
        var (student, teacher, _) = Session();
        await student.SendAsync(MessageTypes.Heartbeat, new HeartbeatMessage(Device, "session-1", 1, "x"), default);
        _time.Advance(TimeSpan.FromMinutes(10));
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => teacher.ReceiveAsync(default));
        Assert.Equal(ErrorCodes.InvalidTimestamp, ex.ErrorCode);
    }

    [Fact]
    public async Task Own_message_reflected_back_is_rejected()
    {
        var (student, _, pipe) = Session();
        await student.SendAsync(MessageTypes.Heartbeat, new HeartbeatMessage(Device, "session-1", 1, "x"), default);
        var frame = await pipe.B.ReadFrameAsync(default);
        await pipe.B.WriteFrameAsync(frame!, default); // an attacker bounces the student's own message back
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => student.ReceiveAsync(default));
        Assert.Equal(ErrorCodes.InvalidSignature, ex.ErrorCode);
    }
}
