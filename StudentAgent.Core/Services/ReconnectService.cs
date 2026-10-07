using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using Microsoft.Extensions.Options;

namespace ClassroomControl.StudentAgent.Services;

public interface IReconnectService
{
    int Attempt { get; }
    /// <summary>Delay before the next attempt; advances the schedule (1, 2, 5, 10, 20, 30, 30, ... seconds).</summary>
    TimeSpan NextDelay();
    void Reset();
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class ReconnectService : IReconnectService
{
    private readonly int[] _delaysSeconds;
    private readonly TimeProvider _time;
    private int _attempt;

    public ReconnectService(IOptions<AgentOptions> options, TimeProvider time)
    {
        _delaysSeconds = options.Value.ReconnectDelaysSeconds is { Length: > 0 } d ? d : [1];
        _time = time;
    }

    public int Attempt => Volatile.Read(ref _attempt);

    public TimeSpan NextDelay()
    {
        var index = Math.Min(Interlocked.Increment(ref _attempt) - 1, _delaysSeconds.Length - 1);
        return TimeSpan.FromSeconds(_delaysSeconds[index]);
    }

    public void Reset() => Volatile.Write(ref _attempt, 0);

    /// <summary>Sleeps without using CPU; honours cancellation.</summary>
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, _time, cancellationToken);
}
