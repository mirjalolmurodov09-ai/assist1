namespace ClassroomControl.StudentAgent.Infrastructure.Helpers;

/// <summary>Auto-reset async event: Set() releases exactly the next WaitAsync().</summary>
public sealed class AsyncSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _tcs = Create();

    private static TaskCompletionSource Create() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Set()
    {
        lock (_gate) _tcs.TrySetResult();
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_gate) task = _tcs.Task;
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (ReferenceEquals(task, _tcs.Task)) _tcs = Create();
        }
    }
}
