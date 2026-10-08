using ClassroomControl.Shared.Communication.Messages;
using ClassroomControl.Shared.Communication.Protocol;
using ClassroomControl.Shared.Communication.Security;
using ClassroomControl.Shared.Communication.Tcp;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.StudentAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassroomControl.StudentAgent.Services;

public sealed class AuthenticatedSession
{
    public AuthenticatedSession(SecureChannel channel, ClassroomInfo classroom, RegistrationState registration, AgentPolicy policy)
    {
        Channel = channel;
        Classroom = classroom;
        Registration = registration;
        Policy = policy;
    }

    public SecureChannel Channel { get; }
    public string SessionId => Channel.SessionId;
    public ClassroomInfo Classroom { get; }
    public RegistrationState Registration { get; }
    public AgentPolicy Policy { get; }
}

public sealed record AuthenticationResult(bool Success, string? ErrorCode, string? Message, AuthenticatedSession? Session)
{
    public static AuthenticationResult Failure(string code, string message) => new(false, code, message, null);
}

public interface IAuthenticationService
{
    Task<AuthenticationResult> AuthenticateAsync(TeacherConnection connection, StudentSettings settings, CancellationToken cancellationToken);
}

/// <summary>Mutual challenge-response authentication (see docs/PROTOCOL.md): HELLO → CHALLENGE → AUTH_RESPONSE → AUTH_RESULT.</summary>
public sealed class AuthenticationService : IAuthenticationService
{
    private readonly IClassroomKeyProvider _keys;
    private readonly IDeviceInfoService _device;
    private readonly TimeProvider _time;
    private readonly AgentOptions _options;
    private readonly ChallengeValidator _validator;
    private readonly ILogger<AuthenticationService> _logger;

    public AuthenticationService(IClassroomKeyProvider keys, IDeviceInfoService device, TimeProvider time,
        IOptions<AgentOptions> options, ILogger<AuthenticationService> logger)
    {
        _keys = keys;
        _device = device;
        _time = time;
        _options = options.Value;
        _logger = logger;
        _validator = new ChallengeValidator(new ReplayGuard(_options.MaxClockSkew + _options.MaxClockSkew), time, _options.MaxClockSkew);
    }

