using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using ClassroomControl.Shared.Communication.Messages;
using ClassroomControl.Shared.Communication.Protocol;
using ClassroomControl.Shared.Communication.Security;

namespace ClassroomControl.Shared.Files;

/// <summary>Where file-transfer messages are written to (the authenticated channel of one connection).</summary>
public interface IFileMessageSink
{
    Task SendAsync<T>(string type, T payload, CancellationToken cancellationToken) where T : class;
}

public sealed class SecureChannelSink : IFileMessageSink
{
    private readonly SecureChannel _channel;

    public SecureChannelSink(SecureChannel channel) => _channel = channel;

    public Task SendAsync<T>(string type, T payload, CancellationToken cancellationToken) where T : class => _channel.SendAsync(type, payload, cancellationToken);
}

/// <summary>Both directions of file transfer on one connection: answers offers of the peer (through <see cref="FileReceiver"/>) and
/// sends files of its own owner. The same class runs on the Teacher (one per computer) and on the Student (one per session).</summary>
public sealed class FileTransferCoordinator : IDisposable
{
    private sealed class Outgoing
    {
        public readonly TaskCompletionSource<FileReplyMessage> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<FileResultMessage> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(60);

    private readonly FileReceiver _receiver;
    private readonly IFileMessageSink _sink;
    private readonly Func<FileTransferPolicy> _policyProvider;
    private readonly ConcurrentDictionary<string, Outgoing> _outgoing = new();

    public FileTransferCoordinator(FileReceiver receiver, IFileMessageSink sink, FileTransferPolicy policy)
        : this(receiver, sink, () => policy)
    {
    }

    public FileTransferCoordinator(FileReceiver receiver, IFileMessageSink sink, Func<FileTransferPolicy> policyProvider)
    {
        _receiver = receiver;
        _sink = sink;
        _policyProvider = policyProvider;
        _receiver.Progress += OnReceiverProgress;
    }

    /// <summary>Progress of both incoming and outgoing transfers. May be raised from any thread.</summary>
    public event Action<TransferProgress>? Progress;

    private FileTransferPolicy _policy => _policyProvider();

    public FileReceiver Receiver => _receiver;

    /// <summary>Answers an offer with a refusal without involving the receiver (for example when this side does not accept files at all).</summary>
    public Task RefuseOfferAsync(string transferId, string errorCode, string message, CancellationToken ct) =>
        _sink.SendAsync(MessageTypes.FileReply, new FileReplyMessage(transferId, false, errorCode, message), ct);

    public static bool IsFileMessage(string type) =>
        type is MessageTypes.FileOffer or MessageTypes.FileReply or MessageTypes.FileChunk or MessageTypes.FileEnd or MessageTypes.FileCancel or MessageTypes.FileResult;

    private void OnReceiverProgress(TransferProgress progress) => Progress?.Invoke(progress);

    /// <summary>Processes one verified file-transfer message of the peer. Throws <see cref="ProtocolException"/> only for malformed messages.</summary>
    public async Task HandleAsync(VerifiedMessage message, CancellationToken ct)
    {
        switch (message.Type)
        {
            case MessageTypes.FileOffer:
                var reply = _receiver.Offer(MessageSerializer.Deserialize<FileOfferMessage>(message.Payload));
                await _sink.SendAsync(MessageTypes.FileReply, reply, ct).ConfigureAwait(false);
                break;

            case MessageTypes.FileChunk:
                var failure = _receiver.Chunk(MessageSerializer.Deserialize<FileChunkMessage>(message.Payload));
                if (failure is not null) await _sink.SendAsync(MessageTypes.FileResult, failure, ct).ConfigureAwait(false);
                break;

            case MessageTypes.FileEnd:
                var (result, _) = _receiver.End(MessageSerializer.Deserialize<FileEndMessage>(message.Payload));
                await _sink.SendAsync(MessageTypes.FileResult, result, ct).ConfigureAwait(false);
                break;

            case MessageTypes.FileCancel:
                _receiver.Cancel(MessageSerializer.Deserialize<FileCancelMessage>(message.Payload).TransferId);
                break;

            case MessageTypes.FileReply:
                var answer = MessageSerializer.Deserialize<FileReplyMessage>(message.Payload);
                if (_outgoing.TryGetValue(answer.TransferId ?? string.Empty, out var waiting)) waiting.Reply.TrySetResult(answer);
                break;

            case MessageTypes.FileResult:
                var verdict = MessageSerializer.Deserialize<FileResultMessage>(message.Payload);
                if (_outgoing.TryGetValue(verdict.TransferId ?? string.Empty, out var pending))
                {
                    pending.Reply.TrySetResult(new FileReplyMessage(verdict.TransferId!, verdict.Success, verdict.ErrorCode, verdict.Message));
                    pending.Result.TrySetResult(verdict);
                }
                break;
        }
    }

