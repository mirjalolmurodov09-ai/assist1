using System.Security.Cryptography;
using System.Threading.Channels;
using ClassroomControl.Shared.Communication.Messages;
using ClassroomControl.Shared.Communication.Protocol;
using ClassroomControl.Shared.Communication.Security;
using ClassroomControl.Shared.Files;
using Xunit;

namespace ClassroomControl.StudentAgent.Tests;

public sealed class FileNameSanitizerTests
{
    [Theory]
    [InlineData("hisobot.docx", "hisobot.docx")]
    [InlineData("..\\..\\Windows\\evil.txt", "evil.txt")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\x\\a.pdf", "a.pdf")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("NUL", "_NUL")]
    [InlineData("a:b?.txt", "a_b_.txt")]
    [InlineData(".hidden", "hidden")]
    [InlineData("..", "file")]
    [InlineData("   ", "file")]
    [InlineData("name. . ", "name")]
    [InlineData("rtl\u202Etxt.exe", "rtltxt.exe")]
    public void Names_from_the_network_become_one_safe_file_name(string input, string expected) =>
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));

    [Fact]
    public void Long_names_are_shortened_but_keep_their_extension()
    {
        var clean = FileNameSanitizer.Sanitize(new string('a', 500) + ".pdf");
        Assert.True(clean.Length <= FileTransferLimits.MaxFileNameLength);
        Assert.EndsWith(".pdf", clean);
    }

    [Fact]
    public void Existing_files_are_never_overwritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-unique-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = FileNameSanitizer.CreateUniqueFile(dir, "a.txt");
            var second = FileNameSanitizer.CreateUniqueFile(dir, "a.txt");
            var third = FileNameSanitizer.CreateUniqueFile(dir, "a.txt");
            Assert.Equal(["a.txt", "a (1).txt", "a (2).txt"], new[] { first, second, third }.Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>Two coordinators wired back to back: every message is serialized, verified-message shaped and handled in order by a pump task,
/// just like the receive loop of a real connection.</summary>
internal sealed class FilePair : IAsyncDisposable
{
    private sealed class PumpSink : IFileMessageSink
    {
        private readonly Channel<VerifiedMessage> _toPeer = Channel.CreateUnbounded<VerifiedMessage>();
        public ChannelReader<VerifiedMessage> Reader => _toPeer.Reader;
        public bool Broken;
        public Func<string, bool>? Drop;

        public Task SendAsync<T>(string type, T payload, CancellationToken cancellationToken) where T : class
        {
            if (Broken) throw new IOException("broken");
            if (Drop?.Invoke(type) == true) return Task.CompletedTask;
            _toPeer.Writer.TryWrite(new VerifiedMessage(type, "s", MessageSerializer.Serialize(payload)));
            return Task.CompletedTask;
        }
    }

    private readonly PumpSink _aToB = new(), _bToA = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _pumps;

    public string DirA { get; } = Path.Combine(Path.GetTempPath(), "cc-a-" + Guid.NewGuid().ToString("N"));
    public string DirB { get; } = Path.Combine(Path.GetTempPath(), "cc-b-" + Guid.NewGuid().ToString("N"));
    public FileTransferCoordinator A { get; }
    public FileTransferCoordinator B { get; }
    public ManualTimeProvider Time { get; } = new();
    public List<TransferProgress> ProgressA { get; } = [];
    public List<TransferProgress> ProgressB { get; } = [];

    public FilePair(FileTransferPolicy? policyA = null, FileTransferPolicy? policyB = null, Func<string, long?>? freeSpaceB = null)
    {
        policyA ??= new FileTransferPolicy();
        policyB ??= new FileTransferPolicy();
        A = new FileTransferCoordinator(new FileReceiver(policyA, DirA, Time), _aToB, policyA);
        B = new FileTransferCoordinator(new FileReceiver(policyB, DirB, Time, freeSpaceB), _bToA, policyB);
        A.Progress += p => { lock (ProgressA) ProgressA.Add(p); };
        B.Progress += p => { lock (ProgressB) ProgressB.Add(p); };
        _pumps = [Pump(_aToB.Reader, B), Pump(_bToA.Reader, A)];
    }

    public void Break() { _aToB.Broken = true; _bToA.Broken = true; }
    public void DropFromAToB(Func<string, bool> predicate) => _aToB.Drop = predicate;

    private Task Pump(ChannelReader<VerifiedMessage> reader, FileTransferCoordinator target) => Task.Run(async () =>
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(_cts.Token)) await target.HandleAsync(message, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // a real receive loop ends the connection here
    });

    public static string MakeFile(string dir, string name, int bytes)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        var data = new byte[bytes];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await Task.WhenAll(_pumps);
        A.Dispose();
        B.Dispose();
        foreach (var dir in new[] { DirA, DirB })
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

public sealed class FileTransferTests
{
    [Fact]
    public async Task A_multi_chunk_file_arrives_identical_in_both_directions()
    {
        await using var pair = new FilePair();
        var source = FilePair.MakeFile(pair.DirA + "-src", "hisobot.bin", 3 * FileTransferLimits.ChunkBytes + 12345);

        var sent = await pair.A.SendFileAsync(source, CancellationToken.None);
        Assert.Equal(TransferState.Completed, sent.State);
        var stored = Path.Combine(pair.DirB, "hisobot.bin");
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(stored));
        Assert.Empty(Directory.GetFiles(Path.Combine(pair.DirB, ".incoming")));
        lock (pair.ProgressB) Assert.Contains(pair.ProgressB, p => p.State == TransferState.Completed && p.Path == stored);

        // ... and back (the other direction uses the same code path).
        var back = FilePair.MakeFile(pair.DirB + "-src", "javob.txt", 5000);
        var reply = await pair.B.SendFileAsync(back, CancellationToken.None);
        Assert.Equal(TransferState.Completed, reply.State);
        Assert.Equal(File.ReadAllBytes(back), File.ReadAllBytes(Path.Combine(pair.DirA, "javob.txt")));
        Directory.Delete(pair.DirA + "-src", true);
        Directory.Delete(pair.DirB + "-src", true);
    }

    [Fact]
    public async Task Progress_is_reported_and_ends_completed()
    {
        await using var pair = new FilePair();
        var source = FilePair.MakeFile(pair.DirA + "-src", "p.bin", 2 * FileTransferLimits.ChunkBytes);
        await pair.A.SendFileAsync(source, CancellationToken.None);
        lock (pair.ProgressA)
        {
            Assert.Equal(TransferState.Preparing, pair.ProgressA[0].State);
            Assert.Contains(pair.ProgressA, p => p.State == TransferState.Transferring && p.Transferred > 0 && p.Transferred < p.Size);
            Assert.Equal(TransferState.Completed, pair.ProgressA[^1].State);
            Assert.Equal(1.0, pair.ProgressA[^1].Fraction);
        }
        Directory.Delete(pair.DirA + "-src", true);
    }

    [Fact]
    public async Task An_empty_file_can_be_sent()
    {
        await using var pair = new FilePair();
        var source = FilePair.MakeFile(pair.DirA + "-src", "bosh.txt", 0);
        Assert.Equal(TransferState.Completed, (await pair.A.SendFileAsync(source, CancellationToken.None)).State);
        Assert.Empty(File.ReadAllBytes(Path.Combine(pair.DirB, "bosh.txt")));
        Directory.Delete(pair.DirA + "-src", true);
    }

    [Fact]
    public async Task The_receiver_refuses_programs_even_when_the_sender_would_send_them()
    {
        await using var pair = new FilePair(policyA: new FileTransferPolicy { AllowExecutables = true });
        var source = FilePair.MakeFile(pair.DirA + "-src", "setup.exe", 1000);
        var result = await pair.A.SendFileAsync(source, CancellationToken.None);
        Assert.Equal((TransferState.Failed, ErrorCodes.FileTypeBlocked), (result.State, result.ErrorCode));
        Assert.False(Directory.Exists(pair.DirB) && Directory.GetFiles(pair.DirB).Length > 0);
        Directory.Delete(pair.DirA + "-src", true);
    }

    [Fact]
    public async Task The_sender_checks_its_own_policy_before_reading_the_file()
    {
        await using var pair = new FilePair();
        var source = FilePair.MakeFile(pair.DirA + "-src", "run.bat", 10);
        var result = await pair.A.SendFileAsync(source, CancellationToken.None);
        Assert.Equal(ErrorCodes.FileTypeBlocked, result.ErrorCode);
        Directory.Delete(pair.DirA + "-src", true);
    }

    [Fact]
    public async Task Files_above_the_receivers_limit_are_refused()
    {
        await using var pair = new FilePair(policyB: new FileTransferPolicy { MaxFileBytes = 1000 });
        var source = FilePair.MakeFile(pair.DirA + "-src", "katta.bin", 5000);
        Assert.Equal(ErrorCodes.FileTooLarge, (await pair.A.SendFileAsync(source, CancellationToken.None)).ErrorCode);
        Directory.Delete(pair.DirA + "-src", true);
    }

    [Fact]
    public async Task A_full_disk_is_reported_before_anything_is_written()
    {
        await using var pair = new FilePair(freeSpaceB: _ => 10);
        var source = FilePair.MakeFile(pair.DirA + "-src", "x.bin", 5000);
        Assert.Equal(ErrorCodes.FileNoSpace, (await pair.A.SendFileAsync(source, CancellationToken.None)).ErrorCode);
        Directory.Delete(pair.DirA + "-src", true);
    }

    [Fact]
    public async Task A_missing_file_fails_cleanly()
    {
        await using var pair = new FilePair();
        var result = await pair.A.SendFileAsync("/no/such/file.txt", CancellationToken.None);
        Assert.Equal(ErrorCodes.FileNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task The_same_name_twice_gives_two_files()
    {
        await using var pair = new FilePair();
        var source = FilePair.MakeFile(pair.DirA + "-src", "bir.txt", 100);
        await pair.A.SendFileAsync(source, CancellationToken.None);
        await pair.A.SendFileAsync(source, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(pair.DirB, "bir.txt")));
        Assert.True(File.Exists(Path.Combine(pair.DirB, "bir (1).txt")));
        Directory.Delete(pair.DirA + "-src", true);
    }

    [Fact]
    public async Task Cancelling_midway_leaves_no_partial_file()
    {
        await using var pair = new FilePair();
        var source = FilePair.MakeFile(pair.DirA + "-src", "katta.bin", 40 * FileTransferLimits.ChunkBytes);
        using var cts = new CancellationTokenSource();
        pair.A.Progress += p =>
        {
            if (p.State == TransferState.Transferring && p.Transferred > 0) cts.Cancel();
        };
        var result = await pair.A.SendFileAsync(source, cts.Token);
        Assert.Equal(TransferState.Cancelled, result.State);
        await Task.Delay(300);
        Assert.Empty(Directory.GetFiles(Path.Combine(pair.DirB, ".incoming")));
        Assert.DoesNotContain(Directory.GetFiles(pair.DirB), f => f.EndsWith("katta.bin"));
        Directory.Delete(pair.DirA + "-src", true);
    }

    [Fact]
    public async Task A_lost_connection_fails_the_sender_and_removes_the_partial_file()
    {
        await using var pair = new FilePair();
        var source = FilePair.MakeFile(pair.DirA + "-src", "uzilgan.bin", 40 * FileTransferLimits.ChunkBytes);
        pair.A.Progress += p =>
        {
            if (p.State == TransferState.Transferring && p.Transferred > 0) pair.Break();
        };
        var result = await pair.A.SendFileAsync(source, CancellationToken.None);
        Assert.Equal((TransferState.Failed, ErrorCodes.ConnectionLost), (result.State, result.ErrorCode));
        pair.B.Abort(); // what the connection owner does when the socket closes
        Assert.Empty(Directory.GetFiles(Path.Combine(pair.DirB, ".incoming")));
        Directory.Delete(pair.DirA + "-src", true);
    }

    [Fact]
    public async Task A_receiver_that_never_answers_times_the_sender_out()
    {
        await using var pair = new FilePair();
        pair.DropFromAToB(type => type == MessageTypes.FileOffer);
        var source = FilePair.MakeFile(pair.DirA + "-src", "x.bin", 100);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var result = await pair.A.SendFileAsync(source, cts.Token);
        Assert.Equal(TransferState.Cancelled, result.State);
        Directory.Delete(pair.DirA + "-src", true);
    }
}

public sealed class FileReceiverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-recv-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private FileReceiver Create(FileTransferPolicy? policy = null) => new(policy ?? new FileTransferPolicy(), _dir, _time);

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static FileChunkMessage Chunk(string id, int index, byte[] data) => new(id, index, Convert.ToBase64String(data));

    [Fact]
    public void A_wrong_digest_is_detected_and_nothing_is_stored()
    {
        using var receiver = Create();
        var data = new byte[] { 1, 2, 3, 4 };
        Assert.True(receiver.Offer(new FileOfferMessage("t1", "a.txt", 4, Sha(data))).Accepted);
        Assert.Null(receiver.Chunk(Chunk("t1", 0, [9, 9, 9, 9]))); // different content, same size
        var (result, path) = receiver.End(new FileEndMessage("t1", 1));
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FileCorrupt, result.ErrorCode);
        Assert.Null(path);
        Assert.Empty(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Chunks_out_of_order_abort_the_transfer()
    {
        using var receiver = Create();
        receiver.Offer(new FileOfferMessage("t1", "a.txt", 8, Sha(new byte[8])));
        var failure = receiver.Chunk(Chunk("t1", 1, new byte[4]));
        Assert.Equal(ErrorCodes.FileCorrupt, failure!.ErrorCode);
        Assert.Equal(0, receiver.ActiveCount);
    }

    [Fact]
    public void More_data_than_announced_aborts_the_transfer()
    {
        using var receiver = Create();
        receiver.Offer(new FileOfferMessage("t1", "a.txt", 4, Sha(new byte[4])));
        Assert.NotNull(receiver.Chunk(Chunk("t1", 0, new byte[10])));
        Assert.Empty(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Garbage_chunks_and_unknown_transfers_are_handled_without_exceptions()
    {
        using var receiver = Create();
        receiver.Offer(new FileOfferMessage("t1", "a.txt", 4, Sha(new byte[4])));
        Assert.NotNull(receiver.Chunk(new FileChunkMessage("t1", 0, "***not base64***")));
        Assert.NotNull(receiver.Chunk(Chunk("nope", 0, [1])));
        Assert.False(receiver.End(new FileEndMessage("nope", 0)).Result.Success);
    }

    [Fact]
    public void A_traversal_name_is_stored_inside_the_receive_folder_only()
    {
        using var receiver = Create();
        var data = new byte[] { 7 };
        Assert.True(receiver.Offer(new FileOfferMessage("t1", "..\\..\\..\\evil.txt", 1, Sha(data))).Accepted);
        receiver.Chunk(Chunk("t1", 0, data));
        var (result, path) = receiver.End(new FileEndMessage("t1", 1));
        Assert.True(result.Success);
        Assert.Equal(Path.Combine(_dir, "evil.txt"), path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a b")]
    public void Bad_transfer_ids_are_refused(string id) =>
        Assert.False(Create().Offer(new FileOfferMessage(id, "a.txt", 1, Sha([1]))).Accepted);

    [Fact]
    public void A_bad_digest_format_and_a_negative_size_are_refused()
    {
        using var receiver = Create();
        Assert.False(receiver.Offer(new FileOfferMessage("t1", "a.txt", 1, "xyz")).Accepted);
        Assert.False(receiver.Offer(new FileOfferMessage("t2", "a.txt", -5, Sha([1]))).Accepted);
    }

    [Fact]
    public void Only_a_limited_number_of_transfers_run_at_once()
    {
        using var receiver = Create(new FileTransferPolicy { MaxConcurrentTransfers = 2 });
        Assert.True(receiver.Offer(new FileOfferMessage("a", "1.txt", 1, Sha([1]))).Accepted);
        Assert.True(receiver.Offer(new FileOfferMessage("b", "2.txt", 1, Sha([1]))).Accepted);
        Assert.Equal(ErrorCodes.TooManyTransfers, receiver.Offer(new FileOfferMessage("c", "3.txt", 1, Sha([1]))).ErrorCode);
        Assert.False(receiver.Offer(new FileOfferMessage("a", "1.txt", 1, Sha([1]))).Accepted); // duplicate id
    }

    [Fact]
    public void A_stalled_transfer_times_out_and_its_partial_file_is_removed()
    {
        using var receiver = Create();
        receiver.Offer(new FileOfferMessage("t1", "a.txt", 8, Sha(new byte[8])));
        receiver.Chunk(Chunk("t1", 0, new byte[4]));
        _time.Advance(TimeSpan.FromMinutes(5));
        var failure = receiver.Chunk(Chunk("t1", 1, new byte[4]));
        Assert.NotNull(failure);
        Assert.Empty(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Stale_partial_files_from_an_earlier_run_are_cleaned_up()
    {
        using var receiver = Create();
        var folder = Path.Combine(_dir, ".incoming");
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, "old.part");
        File.WriteAllText(old, "x");
        File.SetLastWriteTimeUtc(old, _time.GetUtcNow().UtcDateTime.AddDays(-2));
        receiver.DeleteStalePartialFiles(TimeSpan.FromHours(1));
        Assert.False(File.Exists(old));
    }
}
