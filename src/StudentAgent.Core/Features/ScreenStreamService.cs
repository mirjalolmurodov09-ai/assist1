using System.Diagnostics;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;

namespace ClassroomControl.StudentAgent.Features;

public interface IScreenStreamService
{
    bool IsRunning { get; }
    void Start(StartStreamParameters parameters);
    Task StopAsync();
}

/// <summary>While the Teacher watches, sends the screen as JPEG: only the changed region (or nothing when nothing changed),
/// at the requested FPS, adapting quality to the network. Stops by itself when the session ends.</summary>
public sealed class ScreenStreamService : IScreenStreamService, IAsyncDisposable
{
    public const int MinFps = 1, MaxFps = 30, MinJpeg = 10, MaxJpeg = 95, MinWidth = 160, MaxWidth = 3840;
    private static readonly TimeSpan KeyFrameInterval = TimeSpan.FromSeconds(5);

    private readonly IScreenSource _source;
    private readonly IFrameEncoder _encoder;
    private readonly IActiveSession _session;
    private readonly FeatureState _state;
    private readonly TimeProvider _time;
    private readonly ILogger<ScreenStreamService> _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ScreenStreamService(IScreenSource source, IFrameEncoder encoder, IActiveSession session, FeatureState state, TimeProvider time, ILogger<ScreenStreamService> logger)
    {
        _source = source;
        _encoder = encoder;
        _session = session;
        _state = state;
        _time = time;
        _logger = logger;
    }

    public bool IsRunning
    {
        get { lock (_gate) return _loop is { IsCompleted: false }; }
    }

    public void Start(StartStreamParameters parameters)
    {
        var fps = Math.Clamp(parameters.FramesPerSecond, MinFps, MaxFps);
        var quality = Math.Clamp(parameters.JpegQuality, MinJpeg, MaxJpeg);
        var width = Math.Clamp(parameters.MaxWidth, MinWidth, MaxWidth);

        lock (_gate)
        {
            StopCore();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _state.SetStreaming(true);
            _loop = Task.Run(() => RunAsync(fps, quality, width, token));
        }
        _logger.LogInformation("Screen stream started ({Fps} fps, q{Quality}, {Width}px).", fps, quality, width);
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _loop;
            StopCore();
        }
        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                _logger.LogDebug("Screen stream loop did not stop in time.");
            }
        }
        _state.SetStreaming(false);
    }

    private void StopCore()
    {
        _cts?.Cancel();
        _cts = null;
        _loop = null;
    }

    private async Task RunAsync(int fps, int quality, int maxWidth, CancellationToken ct)
    {
        var adaptive = new AdaptiveQuality(fps, quality, maxWidth);
        var differ = new FrameDiffer();
        long sequence = 0;
        var lastKeyFrame = _time.GetTimestamp();
        var warned = false;
        var currentFps = fps;
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / currentFps), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_session.IsConnected) break;
                var started = Stopwatch.GetTimestamp();

                var raw = await Task.Run(() => SafeCapture(adaptive.MaxWidth), ct).ConfigureAwait(false);
                if (raw is null)
                {
                    if (!warned) _logger.LogWarning("Screen cannot be captured right now (locked or secure desktop).");
                    warned = true;
                    continue;
                }
                warned = false;

                var needKey = _time.GetElapsedTime(lastKeyFrame) >= KeyFrameInterval;
                var diff = differ.Compare(raw, needKey);
                if (!diff.Changed) continue;
                if (diff.KeyFrame) lastKeyFrame = _time.GetTimestamp();

                var jpeg = await Task.Run(() => _encoder.Encode(raw, diff.Region, adaptive.Quality), ct).ConfigureAwait(false);
                var frame = new ScreenFrameMessage(++sequence, raw.Width, raw.Height, diff.Region.X, diff.Region.Y, diff.Region.Width, diff.Region.Height,
                    diff.KeyFrame, _encoder.Format, Convert.ToBase64String(jpeg));
                if (!await _session.TrySendAsync(MessageTypes.ScreenFrame, frame, ct).ConfigureAwait(false)) break;

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Error in the screen stream; it was stopped.");
        }
        finally
        {
            timer.Dispose();
            _state.SetStreaming(false);
            _logger.LogInformation("Screen stream stopped.");
        }
    }

    private RawFrame? SafeCapture(int maxWidth)
    {
        try
        {
            return _source.Capture(maxWidth);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or System.Runtime.InteropServices.ExternalException)
        {
            _logger.LogDebug(ex, "Screen capture failed.");
            return null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
