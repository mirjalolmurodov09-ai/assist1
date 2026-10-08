using System.Net;
using System.Net.Sockets;
using System.Text;
using ClassroomControl.Shared.Communication.Messages;
using ClassroomControl.Shared.Communication.Protocol;
using ClassroomControl.Shared.Communication.Security;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.StudentAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassroomControl.StudentAgent.Services;

public sealed record DiscoveryResult(ConnectionInfo? Teacher, bool RejectedResponsesSeen);

public interface IDiscoveryService
{
    /// <summary>Finds the Teacher: uses the configured address when present, otherwise UDP discovery on the local subnet.</summary>
    Task<DiscoveryResult> FindTeacherAsync(StudentSettings settings, CancellationToken cancellationToken);
}

public sealed class DiscoveryService : IDiscoveryService
{
    private readonly AgentOptions _options;
    private readonly ILocalNetworkInfo _network;
    private readonly IClassroomKeyProvider _keys;
    private readonly TimeProvider _time;
    private readonly ILogger<DiscoveryService> _logger;

    public DiscoveryService(IOptions<AgentOptions> options, ILocalNetworkInfo network, IClassroomKeyProvider keys,
        TimeProvider time, ILogger<DiscoveryService> logger)
    {
        _options = options.Value;
        _network = network;
        _keys = keys;
        _time = time;
        _logger = logger;
    }

    public async Task<DiscoveryResult> FindTeacherAsync(StudentSettings settings, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Discovery Started");
        if (!string.IsNullOrWhiteSpace(settings.TeacherAddress))
            return new DiscoveryResult(await ResolveConfiguredAsync(settings, cancellationToken).ConfigureAwait(false), false);

        var key = _keys.GetKey(settings.ClassroomCode);
        if (key is null) return new DiscoveryResult(null, false);

        var targets = _network.GetBroadcastTargets();
        if (targets.Count == 0)
        {
            _logger.LogWarning("No active network adapter; discovery postponed.");
            return new DiscoveryResult(null, false);
        }

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var nonce = HandshakeCrypto.NewNonce();
        var deadline = _time.GetUtcNow() + TimeSpan.FromSeconds(_options.DiscoveryTimeoutSeconds);
        var rejected = false;

        while (_time.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendRequestAsync(udp, targets, settings.DiscoveryPort, nonce, cancellationToken).ConfigureAwait(false);

            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(_options.DiscoveryRetryIntervalMilliseconds);
            try
            {
                while (true)
                {
                    var datagram = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                    var teacher = Evaluate(datagram, key, nonce, ref rejected);
                    if (teacher is not null)
                    {
                        _logger.LogInformation("Teacher Found at {Address}:{Port}", teacher.Address, teacher.Port);
                        return new DiscoveryResult(teacher, false);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Retry window elapsed; send the request again.
            }
        }
        return new DiscoveryResult(null, rejected);
    }

    private async Task SendRequestAsync(UdpClient udp, IReadOnlyList<IPAddress> targets, int port, string nonce, CancellationToken ct)
    {
        var request = new DiscoveryPacket(ProtocolConstants.ServiceName, ProtocolConstants.DiscoveryVersion,
            MessageTypes.DiscoveryRequest, ProtocolConstants.DeviceStudent, 0, string.Empty, string.Empty, string.Empty,
            nonce, _time.GetUtcNow().ToUnixTimeMilliseconds(), string.Empty);
        var bytes = Encoding.UTF8.GetBytes(MessageSerializer.Serialize(request));
        foreach (var target in targets)
        {
            try
            {
                await udp.SendAsync(bytes, new IPEndPoint(target, port), ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Discovery request to {Target} failed.", target);
            }
        }
    }

    private ConnectionInfo? Evaluate(UdpReceiveResult datagram, byte[] key, string nonce, ref bool rejected)
    {
        if (datagram.Buffer.Length is 0 or > ProtocolConstants.MaxDatagramBytes) return null;
        DiscoveryPacket packet;
        try
        {
            packet = MessageSerializer.Deserialize<DiscoveryPacket>(Encoding.UTF8.GetString(datagram.Buffer));
        }
        catch (ProtocolException)
        {
            return null; // Not ours (or junk): ignore silently.
        }

        if (packet.Service != ProtocolConstants.ServiceName || packet.Version != ProtocolConstants.DiscoveryVersion
            || packet.Type != MessageTypes.DiscoveryResponse || packet.Device != ProtocolConstants.DeviceTeacher
            || packet.Nonce != nonce || string.IsNullOrEmpty(packet.Signature))
            return null;

        var source = datagram.RemoteEndPoint.Address;
        var loopbackOk = _options.AllowLoopbackTeacher && IPAddress.IsLoopback(source);
        if (!loopbackOk && (!AddressPolicy.IsPermitted(source, false) || !_network.IsOnLocalSubnet(source)))
        {
            _logger.LogWarning("Security Error: discovery response from a non-local address {Source} was ignored.", source);
            return null;
        }

        var age = (_time.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(packet.Timestamp)).Duration();
        if (age > _options.MaxClockSkew || packet.Port is < 1 or > 65535) return null;

        if (!DiscoverySigner.Verify(key, packet))
        {
            rejected = true;
            _logger.LogWarning("Security Error: discovery response from {Source} failed cryptographic validation (wrong classroom).", source);
            return null;
        }

        return new ConnectionInfo(source, packet.Port, packet.TeacherId, packet.TeacherName, packet.Classroom, TeacherSource.Discovery);
    }

    private async Task<ConnectionInfo?> ResolveConfiguredAsync(StudentSettings settings, CancellationToken ct)
    {
        var host = settings.TeacherAddress.Trim();
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "Teacher host name {Host} could not be resolved.", host);
            return null;
        }

        var permitted = addresses.FirstOrDefault(a => AddressPolicy.IsPermitted(a, _options.AllowLoopbackTeacher));
        if (permitted is null)
        {
            _logger.LogWarning("Security Error: configured Teacher address {Host} is not a local network address.", host);
            return null;
        }
        var port = settings.TeacherPort > 0 ? settings.TeacherPort : _options.DefaultTeacherPort;
        return new ConnectionInfo(permitted, port, string.Empty, string.Empty, settings.ClassroomName, TeacherSource.Manual);
    }
}
