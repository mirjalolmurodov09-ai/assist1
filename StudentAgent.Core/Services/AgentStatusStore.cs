using ClassroomControl.StudentAgent.Models;

namespace ClassroomControl.StudentAgent.Services;

/// <summary>Single source of truth for what the agent is doing right now. Services update it; UI and commands read it.</summary>
public interface IAgentStatusStore
{
    AgentStatusSnapshot Current { get; }
    event EventHandler<AgentStatusSnapshot>? Changed;
    /// <summary>Moves the state machine (validated) and applies the optional change to the snapshot.</summary>
    void TransitionTo(ConnectionState state, Func<AgentStatusSnapshot, AgentStatusSnapshot>? update = null);
    void Update(Func<AgentStatusSnapshot, AgentStatusSnapshot> update);
}

public sealed class AgentStatusStore : IAgentStatusStore
{
    private readonly object _gate = new();
    private readonly ConnectionStateMachine _machine = new();
    private AgentStatusSnapshot _current;

    public AgentStatusStore(TimeProvider time) => _current = AgentStatusSnapshot.Initial(time.GetUtcNow());

    public AgentStatusSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler<AgentStatusSnapshot>? Changed;

    public void TransitionTo(ConnectionState state, Func<AgentStatusSnapshot, AgentStatusSnapshot>? update = null)
    {
        AgentStatusSnapshot snapshot;
        lock (_gate)
        {
            _machine.Transition(state);
            snapshot = _current with { State = state };
            if (update is not null) snapshot = update(snapshot) with { State = state };
            _current = snapshot;
        }
        Changed?.Invoke(this, snapshot);
    }

    public void Update(Func<AgentStatusSnapshot, AgentStatusSnapshot> update)
    {
        AgentStatusSnapshot snapshot;
        lock (_gate)
        {
            snapshot = update(_current) with { State = _current.State };
            _current = snapshot;
        }
        Changed?.Invoke(this, snapshot);
    }
}
