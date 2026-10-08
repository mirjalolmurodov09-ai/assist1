using ClassroomControl.StudentAgent.Models;

namespace ClassroomControl.StudentAgent.Services;

public sealed class InvalidStateTransitionException : InvalidOperationException
{
    public InvalidStateTransitionException(ConnectionState from, ConnectionState to)
        : base($"Invalid connection state transition: {from} -> {to}.") { }
}

/// <summary>Strict connection state machine. Main path:
/// Disconnected → Discovering → TeacherFound → Connecting → Authenticating → WaitingForApproval → Connected → Reconnecting → Disconnected.</summary>
public sealed class ConnectionStateMachine
{
    private static readonly Dictionary<ConnectionState, ConnectionState[]> Allowed = new()
    {
        [ConnectionState.Disconnected] = [ConnectionState.Discovering],
        [ConnectionState.Discovering] = [ConnectionState.TeacherFound, ConnectionState.Reconnecting, ConnectionState.Disconnected],
        [ConnectionState.TeacherFound] = [ConnectionState.Connecting, ConnectionState.Reconnecting, ConnectionState.Disconnected],
        [ConnectionState.Connecting] = [ConnectionState.Authenticating, ConnectionState.Reconnecting, ConnectionState.Disconnected],
        [ConnectionState.Authenticating] = [ConnectionState.WaitingForApproval, ConnectionState.Connected, ConnectionState.Reconnecting, ConnectionState.Disconnected],
        [ConnectionState.WaitingForApproval] = [ConnectionState.Connected, ConnectionState.Reconnecting, ConnectionState.Disconnected],
        [ConnectionState.Connected] = [ConnectionState.WaitingForApproval, ConnectionState.Reconnecting, ConnectionState.Disconnected],
        [ConnectionState.Reconnecting] = [ConnectionState.Discovering, ConnectionState.Disconnected],
    };

    private readonly object _gate = new();
    private ConnectionState _state = ConnectionState.Disconnected;

    public ConnectionState State
    {
        get { lock (_gate) return _state; }
    }

    public event EventHandler<ConnectionState>? StateChanged;

    public static bool CanTransition(ConnectionState from, ConnectionState to) => Allowed[from].Contains(to);

    public void Transition(ConnectionState next)
    {
        lock (_gate)
        {
            if (_state == next) return;
            if (!CanTransition(_state, next)) throw new InvalidStateTransitionException(_state, next);
            _state = next;
        }
        StateChanged?.Invoke(this, next);
    }

    public bool TryTransition(ConnectionState next)
    {
        try
        {
            Transition(next);
            return true;
        }
        catch (InvalidStateTransitionException)
        {
            return false;
        }
    }
}
