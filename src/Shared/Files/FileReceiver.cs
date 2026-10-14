using System.Security.Cryptography;
using ClassroomControl.Shared.Communication.Messages;
using ClassroomControl.Shared.Communication.Protocol;

namespace ClassroomControl.Shared.Files;

/// <summary>Receiving side of a file transfer. Validates the offer against the local policy, writes to a hidden partial file,
/// verifies size and SHA-256 and only then moves the file into the receive folder under a safe, unique name.
/// The sender never chooses the folder, and an existing file is never overwritten.</summary>
public sealed class FileReceiver : IDisposable
{
    private sealed class Incoming
    {
        public required string Id { get; init; }
        public required FileOfferMessage Offer { get; init; }
        public required string SafeName { get; init; }
        public required string PartPath { get; init; }
        public required FileStream Stream { get; init; }
        public required IncrementalHash Hash { get; init; }
        public long Received;
        public int NextIndex;
        public DateTimeOffset LastActivity;
    }

    private const string PartialFolder = ".incoming";
    private readonly object _gate = new();
    private readonly Dictionary<string, Incoming> _active = new(StringComparer.Ordinal);
    private readonly Func<FileTransferPolicy> _policyProvider;
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Func<string, long?> _freeSpace;

    public FileReceiver(FileTransferPolicy policy, string directory, TimeProvider time, Func<string, long?>? freeSpace = null)
        : this(() => policy, directory, time, freeSpace)
    {
    }

    /// <summary>The policy is read for every offer, so a changed setting applies to the next file without reconnecting.</summary>
    public FileReceiver(Func<FileTransferPolicy> policyProvider, string directory, TimeProvider time, Func<string, long?>? freeSpace = null)
    {
        _policyProvider = policyProvider;
        _directory = directory;
        _time = time;
        _freeSpace = freeSpace ?? DefaultFreeSpace;
    }

    public event Action<TransferProgress>? Progress;

    private FileTransferPolicy _policy => _policyProvider();

    public string Directory => _directory;

    public int ActiveCount
    {
        get { lock (_gate) return _active.Count; }
    }

    public FileReplyMessage Offer(FileOfferMessage offer)
    {
        var events = new List<TransferProgress>();
        FileReplyMessage reply;
        lock (_gate)
        {
            Sweep(events);
            reply = OfferCore(offer, events);
        }
        Raise(events);
        return reply;
    }

    private FileReplyMessage OfferCore(FileOfferMessage offer, List<TransferProgress> events)
    {
        var id = offer.TransferId ?? string.Empty;
        var name = FileNameSanitizer.Sanitize(offer.FileName);
        if (!IsValidId(id) || _active.ContainsKey(id)) return Reject(id, ErrorCodes.InvalidMessage, "Noto‘g‘ri uzatish identifikatori.");
        if (offer.Size < 0 || offer.Size > _policy.MaxFileBytes)
            return Reject(id, ErrorCodes.FileTooLarge, $"Fayl juda katta (ruxsat etilgan: {_policy.MaxFileBytes / (1024 * 1024)} MB).");
        if (_policy.IsBlocked(name)) return Reject(id, ErrorCodes.FileTypeBlocked, $"Bu turdagi fayl qabul qilinmaydi: {Path.GetExtension(name)}");
        if (!IsSha256(offer.Sha256)) return Reject(id, ErrorCodes.InvalidMessage, "Fayl nazorat yig‘indisi noto‘g‘ri.");
        if (_active.Count >= _policy.MaxConcurrentTransfers) return Reject(id, ErrorCodes.TooManyTransfers, "Bir vaqtda juda ko‘p fayl uzatilmoqda.");
        if (_freeSpace(_directory) is { } free && free < offer.Size + _policy.MinFreeBytes)
            return Reject(id, ErrorCodes.FileNoSpace, "Diskda yetarli joy yo‘q.");

        FileStream stream;
        var partPath = Path.Combine(_directory, PartialFolder, id + ".part");
        try
        {
            System.IO.Directory.CreateDirectory(Path.Combine(_directory, PartialFolder));
            stream = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Reject(id, ErrorCodes.FileRejected, "Qabul papkasiga yozib bo‘lmadi.");
        }

        _active[id] = new Incoming
        {
            Id = id,
            Offer = offer with { Sha256 = offer.Sha256.ToLowerInvariant() },
            SafeName = name,
            PartPath = partPath,
            Stream = stream,
            Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            LastActivity = _time.GetUtcNow(),
        };
        events.Add(new TransferProgress(id, TransferDirection.Receiving, name, offer.Size, 0, TransferState.Transferring));
        return new FileReplyMessage(id, true, null, null);
    }

