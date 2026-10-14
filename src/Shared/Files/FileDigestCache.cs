using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ClassroomControl.Shared.Files;

/// <summary>SHA-256 of a file, remembered while the file is unchanged (same length and write time). Sending one file to many
/// computers therefore reads it for hashing only once.</summary>
public static class FileDigestCache
{
    private sealed record Key(string Path, long Length, long WriteTicks);

    private static readonly ConcurrentDictionary<Key, Lazy<Task<string>>> Cache = new();
    private const int MaxEntries = 64;

    public static Task<string> GetAsync(string path, CancellationToken ct)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return ComputeAsync(path, ct); // throws the proper exception
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ComputeAsync(path, ct);
        }

        var key = new Key(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
        if (Cache.Count > MaxEntries) Cache.Clear();
        // A cancelled caller must not cancel the hash other callers wait for, so the shared task runs without a token.
        var entry = Cache.GetOrAdd(key, _ => new Lazy<Task<string>>(() => ComputeAsync(info.FullName, CancellationToken.None)));
        var task = entry.Value;
        if (task.IsFaulted) Cache.TryRemove(key, out _);
        return task.WaitAsync(ct);
    }

    private static async Task<string> ComputeAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }
}
