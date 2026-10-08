using System.Collections.Concurrent;

namespace ClassroomControl.TeacherApp.Services;

/// <summary>Keeps a low-rate thumbnail stream running for every online approved computer, raises the quality for the one the teacher
/// is looking at full screen, and restores everything when the teacher pauses or leaves.</summary>
public sealed class MonitoringService : IDisposable
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(8);
    private readonly Server _server;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextAttempt = new();
    private volatile string? _focus;
    private volatile bool _enabled = true;

    public MonitoringService(Server server, TimeProvider? time = null)
    {
        _server = server;
        _time = time ?? TimeProvider.System;
        _server.DeviceChanged += OnDeviceChanged;
        _server.FrameReceived += OnFrame;
    }

    public event EventHandler<FrameReceivedEventArgs>? FrameArrived;

    public bool Enabled => _enabled;
    public string? FocusedDevice => _focus;

    private void OnFrame(object? sender, FrameReceivedEventArgs e) => FrameArrived?.Invoke(this, e);

    private void OnDeviceChanged(object? sender, DeviceSnapshot snapshot)
    {
        if (!_enabled || !snapshot.Online || !snapshot.IsApproved || snapshot.Streaming) return;
        if (_nextAttempt.TryGetValue(snapshot.DeviceId, out var next) && _time.GetUtcNow() < next) return;
        _nextAttempt[snapshot.DeviceId] = _time.GetUtcNow() + RetryAfter;
        _ = StartAsync(snapshot.DeviceId);
    }

    private async Task StartAsync(string deviceId)
    {
        var s = _server.Settings;
        var focused = _focus == deviceId;
        await _server.StartStreamAsync(deviceId,
            focused ? s.FullScreenFps : s.MonitoringFps,
            focused ? s.FullScreenQuality : s.MonitoringQuality,
            focused ? s.FullScreenMaxWidth : s.MonitoringMaxWidth).ConfigureAwait(false);
    }

    /// <summary>Starts streams for everything that is already online (call after the window opened or monitoring was resumed).</summary>
    public async Task StartAllAsync()
    {
        _enabled = true;
        _nextAttempt.Clear();
        await Task.WhenAll(_server.Devices.Where(d => d.Online && d.IsApproved).Select(d => StartAsync(d.DeviceId))).ConfigureAwait(false);
    }

    public async Task PauseAsync()
    {
        _enabled = false;
        await Task.WhenAll(_server.Devices.Where(d => d.Online && d.IsApproved && d.Streaming)
            .Select(d => _server.StopStreamAsync(d.DeviceId))).ConfigureAwait(false);
    }

    /// <summary>Full-screen quality for one computer (null = back to thumbnails for everyone).</summary>
    public async Task SetFocusAsync(string? deviceId)
    {
        var previous = _focus;
        _focus = deviceId;
        if (previous is not null && previous != deviceId && _enabled) await StartAsync(previous).ConfigureAwait(false);
        if (deviceId is not null) await StartAsync(deviceId).ConfigureAwait(false);
    }

    /// <summary>Re-sends the stream settings after the teacher changed them.</summary>
    public async Task ApplySettingsAsync()
    {
        if (_enabled) await Task.WhenAll(_server.Devices.Where(d => d.Online && d.IsApproved).Select(d => StartAsync(d.DeviceId))).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _server.DeviceChanged -= OnDeviceChanged;
        _server.FrameReceived -= OnFrame;
    }
}