    /// <summary>Stores one chunk. Returns null when everything is fine; otherwise the transfer was aborted and the returned verdict must be sent to the sender.</summary>
    public FileResultMessage? Chunk(FileChunkMessage chunk)
    {
        var events = new List<TransferProgress>();
        FileResultMessage? failure;
        lock (_gate)
        {
            Sweep(events);
            failure = ChunkCore(chunk, events);
        }
        Raise(events);
        return failure;
    }

    private FileResultMessage? ChunkCore(FileChunkMessage chunk, List<TransferProgress> events)
    {
        var id = chunk.TransferId ?? string.Empty;
        if (!_active.TryGetValue(id, out var incoming))
            return new FileResultMessage(id, false, ErrorCodes.FileRejected, "Noma’lum yoki to‘xtatilgan uzatish.", null);
        if (chunk.Index != incoming.NextIndex) return Abort(incoming, TransferState.Failed, ErrorCodes.FileCorrupt, "Bo‘laklar tartibi buzildi.", events);
        if (string.IsNullOrEmpty(chunk.DataBase64) || chunk.DataBase64.Length > FileTransferLimits.MaxChunkBytes / 3 * 4 + 8)
            return Abort(incoming, TransferState.Failed, ErrorCodes.FileCorrupt, "Bo‘lak o‘lchami noto‘g‘ri.", events);

        byte[] data;
        try
        {
            data = Convert.FromBase64String(chunk.DataBase64);
        }
        catch (FormatException)
        {
            return Abort(incoming, TransferState.Failed, ErrorCodes.FileCorrupt, "Bo‘lak ma’lumoti buzilgan.", events);
        }
        if (data.Length > FileTransferLimits.MaxChunkBytes || incoming.Received + data.Length > incoming.Offer.Size)
            return Abort(incoming, TransferState.Failed, ErrorCodes.FileCorrupt, "Fayl e’lon qilingan o‘lchamdan katta.", events);

        try
        {
            incoming.Stream.Write(data);
        }
        catch (IOException)
        {
            return Abort(incoming, TransferState.Failed, ErrorCodes.FileNoSpace, "Faylni yozib bo‘lmadi (disk to‘lgan bo‘lishi mumkin).", events);
        }
        incoming.Hash.AppendData(data);
        incoming.Received += data.Length;
        incoming.NextIndex++;
        incoming.LastActivity = _time.GetUtcNow();
        events.Add(new TransferProgress(id, TransferDirection.Receiving, incoming.SafeName, incoming.Offer.Size, incoming.Received, TransferState.Transferring));
        return null;
    }

    /// <summary>Verifies the finished file and moves it into the receive folder.</summary>
    public (FileResultMessage Result, string? Path) End(FileEndMessage end)
    {
        var events = new List<TransferProgress>();
        (FileResultMessage, string?) outcome;
        lock (_gate)
        {
            Sweep(events);
            outcome = EndCore(end, events);
        }
        Raise(events);
        return outcome;
    }

