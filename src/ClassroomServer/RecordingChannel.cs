namespace ClassroomControl.ClassroomServer;

/// <summary>Remembers the last frame written so a test can replay it (fault injection only).</summary>
public sealed class RecordingChannel : IFramedChannel
{
    private readonly IFramedChannel _inner;
    private string? _last;

    public RecordingChannel(IFramedChannel inner) => _inner = inner;

    public Task<string?> ReadFrameAsync(CancellationToken cancellationToken) => _inner.ReadFrameAsync(cancellationToken);

    public Task WriteFrameAsync(string frame, CancellationToken cancellationToken)
    {
        _last = frame;
        return _inner.WriteFrameAsync(frame, cancellationToken);
    }

    public Task ReplayLastAsync(CancellationToken cancellationToken) =>
        _last is null ? Task.CompletedTask : _inner.WriteFrameAsync(_last, cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
