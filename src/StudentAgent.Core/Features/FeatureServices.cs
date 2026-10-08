using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.StudentAgent.Services;
using Microsoft.Extensions.Logging;

namespace ClassroomControl.StudentAgent.Features;

public interface IRemoteInputService
{
    bool IsActive { get; }
    void Start();
    void Stop();
    void Handle(MouseEventMessage mouse);
    void Handle(KeyboardEventMessage key);
}

/// <summary>Applies the Teacher's mouse and keyboard events - but only while the Teacher has switched remote control on for this
/// session. Everything else is dropped and logged once.</summary>
public sealed class RemoteInputService : IRemoteInputService
{
    private const int MaxWheelDelta = 12_000;
    private readonly IInputInjector _injector;
    private readonly FeatureState _state;
    private readonly ILogger<RemoteInputService> _logger;
    private int _warned;

    public RemoteInputService(IInputInjector injector, FeatureState state, ILogger<RemoteInputService> logger)
    {
        _injector = injector;
        _state = state;
        _logger = logger;
    }

    public bool IsActive => _state.RemoteControl;

    public void Start()
    {
        Volatile.Write(ref _warned, 0);
        _state.SetRemoteControl(true);
        _logger.LogInformation("Remote control started.");
    }

    public void Stop()
    {
        if (!_state.RemoteControl) return;
        _state.SetRemoteControl(false);
        _logger.LogInformation("Remote control stopped.");
    }

    public void Handle(MouseEventMessage mouse)
    {
        if (!Allowed()) return;
        if (!double.IsFinite(mouse.X) || !double.IsFinite(mouse.Y) || Math.Abs(mouse.WheelDelta) > MaxWheelDelta) return;
        _injector.Inject(mouse with { X = Math.Clamp(mouse.X, 0, 1), Y = Math.Clamp(mouse.Y, 0, 1) });
    }

    public void Handle(KeyboardEventMessage key)
    {
        if (!Allowed()) return;
        if (key.VirtualKey is < 1 or > 254) return;
        _injector.Inject(key);
    }

    private bool Allowed()
    {
        if (_state.RemoteControl) return true;
        if (Interlocked.Exchange(ref _warned, 1) == 0) _logger.LogWarning("Security Error: input event received while remote control is not active; ignored.");
        return false;
    }
}

public interface ITeacherScreenService
{
    bool IsActive { get; }
    void Start(string? title);
    void Stop();
    void Handle(ScreenFrameMessage frame);
}

public sealed class TeacherScreenService : ITeacherScreenService
{
    private readonly ITeacherScreenViewer _viewer;
    private readonly FeatureState _state;
    private readonly ILogger<TeacherScreenService> _logger;

    public TeacherScreenService(ITeacherScreenViewer viewer, FeatureState state, ILogger<TeacherScreenService> logger)
    {
        _viewer = viewer;
        _state = state;
        _logger = logger;
    }

    public bool IsActive => _state.TeacherScreen;

    public void Start(string? title)
    {
        _viewer.Show(title);
        _state.SetTeacherScreen(true);
        _logger.LogInformation("Teacher screen is shown.");
    }

    public void Stop()
    {
        if (!_state.TeacherScreen) return;
        _viewer.Close();
        _state.SetTeacherScreen(false);
        _logger.LogInformation("Teacher screen closed.");
    }

    public void Handle(ScreenFrameMessage frame)
    {
        if (!_state.TeacherScreen) return;
        if (frame.FullWidth is < 1 or > 16_384 || frame.FullHeight is < 1 or > 16_384 || frame.Width < 1 || frame.Height < 1
            || frame.X < 0 || frame.Y < 0 || frame.X + frame.Width > frame.FullWidth || frame.Y + frame.Height > frame.FullHeight)
        {
            _logger.LogWarning("Invalid Command: teacher screen frame with impossible dimensions ignored.");
            return;
        }
        _viewer.Update(frame);
    }
}

public interface IFeatureCoordinator
{
    void OnSessionStarted(AgentPolicy policy);
    Task OnSessionEndedAsync();
}