    private (FileResultMessage, string?) EndCore(FileEndMessage end, List<TransferProgress> events)
    {
        var id = end.TransferId ?? string.Empty;
        if (!_active.TryGetValue(id, out var incoming))
            return (new FileResultMessage(id, false, ErrorCodes.FileRejected, "Noma’lum yoki to‘xtatilgan uzatish.", null), null);
        if (end.ChunkCount != incoming.NextIndex || incoming.Received != incoming.Offer.Size)
            return (Abort(incoming, TransferState.Failed, ErrorCodes.FileCorrupt, "Fayl to‘liq kelmadi.", events), null);

        string finalPath;
        try
        {
            incoming.Stream.Flush();
            incoming.Stream.Dispose();
            var digest = Convert.ToHexString(incoming.Hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(digest, incoming.Offer.Sha256, StringComparison.Ordinal))
                return (Abort(incoming, TransferState.Failed, ErrorCodes.FileCorrupt, "Fayl nazorat yig‘indisi mos kelmadi (SHA-256).", events), null);
            finalPath = FileNameSanitizer.CreateUniqueFile(_directory, incoming.SafeName);
            File.Move(incoming.PartPath, finalPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (Abort(incoming, TransferState.Failed, ErrorCodes.FileRejected, "Faylni saqlab bo‘lmadi.", events), null);
        }

        incoming.Hash.Dispose();
        _active.Remove(id);
        events.Add(new TransferProgress(id, TransferDirection.Receiving, incoming.SafeName, incoming.Offer.Size, incoming.Received, TransferState.Completed, null, null, finalPath));
        return (new FileResultMessage(id, true, null, null, Path.GetFileName(finalPath)), finalPath);
    }

    public void Cancel(string transferId, string? reason = null)
    {
        var events = new List<TransferProgress>();
        lock (_gate)
        {
            if (_active.TryGetValue(transferId, out var incoming)) Abort(incoming, TransferState.Cancelled, ErrorCodes.FileCancelled, reason ?? "Uzatish bekor qilindi.", events);
        }
        Raise(events);
    }

    /// <summary>Aborts everything in progress (connection lost, session ended) and removes the partial files.</summary>
    public void CancelAll(string errorCode = ErrorCodes.ConnectionLost, string message = "Aloqa uzildi.")
    {
        var events = new List<TransferProgress>();
        lock (_gate)
        {
            foreach (var incoming in _active.Values.ToList()) Abort(incoming, TransferState.Failed, errorCode, message, events);
        }
        Raise(events);
    }

    /// <summary>Removes partial files that an earlier run left behind (crash, power loss).</summary>
    public void DeleteStalePartialFiles(TimeSpan olderThan)
    {
        var folder = Path.Combine(_directory, PartialFolder);
        if (!System.IO.Directory.Exists(folder)) return;
        foreach (var file in System.IO.Directory.EnumerateFiles(folder, "*.part"))
        {
            try
            {
                lock (_gate)
                {
                    if (_active.Values.Any(a => a.PartPath == file)) continue;
                }
                if (_time.GetUtcNow() - File.GetLastWriteTimeUtc(file) > olderThan) File.Delete(file);
            }
            catch (IOException) { /* in use: leave it */ }
            catch (UnauthorizedAccessException) { /* not ours to delete */ }
        }
    }

    public void Dispose() => CancelAll();

    private void Sweep(List<TransferProgress> events)
    {
        var now = _time.GetUtcNow();
        foreach (var incoming in _active.Values.Where(a => now - a.LastActivity > _policy.IdleTimeout).ToList())
            Abort(incoming, TransferState.Failed, ErrorCodes.TransferTimeout, "Uzatish to‘xtab qoldi.", events);
    }

    private FileResultMessage Abort(Incoming incoming, TransferState state, string code, string message, List<TransferProgress> events)
    {
        _active.Remove(incoming.Id);
        try { incoming.Stream.Dispose(); } catch (IOException) { }
        incoming.Hash.Dispose();
        try { File.Delete(incoming.PartPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        events.Add(new TransferProgress(incoming.Id, TransferDirection.Receiving, incoming.SafeName, incoming.Offer.Size, incoming.Received, state, code, message));
        return new FileResultMessage(incoming.Id, false, code, message, null);
    }

    private static FileReplyMessage Reject(string id, string code, string message) => new(id, false, code, message);

    private void Raise(List<TransferProgress> events)
    {
        foreach (var e in events) Progress?.Invoke(e);
    }

    private static bool IsValidId(string id) =>
        id.Length is > 0 and <= FileTransferLimits.MaxTransferIdLength && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    private static bool IsSha256(string? text) => text is { Length: 64 } && text.All(char.IsAsciiHexDigit);

    private static long? DefaultFreeSpace(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
