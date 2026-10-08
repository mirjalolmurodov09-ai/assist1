namespace ClassroomControl.Shared.Communication.Security;

/// <summary>Remembers identifiers (nonces / message ids) for a time window and rejects repeats.</summary>
public sealed class ReplayGuard
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Queue<(string Id, DateTimeOffset At)> _order = new();
    private readonly TimeSpan _window;
    private readonly int _capacity;

    public ReplayGuard(TimeSpan window, int capacity = 4096)
    {
        _window = window;
        _capacity = capacity;
    }

    /// <summary>Returns true when the id is new; false when it was already seen inside the window.</summary>
    public bool TryRegister(string id, DateTimeOffset now)
    {
        lock (_gate)
        {
            Evict(now);
            if (_seen.ContainsKey(id)) return false;
            _seen[id] = now;
            _order.Enqueue((id, now));
            return true;
        }
    }

    private void Evict(DateTimeOffset now)
    {
        while (_order.Count > 0 && (now - _order.Peek().At > _window || _order.Count > _capacity))
        {
            var (id, at) = _order.Dequeue();
            if (_seen.TryGetValue(id, out var current) && current == at) _seen.Remove(id);
        }
    }
}
