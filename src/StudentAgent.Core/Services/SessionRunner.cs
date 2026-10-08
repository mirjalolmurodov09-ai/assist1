using System.Net.Sockets;
using ClassroomControl.StudentAgent.Commands;
using ClassroomControl.StudentAgent.Features;
using ClassroomControl.Shared.Communication.Messages;
using ClassroomControl.Shared.Communication.Protocol;
using ClassroomControl.Shared.Communication.Security;
using ClassroomControl.StudentAgent.Models;
using Microsoft.Extensions.Logging;

namespace ClassroomControl.StudentAgent.Services;

public enum SessionEndReason { Cancelled, ConnectionLost, HeartbeatTimeout, TeacherDisconnected, SecurityViolation, Rejected }

public sealed record SessionEnd(SessionEndReason Reason, string? Message = null);

public interface ISessionRunner
{
    /// <summary>Runs an authenticated session (receive loop + heartbeat) until it ends. Never throws for network/protocol problems.</summary>
    Task<SessionEnd> RunAsync(AuthenticatedSession session, Action<RegistrationUpdateMessage> onRegistrationChanged, CancellationToken cancellationToken);
}

public sealed class SessionRunner : ISessionRunner
{
    private static readonly TimeSpan GoodbyeTimeout = TimeSpan.FromSeconds(2);
    private readonly IHeartbeatService _heartbeat;
    private readonly ICommandDispatcher _commands;
    private readonly IDeviceInfoService _device;
    private readonly IActiveSession _active;
    private readonly IFeatureCoordinator _features;
    private readonly IRemoteInputService _remote;
    private readonly ITeacherScreenService _teacherScreen;
    private readonly IPingTracker _ping;
    private readonly StatusReporter _status;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionRunner> _logger;

    public SessionRunner(IHeartbeatService heartbeat, ICommandDispatcher commands, IDeviceInfoService device, IActiveSession active,
        IFeatureCoordinator features, IRemoteInputService remote, ITeacherScreenService teacherScreen, IPingTracker ping, StatusReporter status,
        TimeProvider time, ILogger<SessionRunner> logger)
    {
        _heartbeat = heartbeat;
        _commands = commands;
        _device = device;
        _active = active;
        _features = features;
        _remote = remote;
        _teacherScreen = teacherScreen;
        _ping = ping;
        _status = status;
        _time = time;
        _logger = logger;
    }

    public async Task<SessionEnd> RunAsync(AuthenticatedSession session, Action<RegistrationUpdateMessage> onRegistrationChanged, CancellationToken cancellationToken)
    {
        var registration = session.Registration;
        var lastReceivedTicks = _time.GetUtcNow().UtcTicks;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deviceId = _device.DeviceId;

        _active.Attach(session.Channel);
        _features.OnSessionStarted(session.Policy);
        var statusTask = Guard(async () =>
        {
            await _status.RunAsync(session.Channel, linked.Token).ConfigureAwait(false);
            return new SessionEnd(SessionEndReason.Cancelled);
        }, linked.Token);
        var receive = Guard(() => ReceiveLoopAsync(session, deviceId, () => registration, r => registration = r.Registration,
            onRegistrationChanged, () => Interlocked.Exchange(ref lastReceivedTicks, _time.GetUtcNow().UtcTicks), linked.Token), linked.Token);
        var heartbeat = Guard(async () =>
        {
            await _heartbeat.RunAsync(session.Channel, deviceId, () => registration.ToString(),
                () => new DateTimeOffset(Interlocked.Read(ref lastReceivedTicks), TimeSpan.Zero), linked.Token).ConfigureAwait(false);
            return new SessionEnd(SessionEndReason.Cancelled);
        }, linked.Token);

        var first = await Task.WhenAny(receive, heartbeat, statusTask).ConfigureAwait(false);
        var end = await first.ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(receive, heartbeat, statusTask).ConfigureAwait(false);
        _active.Detach();
        await _features.OnSessionEndedAsync().ConfigureAwait(false);

        if (end.Reason == SessionEndReason.Cancelled || cancellationToken.IsCancellationRequested)
        {
            await SayGoodbyeAsync(session).ConfigureAwait(false);
            return new SessionEnd(SessionEndReason.Cancelled);
        }
        return end;
    }

