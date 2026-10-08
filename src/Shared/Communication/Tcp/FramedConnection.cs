using System.Buffers.Binary;
using System.Text;
using ClassroomControl.Shared.Communication.Protocol;

namespace ClassroomControl.Shared.Communication.Tcp;

public interface IFramedChannel : IAsyncDisposable
{
    /// <summary>Reads one frame. Returns null when the peer closed the connection cleanly.</summary>
    Task<string?> ReadFrameAsync(CancellationToken cancellationToken);
    Task WriteFrameAsync(string frame, CancellationToken cancellationToken);
}

/// <summary>Length-prefixed (4-byte big-endian) UTF-8 frames over a stream (TLS in production).</summary>
public sealed class FramedConnection : IFramedChannel
{
    private readonly Stream _stream;
    private readonly int _maxFrameBytes;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public FramedConnection(Stream stream, int maxFrameBytes = ProtocolConstants.MaxFrameBytes)
    {
        _stream = stream;
        _maxFrameBytes = maxFrameBytes;
    }

    public async Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = await ReadExactAsync(header, cancellationToken).ConfigureAwait(false);
        if (read == 0) return null;
        if (read < header.Length) throw new EndOfStreamException("Connection closed in the middle of a frame header.");

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > _maxFrameBytes)
            throw new ProtocolException(ErrorCodes.InvalidMessage, $"Frame length {length} is outside the allowed range.");

        var body = new byte[length];
        if (await ReadExactAsync(body, cancellationToken).ConfigureAwait(false) < length)
            throw new EndOfStreamException("Connection closed in the middle of a frame.");
        return Encoding.UTF8.GetString(body);
    }

    public async Task WriteFrameAsync(string frame, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(frame);
        if (body.Length > _maxFrameBytes)
            throw new ProtocolException(ErrorCodes.InvalidMessage, "Outgoing frame is too large.");
        var buffer = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer, body.Length);
        body.CopyTo(buffer, 4);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<int> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }
}