    /// <summary>Sends one file to the peer and returns the final state (Completed, Failed or Cancelled). Never throws for transfer problems.</summary>
    public async Task<TransferProgress> SendFileAsync(string filePath, CancellationToken ct)
    {
        var name = FileNameSanitizer.Sanitize(Path.GetFileName(filePath));
        var id = Guid.NewGuid().ToString("N");
        long size = 0;
        long sent = 0;

        TransferProgress Report(TransferState state, string? code = null, string? message = null)
        {
            var progress = new TransferProgress(id, TransferDirection.Sending, name, size, sent, state, code, message, filePath);
            Progress?.Invoke(progress);
            return progress;
        }

        var outgoing = new Outgoing();
        _outgoing[id] = outgoing;
        var offered = false;
        try
        {
            FileInfo info;
            try
            {
                info = new FileInfo(filePath);
                if (!info.Exists) return Report(TransferState.Failed, ErrorCodes.FileNotFound, "Fayl topilmadi.");
                size = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Report(TransferState.Failed, ErrorCodes.FileNotFound, "Faylni ochib bo‘lmadi.");
            }

            if (size > _policy.MaxFileBytes) return Report(TransferState.Failed, ErrorCodes.FileTooLarge, $"Fayl juda katta (ruxsat etilgan: {_policy.MaxFileBytes / (1024 * 1024)} MB).");
            if (_policy.IsBlocked(name)) return Report(TransferState.Failed, ErrorCodes.FileTypeBlocked, $"Bu turdagi fayl yuborilmaydi: {Path.GetExtension(name)}");

            Report(TransferState.Preparing);
            var digest = await HashAsync(filePath, ct).ConfigureAwait(false);

            await _sink.SendAsync(MessageTypes.FileOffer, new FileOfferMessage(id, name, size, digest), ct).ConfigureAwait(false);
            offered = true;
            Report(TransferState.Waiting);
            var reply = await outgoing.Reply.Task.WaitAsync(ReplyTimeout, ct).ConfigureAwait(false);
            if (!reply.Accepted) return Report(TransferState.Failed, reply.ErrorCode ?? ErrorCodes.FileRejected, reply.Message ?? "Qabul qiluvchi faylni rad etdi.");

            var chunks = 0;
            await using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[FileTransferLimits.ChunkBytes];
                Report(TransferState.Transferring);
                while (true)
                {
                    if (outgoing.Result.Task.IsCompleted) break; // the receiver stopped the transfer
                    var read = await ReadFullAsync(stream, buffer, ct).ConfigureAwait(false);
                    if (read == 0) break;
                    await _sink.SendAsync(MessageTypes.FileChunk, new FileChunkMessage(id, chunks++, Convert.ToBase64String(buffer, 0, read)), ct).ConfigureAwait(false);
                    sent += read;
                    Report(TransferState.Transferring);
                }
            }

            if (!outgoing.Result.Task.IsCompleted)
                await _sink.SendAsync(MessageTypes.FileEnd, new FileEndMessage(id, chunks), ct).ConfigureAwait(false);
            var verdict = await outgoing.Result.Task.WaitAsync(ResultTimeout, ct).ConfigureAwait(false);
            if (verdict.Success)
            {
                sent = size;
                return Report(TransferState.Completed, null, verdict.SavedName);
            }
            return Report(TransferState.Failed, verdict.ErrorCode ?? ErrorCodes.FileRejected, verdict.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (offered) await TryCancelAsync(id).ConfigureAwait(false);
            return Report(TransferState.Cancelled, ErrorCodes.FileCancelled, "Uzatish bekor qilindi.");
        }
        catch (TimeoutException)
        {
            if (offered) await TryCancelAsync(id).ConfigureAwait(false);
            return Report(TransferState.Failed, ErrorCodes.TransferTimeout, "Qabul qiluvchi javob bermadi.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or UnauthorizedAccessException)
        {
            return Report(TransferState.Failed, ErrorCodes.ConnectionLost, "Aloqa uzildi yoki faylni o‘qib bo‘lmadi.");
        }
        finally
        {
            _outgoing.TryRemove(id, out _);
        }
    }

    private async Task TryCancelAsync(string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _sink.SendAsync(MessageTypes.FileCancel, new FileCancelMessage(id, null), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The connection is gone anyway; the receiver cleans up on its own.
        }
    }

    /// <summary>Connection ended: waiting senders fail, partially received files are deleted.</summary>
    public void Abort()
    {
        foreach (var outgoing in _outgoing.Values)
        {
            outgoing.Reply.TrySetException(new IOException("Connection lost."));
            outgoing.Result.TrySetException(new IOException("Connection lost."));
        }
        _receiver.CancelAll();
    }

    public void Dispose()
    {
        _receiver.Progress -= OnReceiverProgress;
        Abort();
        _receiver.Dispose();
    }

    private static Task<string> HashAsync(string path, CancellationToken ct) => FileDigestCache.GetAsync(path, ct);

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
