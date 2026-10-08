using System.Diagnostics;
using System.Windows.Input;

namespace ClassroomControl.TeacherApp.ViewModels;

/// <summary>Full-screen view of one computer, with optional remote control (mouse + keyboard are forwarded only while it is switched on).</summary>
public sealed class FullScreenViewModel : ObservableObject, IDisposable
{
    private const int MinMoveIntervalMs = 15;
    private readonly Server _server;
    private readonly MonitoringService _monitoring;
    private bool _remoteActive;
    private string _status = string.Empty;
    private long _lastMove;

    public FullScreenViewModel(ComputerViewModel computer, Server server, MonitoringService monitoring)
    {
        Computer = computer;
        _server = server;
        _monitoring = monitoring;
        ToggleRemoteCommand = new AsyncRelayCommand(ToggleRemoteAsync, () => computer.CanBeCommanded, ex => Status = ex.Message);
        ScreenshotCommand = new AsyncRelayCommand(ScreenshotAsync, () => computer.CanBeCommanded, ex => Status = ex.Message);
        LockCommand = new AsyncRelayCommand(() => Run(server.LockAsync(computer.DeviceId, Loc.Instance["Lock.DefaultMessage"])), () => computer.CanBeCommanded);
        UnlockCommand = new AsyncRelayCommand(() => Run(server.UnlockAsync(computer.DeviceId)), () => computer.CanBeCommanded);
    }

    public ComputerViewModel Computer { get; }
    public ICommand ToggleRemoteCommand { get; }
    public ICommand ScreenshotCommand { get; }
    public ICommand LockCommand { get; }
    public ICommand UnlockCommand { get; }

    public string Title => $"{Computer.Title} — {Computer.StudentName}";
    public string Details => $"{Computer.IpAddress}   {Computer.QualityText}   {Computer.PingText}";

    public bool RemoteActive
    {
        get => _remoteActive;
        private set => Set(ref _remoteActive, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Make this the focused computer: it streams at full-screen quality while the window is open.</summary>
    public Task OpenAsync() => _monitoring.SetFocusAsync(Computer.DeviceId);

    public async Task StartRemoteAsync()
    {
        if (RemoteActive) return;
        await ToggleRemoteAsync().ConfigureAwait(true);
    }

    private async Task ToggleRemoteAsync()
    {
        var outcome = RemoteActive ? await _server.StopRemoteControlAsync(Computer.DeviceId).ConfigureAwait(true)
            : await _server.StartRemoteControlAsync(Computer.DeviceId).ConfigureAwait(true);
        if (outcome.Success) RemoteActive = !RemoteActive;
        Status = outcome.Success ? string.Empty : outcome.Message;
    }

    private async Task ScreenshotAsync()
    {
        var (outcome, record) = await _server.TakeScreenshotAsync(Computer.DeviceId).ConfigureAwait(true);
        Status = outcome.Success ? Loc.Instance.Format("Screenshot.Saved", Path.GetFileName(record?.FilePath ?? string.Empty)) : outcome.Message;
    }

    private async Task Run(Task<CommandOutcome> task)
    {
        var outcome = await task.ConfigureAwait(true);
        Status = outcome.Success ? string.Empty : outcome.Message;
    }

    // ---- input forwarding (called by the window with coordinates normalized to the image) ----

    public async Task MouseMoveAsync(double x, double y)
    {
        if (!RemoteActive) return;
        if (Stopwatch.GetElapsedTime(_lastMove).TotalMilliseconds < MinMoveIntervalMs && _lastMove != 0) return; // at most ~60 moves per second
        _lastMove = Stopwatch.GetTimestamp();
        await _server.SendMouseAsync(Computer.DeviceId, new MouseEventMessage(MouseAction.Move, x, y, MouseButtonKind.None, 0)).ConfigureAwait(true);
    }

    public Task MouseButtonAsync(double x, double y, MouseButtonKind button, bool down) =>
        RemoteActive ? _server.SendMouseAsync(Computer.DeviceId, new MouseEventMessage(down ? MouseAction.Down : MouseAction.Up, x, y, button, 0)) : Task.CompletedTask;

    public Task MouseWheelAsync(double x, double y, int delta) =>
        RemoteActive ? _server.SendMouseAsync(Computer.DeviceId, new MouseEventMessage(MouseAction.Wheel, x, y, MouseButtonKind.None, delta)) : Task.CompletedTask;

    public Task KeyAsync(int virtualKey, bool down, bool extended) =>
        RemoteActive ? _server.SendKeyboardAsync(Computer.DeviceId, new KeyboardEventMessage(virtualKey, down, extended)) : Task.CompletedTask;

    /// <summary>Closing the window ends remote control and returns the computer to thumbnail quality.</summary>
    public async Task CloseAsync()
    {
        if (RemoteActive)
        {
            await _server.StopRemoteControlAsync(Computer.DeviceId).ConfigureAwait(true);
            RemoteActive = false;
        }
        await _monitoring.SetFocusAsync(null).ConfigureAwait(true);
    }

    public void Dispose()
    {
    }
}
