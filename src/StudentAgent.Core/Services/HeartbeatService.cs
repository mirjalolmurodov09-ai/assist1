using ClassroomControl.Shared.Communication.Messages;
using ClassroomControl.Shared.Communication.Protocol;
using ClassroomControl.Shared.Communication.Security;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using Microsoft.Extensions.Options;

namespace ClassroomControl.StudentAgent.Services;

public sealed class HeartbeatTimeoutException : TimeoutException
{
    public HeartbeatTimeoutException(TimeSpan silence) : base($"Teacher did not respond for {silence.TotalSeconds:0} seconds.") { }
}

public interface IHeartbeatService
{
    /// <summary>Sends HEARTBEAT at the configured interval until cancelled. Throws <see cref="HeartbeatTimeoutException"/>
    /// when nothing was received from the Teacher within the timeout.</summary>
    Task RunAsync(SecureChannel channel, string deviceId, Func<string> status, Func<DateTimeOffset> lastReceived, CancellationToken cancellationToken);
}

public sealed class HeartbeatService : IHeartbeatService
{
    private readonly AgentOptions _options;
    private readonly TimeProvider _time;

    public HeartbeatService(IOptions<AgentOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
    }

    public async Task RunAsync(SecureChannel channel, string deviceId, Func<string> status, Func<DateTimeOffset> lastReceived, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, _time);
        do
        {
            var now = _time.GetUtcNow();
            var silence = now - lastReceived();
            if (silence > _options.HeartbeatTimeout) throw new HeartbeatTimeoutException(silence);

            await channel.SendAsync(MessageTypes.Heartbeat,
                new HeartbeatMessage(deviceId, channel.SessionId, now.ToUnixTimeMilliseconds(), status()), cancellationToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }
}
