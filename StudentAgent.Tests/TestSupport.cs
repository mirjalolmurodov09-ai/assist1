using ClassroomControl.StudentAgent.Communication.Security;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.StudentAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClassroomControl.StudentAgent.Tests;

internal static class TestSupport
{
    /// <summary>A well-formed code generated for tests only (not a real classroom secret).</summary>
    public static string NewCode() => $"CLASS-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2))}-{Random.Shared.Next(1000, 9999)}";

    public static string TempDir() => Path.Combine(Path.GetTempPath(), "cc-tests-" + Guid.NewGuid().ToString("N"));

    public sealed class TempSettings : IDisposable
    {
        public TempSettings()
        {
            Paths = new AppPaths(TempDir());
            Protector = new AesFileSecretProtector(Paths.KeyFile);
        }

        public AppPaths Paths { get; }
        public ISecretProtector Protector { get; }

        public SettingsService Create(bool allowLoopback = false) => new(Paths, Protector,
            Options.Create(new AgentOptions { AllowLoopbackTeacher = allowLoopback }), NullLogger<SettingsService>.Instance);

        public void Dispose()
        {
            try { Directory.Delete(Paths.DataDirectory, true); }
            catch (IOException) { /* best effort */ }
        }
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public ManualTimeProvider(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Two connected in-memory framed channels (A writes → B reads and vice versa).</summary>
internal sealed class MemoryChannelPair
{
    public MemoryChannelPair()
    {
        var ab = System.Threading.Channels.Channel.CreateUnbounded<string>();
        var ba = System.Threading.Channels.Channel.CreateUnbounded<string>();
        A = new End(ba.Reader, ab.Writer);
        B = new End(ab.Reader, ba.Writer);
    }

    public End A { get; }
    public End B { get; }

    public sealed class End : ClassroomControl.StudentAgent.Communication.Tcp.IFramedChannel
    {
        private readonly System.Threading.Channels.ChannelReader<string> _reader;
        private readonly System.Threading.Channels.ChannelWriter<string> _writer;
        public End(System.Threading.Channels.ChannelReader<string> reader, System.Threading.Channels.ChannelWriter<string> writer)
        {
            _reader = reader;
            _writer = writer;
        }

        public async Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            try { return await _reader.ReadAsync(cancellationToken); }
            catch (System.Threading.Channels.ChannelClosedException) { return null; }
        }

        public Task WriteFrameAsync(string frame, CancellationToken cancellationToken)
        {
            _writer.TryWrite(frame);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
