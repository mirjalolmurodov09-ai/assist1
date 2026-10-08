using System.Collections.Concurrent;

namespace ClassroomControl.ClassroomServer;

/// <summary>Runtime state of one connected Student Agent.</summary>
internal sealed class DeviceConnection
{
    private long _lastReceivedTicks;
    private int _frameSending;

    public DeviceConnection(string deviceId, string sessionId, SecureChannel channel, CancellationTokenSource cts, RecordingChannel? recorder, DateTimeOffset now)
    {
        DeviceId = deviceId;
        SessionId = sessionId;
        Channel = channel;
        Cts = cts;
        Recorder = recorder;
        _lastReceivedTicks = now.UtcTicks;
    }

    public string DeviceId { get; }
    public string SessionId { get; }
    public SecureChannel Channel { get; }
    public CancellationTokenSource Cts { get; }
    public RecordingChannel? Recorder { get; }
    public ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> Pending { get; } = new();
    public volatile StatusUpdateMessage? Status;
    public RegistrationState Registration { get; set; }
    public string DisconnectReason { get; set; } = "Connection closed";

    public DateTimeOffset LastReceived => new(Interlocked.Read(ref _lastReceivedTicks), TimeSpan.Zero);

    public void MarkReceived(DateTimeOffset now) => Interlocked.Exchange(ref _lastReceivedTicks, now.UtcTicks);

    public void Disconnect(string reason)
    {
        DisconnectReason = reason;
        try
        {
            Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Connection already finished.
        }
    }

    /// <summary>Sends a frame unless the previous one is still being written (slow student): newer frames replace older ones.</summary>
    public async Task<bool> TrySendFrameAsync<T>(string type, T payload, CancellationToken cancellationToken) where T : class
    {
        if (Interlocked.CompareExchange(ref _frameSending, 1, 0) != 0) return false;
        try
        {
            await Channel.SendAsync(type, payload, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            Volatile.Write(ref _frameSending, 0);
        }
    }
}
