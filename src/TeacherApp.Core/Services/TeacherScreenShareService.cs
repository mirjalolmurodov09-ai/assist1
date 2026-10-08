using System.Diagnostics;

namespace ClassroomControl.TeacherApp.Services;

/// <summary>"Show teacher screen": captures the teacher's own screen, sends only what changed, adapts to the network, and keeps the
/// target list current (computers that connect later join automatically; slow ones skip frames instead of slowing the rest).</summary>
public sealed class TeacherScreenShareService : IAsyncDisposable
{
    private static readonly TimeSpan KeyFrameInterval = TimeSpan.FromSeconds(3);
    private readonly Server _server;
    private readonly IScreenSource _source;
    private readonly IFrameEncoder _encoder;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Func<IReadOnlyCollection<string>>? _targets;

    public TeacherScreenShareService(Server server, IScreenSource source, IFrameEncoder encoder)
    {
        _server = server;
        _source = source;
        _encoder = encoder;
    }

    public bool IsSharing
    {
        get { lock (_gate) return _loop is { IsCompleted: false }; }
    }

    public event EventHandler? SharingChanged;

    /// <summary>Starts sharing with the computers returned by <paramref name="targets"/> (re-evaluated continuously).</summary>
    public async Task StartAsync(Func<IReadOnlyCollection<string>> targets, string? title)
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _targets = targets;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(title, token));
        }
        SharingChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopAsync()
    {
        Task? loop;
        IReadOnlyCollection<string> targets;
        lock (_gate)
        {
            loop = _loop;
            targets = _targets?.Invoke() ?? [];
            _cts?.Cancel();
            _cts = null;
            _loop = null;
            _targets = null;
        }
        if (loop is null) return;
        try
        {
            await loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Loop is already stopping.
        }
        await _server.ExecuteManyAsync(targets.Where(_server.IsOnline), CommandNames.StopTeacherScreen, null).ConfigureAwait(false);
        SharingChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync(string? title, CancellationToken ct)
    {
        var s = _server.Settings;
        var adaptive = new AdaptiveQuality(s.TeacherScreenFps, s.TeacherScreenQuality, s.TeacherScreenMaxWidth);
        var differ = new FrameDiffer();
        var announced = new HashSet<string>();
        long sequence = 0;
        var lastKey = Stopwatch.GetTimestamp();
        var currentFps = adaptive.Fps;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / currentFps));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var started = Stopwatch.GetTimestamp();
                var targets = _targets?.Invoke() ?? [];
                var online = targets.Where(_server.IsOnline).ToList();

                // Computers that joined after the share started get the "show teacher screen" command and a key frame.
                var fresh = online.Where(announced.Add).ToList();
                if (fresh.Count > 0)
                {
                    _ = _server.ExecuteManyAsync(fresh, CommandNames.StartTeacherScreen, new StartTeacherScreenParameters(title));
                }
                announced.RemoveWhere(id => !online.Contains(id));
                if (online.Count == 0) continue;

                var raw = await Task.Run(() => _source.Capture(adaptive.MaxWidth), ct).ConfigureAwait(false);
                if (raw is null) continue;
                var needKey = fresh.Count > 0 || Stopwatch.GetElapsedTime(lastKey) >= KeyFrameInterval;
                var diff = differ.Compare(raw, needKey);
                if (!diff.Changed) continue;
                if (diff.KeyFrame) lastKey = Stopwatch.GetTimestamp();

                var jpeg = await Task.Run(() => _encoder.Encode(raw, diff.Region, adaptive.Quality), ct).ConfigureAwait(false);
                var frame = new ScreenFrameMessage(++sequence, raw.Width, raw.Height, diff.Region.X, diff.Region.Y, diff.Region.Width, diff.Region.Height,
                    diff.KeyFrame, _encoder.Format, Convert.ToBase64String(jpeg));
                await _server.SendTeacherFrameAsync(online, frame).ConfigureAwait(false);

                adaptive.Report(Stopwatch.GetElapsedTime(started));
                if (adaptive.Fps != currentFps)
                {
                    currentFps = adaptive.Fps;
                    timer.Period = TimeSpan.FromSeconds(1.0 / currentFps);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
