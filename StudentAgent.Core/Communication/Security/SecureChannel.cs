using System.Globalization;
using ClassroomControl.StudentAgent.Communication.Messages;
using ClassroomControl.StudentAgent.Communication.Protocol;
using ClassroomControl.StudentAgent.Communication.Tcp;

namespace ClassroomControl.StudentAgent.Communication.Security;

public sealed record VerifiedMessage(string Type, string SessionId, string Payload);

/// <summary>Authenticated message layer on top of a framed TLS connection. Every message carries session id, message id,
/// strictly increasing sequence, timestamp and an HMAC signature (direction-specific key). Receiving enforces:
/// session id match, signature, timestamp window, sequence monotonicity and message-id uniqueness.</summary>
public sealed class SecureChannel : IAsyncDisposable
{
    private readonly IFramedChannel _framed;
    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;
    private readonly TimeProvider _time;
    private readonly TimeSpan _maxSkew;
    private readonly ReplayGuard _replay;
    private readonly string _version;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private long _sendSequence;
    private long _receiveSequence;

    public string SessionId { get; }

    public SecureChannel(IFramedChannel framed, string sessionId, byte[] sendKey, byte[] receiveKey,
        TimeProvider time, TimeSpan maxSkew, ProtocolVersion negotiated)
    {
        _framed = framed;
        SessionId = sessionId;
        _sendKey = sendKey;
        _receiveKey = receiveKey;
        _time = time;
        _maxSkew = maxSkew;
        _replay = new ReplayGuard(maxSkew + maxSkew);
        _version = negotiated.ToString();
    }

    public async Task SendAsync<T>(string type, T payload, CancellationToken cancellationToken) where T : class
    {
        // Sequence assignment and the write must be atomic, otherwise concurrent senders could reach the peer out of order.
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendCoreAsync(type, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private Task SendCoreAsync<T>(string type, T payload, CancellationToken cancellationToken) where T : class
    {
        var message = new WireMessage
        {
            ProtocolVersion = _version,
            Type = type,
            SessionId = SessionId,
            MessageId = Guid.NewGuid().ToString("N"),
            Sequence = ++_sendSequence,
            Timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds(),
            Payload = MessageSerializer.Serialize(payload),
        };
        message.Signature = Sign(_sendKey, message);
        return _framed.WriteFrameAsync(MessageSerializer.Serialize(message), cancellationToken);
    }

    /// <summary>Returns null on clean close. Throws <see cref="ProtocolException"/> for any security violation.</summary>
    public async Task<VerifiedMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var frame = await _framed.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null) return null;

        var message = MessageSerializer.DeserializeWire(frame);
        if (!string.Equals(message.SessionId, SessionId, StringComparison.Ordinal))
            throw new ProtocolException(ErrorCodes.SessionMismatch, "Message belongs to a different session.");
        if (!HandshakeCrypto.ProofEquals(Sign(_receiveKey, message), message.Signature))
            throw new ProtocolException(ErrorCodes.InvalidSignature, "Message signature is invalid.");

        var now = _time.GetUtcNow();
        var sent = DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp);
        if ((now - sent).Duration() > _maxSkew)
            throw new ProtocolException(ErrorCodes.InvalidTimestamp, "Message timestamp is outside the allowed window.");

        if (message.Sequence <= _receiveSequence || !_replay.TryRegister(message.MessageId, now))
            throw new ProtocolException(ErrorCodes.ReplayDetected, "Replayed or out-of-order message.");
        _receiveSequence = message.Sequence;

        return new VerifiedMessage(message.Type, message.SessionId!, message.Payload);
    }

    private static string Sign(byte[] key, WireMessage m) => HandshakeCrypto.Sign(key,
        m.ProtocolVersion, m.Type, m.SessionId ?? string.Empty, m.MessageId,
        m.Sequence.ToString(CultureInfo.InvariantCulture), m.Timestamp.ToString(CultureInfo.InvariantCulture), m.Payload);

    public ValueTask DisposeAsync() => _framed.DisposeAsync();
}