/// <summary>Everything the Teacher switched on is switched off when the connection goes away: streaming, remote control and the
/// teacher-screen view stop immediately, and a lock is released after the policy's safety timeout so a crashed Teacher PC can never
/// leave students locked out.</summary>
public sealed class FeatureCoordinator : IFeatureCoordinator
{
    private readonly IScreenStreamService _stream;
    private readonly IRemoteInputService _remote;
    private readonly ITeacherScreenService _teacherScreen;
    private readonly ILockScreen _lockScreen;
    private readonly IApplicationBlocker _blocker;
    private readonly FeatureState _state;
    private readonly TimeProvider _time;
    private readonly ILogger<FeatureCoordinator> _logger;
    private readonly object _gate = new();
    private ITimer? _unlockTimer;
    private int _autoUnlockSeconds = 30;

    public FeatureCoordinator(IScreenStreamService stream, IRemoteInputService remote, ITeacherScreenService teacherScreen, ILockScreen lockScreen,
        IApplicationBlocker blocker, FeatureState state, TimeProvider time, ILogger<FeatureCoordinator> logger)
    {
        _blocker = blocker;
        _stream = stream;
        _remote = remote;
        _teacherScreen = teacherScreen;
        _lockScreen = lockScreen;
        _state = state;
        _time = time;
        _logger = logger;
    }

    public void OnSessionStarted(AgentPolicy policy)
    {
        lock (_gate)
        {
            _autoUnlockSeconds = Math.Max(0, policy.AutoUnlockSeconds);
            _unlockTimer?.Dispose();
            _unlockTimer = null;
        }
    }

    public async Task OnSessionEndedAsync()
    {
        try
        {
            await _stream.StopAsync().ConfigureAwait(false);
            _remote.Stop();
            _teacherScreen.Stop();
            _blocker.Clear();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Features could not be stopped cleanly after the session ended.");
        }

        if (!_state.Locked) return;
        lock (_gate)
        {
            if (_autoUnlockSeconds == 0) return; // policy: stay locked until the Teacher returns
            _unlockTimer?.Dispose();
            _unlockTimer = _time.CreateTimer(_ => AutoUnlock(), null, TimeSpan.FromSeconds(_autoUnlockSeconds), Timeout.InfiniteTimeSpan);
        }
        _logger.LogWarning("Teacher connection lost while locked; the screen will unlock in {Seconds} s unless the Teacher returns.", _autoUnlockSeconds);
    }

    private void AutoUnlock()
    {
        if (!_state.Locked) return;
        try
        {
            _lockScreen.Hide();
            _state.SetLocked(false);
            _logger.LogWarning("Screen unlocked automatically (Teacher unreachable).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Error while unlocking automatically.");
        }
    }
}

public sealed class StatusReporter
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(250);
    private readonly FeatureState _state;
    private readonly ISystemMetrics _metrics;
    private readonly IPingTracker _ping;
    private readonly TimeProvider _time;

    public StatusReporter(FeatureState state, ISystemMetrics metrics, IPingTracker ping, TimeProvider time)
    {
        _state = state;
        _metrics = metrics;
        _ping = ping;
        _time = time;
    }

    public StatusUpdateMessage Build()
    {
        var m = _metrics.Read();
        return new StatusUpdateMessage(_state.Locked, _state.Streaming, _state.RemoteControl, _state.TeacherScreen,
            m.CpuPercent, m.RamTotalBytes, m.RamUsedBytes, _ping.LastMilliseconds, _state.BlockedApplications.Count, m.BootTimeUnixSeconds);
    }

    /// <summary>Sends STATUS_UPDATE every few seconds and immediately when a feature turns on or off.</summary>
    public async Task RunAsync(SecureChannel channel, CancellationToken cancellationToken)
    {
        var changed = new AsyncSignal();
        void OnChanged(object? s, EventArgs e) => changed.Set();
        _state.Changed += OnChanged;
        try
        {
            while (true)
            {
                await channel.SendAsync(MessageTypes.StatusUpdate, Build(), cancellationToken).ConfigureAwait(false);
                await Task.Delay(MinGap, _time, cancellationToken).ConfigureAwait(false);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(Interval);
                try
                {
                    await changed.WaitAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Interval elapsed: send the periodic update.
                }
            }
        }
        finally
        {
            _state.Changed -= OnChanged;
        }
    }
}
