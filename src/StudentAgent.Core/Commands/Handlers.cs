using ClassroomControl.Shared.Communication.Protocol;
using ClassroomControl.StudentAgent.Models;
using ClassroomControl.StudentAgent.Services;

namespace ClassroomControl.StudentAgent.Commands;

public sealed class PingCommandHandler : ICommandHandler
{
    private readonly TimeProvider _time;

    public PingCommandHandler(TimeProvider time) => _time = time;
    public string Name => CommandNames.Ping;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken) =>
        Task.FromResult(CommandResults.Success(context, "Pong", new { pong = true, agentTime = _time.GetUtcNow() }));
}

public sealed class GetStatusCommandHandler : ICommandHandler
{
    private readonly IAgentStatusStore _status;
    private readonly IDeviceInfoService _device;
    private readonly TimeProvider _time;

    public GetStatusCommandHandler(IAgentStatusStore status, IDeviceInfoService device, TimeProvider time)
    {
        _status = status;
        _device = device;
        _time = time;
    }

    public string Name => CommandNames.GetStatus;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var s = _status.Current;
        return Task.FromResult(CommandResults.Success(context, "Status retrieved", new
        {
            state = s.State.ToString(),
            registration = s.Registration.ToString(),
            classroom = s.ClassroomName,
            agentVersion = _device.AgentVersion,
            protocolVersion = ProtocolConstants.Current.ToString(),
            uptimeSeconds = (long)(_time.GetUtcNow() - s.StartedUtc).TotalSeconds,
            lastConnectionUtc = s.LastConnectionUtc,
        }));
    }
}

public sealed class GetDeviceInfoCommandHandler : ICommandHandler
{
    private readonly IDeviceInfoService _device;
    private readonly IAgentStatusStore _status;

    public GetDeviceInfoCommandHandler(IDeviceInfoService device, IAgentStatusStore status)
    {
        _device = device;
        _status = status;
    }

    public string Name => CommandNames.GetDeviceInfo;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken) =>
        Task.FromResult(CommandResults.Success(context, "Device info retrieved",
            _device.GetDeviceInfo() with { AgentStatus = _status.Current.State.ToString() }));
}