    public async Task<AuthenticationResult> AuthenticateAsync(TeacherConnection connection, StudentSettings settings, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Authentication Started");
        var key = _keys.GetKey(settings.ClassroomCode);
        if (key is null) return Fail(ErrorCodes.InvalidClassroomCode, "Classroom code noto‘g‘ri.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ConnectionTimeoutSeconds));
        var ct = timeout.Token;
        var channel = connection.Channel;

        try
        {
            var hello = new HelloMessage(settings.DeviceId, _device.AgentVersion, ProtocolConstants.Current.ToString(),
                _device.ComputerName, HandshakeCrypto.NewNonce(), _time.GetUtcNow().ToUnixTimeMilliseconds());
            await SendHandshakeAsync(channel, MessageTypes.Hello, hello, ct).ConfigureAwait(false);

            var challengeWire = await ReceiveHandshakeAsync(channel, ct).ConfigureAwait(false);
            if (challengeWire.Type == MessageTypes.AuthResult)
                return FailFromResult(MessageSerializer.Deserialize<AuthResultMessage>(challengeWire.Payload));
            if (challengeWire.Type != MessageTypes.Challenge)
                return Fail(ErrorCodes.InvalidMessage, "Teacher kutilmagan xabar yubordi.");

            var challenge = MessageSerializer.Deserialize<ChallengeMessage>(challengeWire.Payload);
            var validation = _validator.Validate(challenge, hello, key, connection.CertificateFingerprint);
            if (!validation.IsValid) return Fail(validation.ErrorCode!, validation.Message!);

            var response = new AuthResponseMessage(settings.DeviceId,
                HandshakeCrypto.ClientProof(key, settings.DeviceId, hello.Nonce, challenge.Challenge, connection.CertificateFingerprint, challenge.Timestamp),
                _device.ComputerName, settings.StudentName, _device.LocalIp,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription, _device.AgentVersion);
            await SendHandshakeAsync(channel, MessageTypes.AuthResponse, response, ct).ConfigureAwait(false);

            var resultWire = await ReceiveHandshakeAsync(channel, ct).ConfigureAwait(false);
            if (resultWire.Type != MessageTypes.AuthResult)
                return Fail(ErrorCodes.InvalidMessage, "Teacher kutilmagan xabar yubordi.");
            var result = MessageSerializer.Deserialize<AuthResultMessage>(resultWire.Payload);
            if (!result.Success) return FailFromResult(result);

            if (string.IsNullOrEmpty(result.SessionId) || result.SessionId.Length > 64)
                return Fail(ErrorCodes.InvalidMessage, "Teacher session identifikatori noto‘g‘ri.");
            var expectedProof = HandshakeCrypto.ResultProof(key, settings.DeviceId, hello.Nonce, challenge.Challenge,
                result.SessionId, result.Registration.ToString(), true);
            if (!HandshakeCrypto.ProofEquals(expectedProof, result.ResultProof))
                return Fail(ErrorCodes.InvalidSignature, "Teacher javobi imzosi noto‘g‘ri.");

            var keys = HandshakeCrypto.DeriveSessionKeys(key, hello.Nonce, challenge.Challenge, result.SessionId);
            var secure = new SecureChannel(channel, result.SessionId, keys.ClientToTeacher, keys.TeacherToClient,
                _time, _options.MaxClockSkew, validation.Negotiated);
            var classroom = new ClassroomInfo(challenge.ClassroomId, challenge.ClassroomName, challenge.TeacherId, challenge.TeacherName);

            _logger.LogInformation("Authentication Success (registration {Registration})", result.Registration);
            if (result.Registration == RegistrationState.Pending) _logger.LogInformation("Registration Request sent to Teacher");
            return new AuthenticationResult(true, null, null,
                new AuthenticatedSession(secure, classroom, result.Registration, result.Policy ?? new AgentPolicy(false)));
        }
        catch (ProtocolException ex)
        {
            return Fail(ex.ErrorCode, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(ErrorCodes.AuthenticationFailed, "Teacher autentifikatsiya vaqtida javob bermadi.");
        }
    }

    private AuthenticationResult FailFromResult(AuthResultMessage result)
    {
        var code = string.IsNullOrEmpty(result.ErrorCode) ? ErrorCodes.AuthenticationFailed : result.ErrorCode;
        var message = code switch
        {
            ErrorCodes.InvalidClassroomCode => "Classroom code noto‘g‘ri.",
            ErrorCodes.Rejected => "Teacher bu kompyuterni rad etdi.",
            ErrorCodes.VersionMismatch => result.Message ?? "Teacher va Student protocol versiyalari mos emas.",
            _ => result.Message ?? "Autentifikatsiya muvaffaqiyatsiz tugadi.",
        };
        return Fail(code, message);
    }

    private AuthenticationResult Fail(string code, string message)
    {
        if (code is ErrorCodes.InvalidSignature or ErrorCodes.ReplayDetected or ErrorCodes.InvalidClassroomCode)
            _logger.LogError("Security Error during authentication: {Code}", code);
        _logger.LogWarning("Authentication Failed: {Code}", code);
        return AuthenticationResult.Failure(code, message);
    }

    private static Task SendHandshakeAsync<T>(IFramedChannel channel, string type, T payload, CancellationToken ct) where T : class
    {
        var wire = new WireMessage
        {
            ProtocolVersion = ProtocolConstants.Current.ToString(),
            Type = type,
            MessageId = Guid.NewGuid().ToString("N"),
            Payload = MessageSerializer.Serialize(payload),
        };
        return channel.WriteFrameAsync(MessageSerializer.Serialize(wire), ct);
    }

    private static async Task<WireMessage> ReceiveHandshakeAsync(IFramedChannel channel, CancellationToken ct)
    {
        var frame = await channel.ReadFrameAsync(ct).ConfigureAwait(false)
                    ?? throw new ProtocolException(ErrorCodes.AuthenticationFailed, "Teacher ulanishni yopdi.");
        return MessageSerializer.DeserializeWire(frame);
    }
}