    private async Task<SessionEnd> ReceiveLoopAsync(AuthenticatedSession session, string deviceId, Func<RegistrationState> registration,
        Action<RegistrationUpdateMessage> setRegistration, Action<RegistrationUpdateMessage> notify, Action markReceived, CancellationToken ct)
    {
        while (true)
        {
            var message = await session.Channel.ReceiveAsync(ct).ConfigureAwait(false);
            if (message is null) return new SessionEnd(SessionEndReason.ConnectionLost, "Teacher ulanishni yopdi.");
            markReceived();

            switch (message.Type)
            {
                case MessageTypes.HeartbeatAck:
                    _ping.AckReceived();
                    break;

                case MessageTypes.MouseEvent:
                    _remote.Handle(MessageSerializer.Deserialize<MouseEventMessage>(message.Payload));
                    break;

                case MessageTypes.KeyboardEvent:
                    _remote.Handle(MessageSerializer.Deserialize<KeyboardEventMessage>(message.Payload));
                    break;

                case MessageTypes.TeacherScreenFrame:
                    _teacherScreen.Handle(MessageSerializer.Deserialize<ScreenFrameMessage>(message.Payload));
                    break;

                case MessageTypes.RegistrationUpdate:
                    var update = MessageSerializer.Deserialize<RegistrationUpdateMessage>(message.Payload);
                    setRegistration(update);
                    notify(update);
                    if (update.Registration == RegistrationState.Rejected)
                        return new SessionEnd(SessionEndReason.Rejected, update.Message);
                    break;

                case MessageTypes.Command:
                    await HandleCommandAsync(session, deviceId, registration(), message, ct).ConfigureAwait(false);
                    break;

                case MessageTypes.Disconnect:
                    return new SessionEnd(SessionEndReason.TeacherDisconnected, MessageSerializer.Deserialize<DisconnectMessage>(message.Payload).Reason);

                default:
                    _logger.LogWarning("Invalid Command: unexpected message type {Type} ignored.", message.Type);
                    break;
            }
        }
    }

    private async Task HandleCommandAsync(AuthenticatedSession session, string deviceId, RegistrationState registration, VerifiedMessage message, CancellationToken ct)
    {
        CommandResult result;
        try
        {
            var request = MessageSerializer.Deserialize<CommandRequest>(message.Payload);
            result = await _commands.DispatchAsync(request, new SessionContext(session.SessionId, deviceId, registration), ct).ConfigureAwait(false);
        }
        catch (ProtocolException ex)
        {
            _logger.LogWarning("Invalid Command: {Reason}", ex.Message);
            result = CommandResults.Error("invalid", deviceId, CommandStatus.Rejected, ex.ErrorCode, "Buyruq formati noto‘g‘ri.");
        }
        await session.Channel.SendAsync(MessageTypes.CommandResponse, result, ct).ConfigureAwait(false);
    }

    private async Task<SessionEnd> Guard(Func<Task<SessionEnd>> body, CancellationToken ct)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new SessionEnd(SessionEndReason.Cancelled);
        }
        catch (HeartbeatTimeoutException ex)
        {
            _logger.LogWarning("Connection Lost: {Reason}", ex.Message);
            return new SessionEnd(SessionEndReason.HeartbeatTimeout, ex.Message);
        }
        catch (ProtocolException ex)
        {
            _logger.LogError("Security Error: {Code} - {Message}", ex.ErrorCode, ex.Message);
            return new SessionEnd(SessionEndReason.SecurityViolation, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogWarning("Connection Lost: {Reason}", ex.Message);
            return new SessionEnd(SessionEndReason.ConnectionLost, ex.Message);
        }
    }

    private async Task SayGoodbyeAsync(AuthenticatedSession session)
    {
        using var timeout = new CancellationTokenSource(GoodbyeTimeout);
        try
        {
            await session.Channel.SendAsync(MessageTypes.Disconnect, new DisconnectMessage("Agent stopped"), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Goodbye message could not be delivered.");
        }
    }
}
