namespace ClassroomControl.StudentAgent.Features;

public interface IInputInjector
{
    void Inject(MouseEventMessage mouse);
    void Inject(KeyboardEventMessage key);
}

public sealed record StartApplicationResult(bool Started, string Message);

public interface ISystemControl
{
    void Restart(int delaySeconds, string? reason);
    void Shutdown(int delaySeconds, string? reason);
    StartApplicationResult StartApplication(string target, string? arguments);
    /// <summary>Ends all processes with the given name that belong to the current user session. Returns how many were ended.</summary>
    int StopApplication(string processName);
}

public interface ILockScreen
{
    void Show(string message);
    void Hide();
}

public interface IUserNotifier
{
    void Show(string title, string text);
}

public interface ITeacherScreenViewer
{
    void Show(string? title);
    void Update(ScreenFrameMessage frame);
    void Close();
}

public sealed record SystemSnapshot(int CpuPercent, long RamTotalBytes, long RamUsedBytes);

public interface ISystemMetrics
{
    SystemSnapshot Read();
}

/// <summary>What the Teacher is currently doing to this computer. Shown to the student, so nothing happens invisibly.</summary>
public sealed class FeatureState
{
    private readonly object _gate = new();
    private bool _locked, _streaming, _remote, _teacherScreen;

    public event EventHandler? Changed;

    public bool Locked => Get(ref _locked);
    public bool Streaming => Get(ref _streaming);
    public bool RemoteControl => Get(ref _remote);
    public bool TeacherScreen => Get(ref _teacherScreen);
    public bool Any => Locked || Streaming || RemoteControl || TeacherScreen;

    public void SetLocked(bool value) => Set(ref _locked, value);
    public void SetStreaming(bool value) => Set(ref _streaming, value);
    public void SetRemoteControl(bool value) => Set(ref _remote, value);
    public void SetTeacherScreen(bool value) => Set(ref _teacherScreen, value);

    private bool Get(ref bool field)
    {
        lock (_gate) return field;
    }

    private void Set(ref bool field, bool value)
    {
        lock (_gate)
        {
            if (field == value) return;
            field = value;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Sends messages on the currently authenticated session (if there is one).</summary>
public interface IActiveSession
{
    bool IsConnected { get; }
    void Attach(SecureChannel channel);
    void Detach();
    /// <summary>False when there is no session or the send failed.</summary>
    Task<bool> TrySendAsync<T>(string type, T payload, CancellationToken cancellationToken) where T : class;
}

public sealed class ActiveSession : IActiveSession
{
    private volatile SecureChannel? _channel;

    public bool IsConnected => _channel is not null;
    public void Attach(SecureChannel channel) => _channel = channel;
    public void Detach() => _channel = null;

    public async Task<bool> TrySendAsync<T>(string type, T payload, CancellationToken cancellationToken) where T : class
    {
        var channel = _channel;
        if (channel is null) return false;
        try
        {
            await channel.SendAsync(type, payload, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Round-trip time of the last heartbeat, reported to the Teacher as "ping".</summary>
public interface IPingTracker
{
    int LastMilliseconds { get; }
    void HeartbeatSent();
    void AckReceived();
}

public sealed class PingTracker : IPingTracker
{
    private readonly TimeProvider _time;
    private long _sentTicks;
    private int _last;

    public PingTracker(TimeProvider time) => _time = time;

    public int LastMilliseconds => Volatile.Read(ref _last);
    public void HeartbeatSent() => Interlocked.Exchange(ref _sentTicks, _time.GetTimestamp());

    public void AckReceived()
    {
        var sent = Interlocked.Exchange(ref _sentTicks, 0);
        if (sent == 0) return;
        Volatile.Write(ref _last, (int)_time.GetElapsedTime(sent).TotalMilliseconds);
    }
}
