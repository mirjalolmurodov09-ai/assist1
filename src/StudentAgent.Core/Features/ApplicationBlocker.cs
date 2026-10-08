using Microsoft.Extensions.Logging;

namespace ClassroomControl.StudentAgent.Features;

public interface IApplicationBlocker
{
    IReadOnlyList<string> Blocked { get; }
    void Block(string processName);
    void Unblock(string processName);
    void Clear();
}

/// <summary>"Restrict an application": while a name is blocked, any process with that name in the student's session is closed every couple of
/// seconds. The list is shown to the student and is cleared as soon as the Teacher connection ends.</summary>
public sealed class ApplicationBlocker : IApplicationBlocker, IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private readonly ISystemControl _system;
    private readonly FeatureState _state;
    private readonly ILogger<ApplicationBlocker> _logger;
    private readonly object _gate = new();
    private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ApplicationBlocker(ISystemControl system, FeatureState state, ILogger<ApplicationBlocker> logger)
    {
        _system = system;
        _state = state;
        _logger = logger;
    }

    public IReadOnlyList<string> Blocked
    {
        get { lock (_gate) return [.. _blocked.Order(StringComparer.OrdinalIgnoreCase)]; }
    }

    public void Block(string processName)
    {
        lock (_gate)
        {
            _blocked.Add(Normalize(processName));
            EnsureLoop();
        }
        Publish();
        _logger.LogInformation("Application blocked on the Teacher's request: {Name}", processName);
    }

    public void Unblock(string processName)
    {
        lock (_gate) _blocked.Remove(Normalize(processName));
        Publish();
        StopIfIdle();
    }

    public void Clear()
    {
        lock (_gate) _blocked.Clear();
        Publish();
        StopIfIdle();
    }

    private static string Normalize(string name) => Path.GetFileNameWithoutExtension(name.Trim());

    private void Publish() => _state.SetBlockedApplications(Blocked);

    private void EnsureLoop()
    {
        if (_loop is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token));
    }

    private void StopIfIdle()
    {
        lock (_gate)
        {
            if (_blocked.Count > 0) return;
            _cts?.Cancel();
            _cts = null;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                foreach (var name in Blocked)
                {
                    var closed = _system.StopApplication(name);
                    if (closed > 0) _logger.LogInformation("Blocked application {Name} was closed ({Count}).", name, closed);
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Nothing is blocked any more.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Error in the application blocker; it was stopped.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _blocked.Clear();
            _cts?.Cancel();
            loop = _loop;
        }
        if (loop is not null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { /* stopping */ }
        }
    }
}
