using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ClassroomControl.Shared.Communication.Messages;
using ClassroomControl.Shared.Communication.Protocol;
using ClassroomControl.Shared.Communication.Security;
using ClassroomControl.Shared.Communication.Tcp;
using ClassroomControl.StudentAgent.Models;

namespace ClassroomControl.TestKit;

public enum ApprovalMode { Auto, Manual, Reject }

public sealed class MockTeacherOptions
{
    public string ClassroomCode { get; set; } = string.Empty;
    public string ClassroomId { get; set; } = "8-A";
    public string ClassroomName { get; set; } = "8-A";
    public string TeacherId { get; set; } = "teacher-pc";
    public string TeacherName { get; set; } = "Teacher PC";
    public IPAddress BindAddress { get; set; } = IPAddress.Loopback;
    public int TcpPort { get; set; }
    public int DiscoveryPort { get; set; }
    public ApprovalMode Approval { get; set; } = ApprovalMode.Auto;
    public bool RequireAgentActive { get; set; }
    /// <summary>Protocol version announced in CHALLENGE (to test incompatibility handling).</summary>
    public string ProtocolVersion { get; set; } = ProtocolConstants.Current.ToString();
    /// <summary>Shifts the challenge timestamp (negative = expired challenge).</summary>
    public TimeSpan ChallengeTimeOffset { get; set; }
    public bool EnableDiscovery { get; set; } = true;
    /// <summary>When false the Teacher stops answering heartbeats (to test the heartbeat timeout).</summary>
    public bool AckHeartbeats { get; set; } = true;
    /// <summary>Reuse an existing certificate (a restarted Teacher keeps its identity). Null = generate a new self-signed one.</summary>
    public X509Certificate2? Certificate { get; set; }
}

public sealed class MockDevice
{
    private int _heartbeats;
    private int _connections;
    private int _connected;
    public string DeviceId { get; init; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string StudentName { get; set; } = string.Empty;
    public string LocalIp { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public RegistrationState Registration { get; set; }
    public string? SessionId { get; set; }
    public int HeartbeatCount => Volatile.Read(ref _heartbeats);
    public int ConnectionCount => Volatile.Read(ref _connections);
    public bool IsConnected => Volatile.Read(ref _connected) == 1;
    public string? LastHeartbeatStatus { get; set; }
    internal void CountHeartbeat() => Interlocked.Increment(ref _heartbeats);
    internal void MarkConnected(bool value)
    {
        Volatile.Write(ref _connected, value ? 1 : 0);
        if (value) Interlocked.Increment(ref _connections);
    }
    internal SecureChannel? Channel { get; set; }
    internal RecordingChannel? Recorder { get; set; }
    internal CancellationTokenSource? Cts { get; set; }
}

/// <summary>A real (TLS + UDP discovery) Teacher implementation of the protocol used by tests and the simulator.</summary>
public sealed class MockTeacherServer : IAsyncDisposable
{
    private readonly MockTeacherOptions _options;
    private readonly byte[] _key;
    private readonly X509Certificate2 _certificate;
    private readonly bool _ownsCertificate;
    private readonly string _certFingerprint;
    private readonly ConcurrentDictionary<string, MockDevice> _devices = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> _pending = new();
    private readonly ConcurrentBag<Task> _connectionTasks = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private UdpClient? _udp;
    private Task? _acceptLoop;
    private Task? _discoveryLoop;

    public MockTeacherServer(MockTeacherOptions options)
    {
        _options = options;
        _key = ClassroomCode.DeriveKey(options.ClassroomCode);
        _certificate = options.Certificate ?? CreateCertificate();
        _ownsCertificate = options.Certificate is null;
        _certFingerprint = CertificateFingerprint.Of(_certificate);
        TcpPort = options.TcpPort;
        DiscoveryPort = options.DiscoveryPort;
    }

    public int TcpPort { get; private set; }
    public int DiscoveryPort { get; private set; }
    public string CertificateFingerprintHex => _certFingerprint;
    public X509Certificate2 Certificate => _certificate;
    public IReadOnlyCollection<MockDevice> Devices => _devices.Values.ToList();
    public MockDevice? GetDevice(string deviceId) => _devices.TryGetValue(deviceId, out var d) ? d : null;
    public int DiscoveryRequestsAnswered;

    public void Start()
    {
        _listener = new TcpListener(_options.BindAddress, TcpPort);
        _listener.Start();
        TcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));

        if (_options.EnableDiscovery)
        {
            _udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(_options.BindAddress, DiscoveryPort));
            DiscoveryPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _discoveryLoop = Task.Run(() => DiscoveryLoopAsync(_udp, _cts.Token));
        }
    }

    /// <summary>Stops listening and drops every connection (simulates a Teacher PC going away).</summary>
    public async Task StopAsync()
    {
        _listener?.Stop();
        _udp?.Dispose();
        foreach (var d in _devices.Values) SafeCancel(d.Cts);
        await WaitQuietlyAsync(_acceptLoop).ConfigureAwait(false);
        await WaitQuietlyAsync(_discoveryLoop).ConfigureAwait(false);
        await WaitQuietlyAsync(Task.WhenAll(_connectionTasks)).ConfigureAwait(false);
        _listener = null;
        _udp = null;
    }

    public Task ApproveAsync(string deviceId) => SetRegistrationAsync(deviceId, RegistrationState.Approved);
    public Task RejectAsync(string deviceId) => SetRegistrationAsync(deviceId, RegistrationState.Rejected);

    private async Task SetRegistrationAsync(string deviceId, RegistrationState state)
    {
        var device = Require(deviceId);
        device.Registration = state;
        await device.Channel!.SendAsync(MessageTypes.RegistrationUpdate,
            new RegistrationUpdateMessage(state, null, new AgentPolicy(_options.RequireAgentActive)), _cts.Token).ConfigureAwait(false);
    }

    public async Task<CommandResult> SendCommandAsync(string deviceId, string name, TimeSpan? timeout = null, JsonElement? parameters = null)
    {
        var device = Require(deviceId);
        var commandId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[commandId] = tcs;
        try
        {
            await device.Channel!.SendAsync(MessageTypes.Command, new CommandRequest(commandId, name, parameters), _cts.Token).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(commandId, out _);
        }
    }

    /// <summary>Closes the device's connection from the Teacher side (without a DISCONNECT message).</summary>
    public void DropConnection(string deviceId) => SafeCancel(Require(deviceId).Cts);

    private static void SafeCancel(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The connection already ended.
        }
    }

    /// <summary>Sends the previous frame again - a replay attack the Student must detect.</summary>
    public Task ReplayLastFrameAsync(string deviceId)
    {
        var recorder = Require(deviceId).Recorder ?? throw new InvalidOperationException("No frame recorded.");
        return recorder.ReplayLastAsync(_cts.Token);
    }

    public async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25).ConfigureAwait(false);
        }
        return condition();
    }

    private MockDevice Require(string deviceId) =>
        _devices.TryGetValue(deviceId, out var d) && d.Channel is not null ? d : throw new InvalidOperationException($"Device {deviceId} is not connected.");

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _connectionTasks.Add(Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var ct = connectionCts.Token;
        MockDevice? device = null;
        using (client)
        {
            try
            {
                var ssl = new SslStream(client.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, ct).ConfigureAwait(false);
                var recorder = new RecordingChannel(new FramedConnection(ssl));
                await using var framed = recorder;

                device = await HandshakeAsync(recorder, connectionCts, ct).ConfigureAwait(false);
                if (device is null) return;
                await ServeAsync(device, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException
                                           or AuthenticationException or ProtocolException or EndOfStreamException)
            {
                // Peer went away or violated the protocol: just end this connection.
            }
            finally
            {
                device?.MarkConnected(false);
            }
        }
    }

    private async Task<MockDevice?> HandshakeAsync(RecordingChannel channel, CancellationTokenSource connectionCts, CancellationToken ct)
    {
        var helloWire = MessageSerializer.DeserializeWire(await channel.ReadFrameAsync(ct).ConfigureAwait(false) ?? throw new EndOfStreamException());
        var hello = MessageSerializer.Deserialize<HelloMessage>(helloWire.Payload);

        var teacherNonce = HandshakeCrypto.NewNonce();
        var timestamp = (DateTimeOffset.UtcNow + _options.ChallengeTimeOffset).ToUnixTimeMilliseconds();
        var challenge = new ChallengeMessage(_options.ProtocolVersion, _options.TeacherId, _options.TeacherName, _options.ClassroomId,
            _options.ClassroomName, teacherNonce, timestamp,
            HandshakeCrypto.ServerProof(_key, hello.DeviceId, hello.Nonce, teacherNonce, _certFingerprint, timestamp, _options.TeacherId, _options.ClassroomId));
        await WriteHandshakeAsync(channel, MessageTypes.Challenge, challenge, ct).ConfigureAwait(false);

        var responseWire = MessageSerializer.DeserializeWire(await channel.ReadFrameAsync(ct).ConfigureAwait(false) ?? throw new EndOfStreamException());
        if (responseWire.Type != MessageTypes.AuthResponse) return null;
        var response = MessageSerializer.Deserialize<AuthResponseMessage>(responseWire.Payload);

        var expected = HandshakeCrypto.ClientProof(_key, hello.DeviceId, hello.Nonce, teacherNonce, _certFingerprint, timestamp);
        if (response.DeviceId != hello.DeviceId || !HandshakeCrypto.ProofEquals(expected, response.ClientProof))
        {
            await WriteHandshakeAsync(channel, MessageTypes.AuthResult,
                new AuthResultMessage(false, ErrorCodes.InvalidClassroomCode, "Classroom code noto‘g‘ri.", null, RegistrationState.NotRegistered, null, string.Empty), ct).ConfigureAwait(false);
            return null;
        }

        var device = _devices.GetOrAdd(hello.DeviceId, id => new MockDevice { DeviceId = id });
        if (_options.Approval == ApprovalMode.Reject)
        {
            await WriteHandshakeAsync(channel, MessageTypes.AuthResult,
                new AuthResultMessage(false, ErrorCodes.Rejected, "Rejected", null, RegistrationState.Rejected, null, string.Empty), ct).ConfigureAwait(false);
            return null;
        }

        device.ComputerName = response.ComputerName;
        device.StudentName = response.StudentName;
        device.LocalIp = response.LocalIp;
        device.AgentVersion = response.AgentVersion;
        if (device.Registration != RegistrationState.Approved)
            device.Registration = _options.Approval == ApprovalMode.Auto ? RegistrationState.Approved : RegistrationState.Pending;

        var sessionId = Guid.NewGuid().ToString("N");
        var proof = HandshakeCrypto.ResultProof(_key, hello.DeviceId, hello.Nonce, teacherNonce, sessionId, device.Registration.ToString(), true);
        await WriteHandshakeAsync(channel, MessageTypes.AuthResult,
            new AuthResultMessage(true, null, null, sessionId, device.Registration, new AgentPolicy(_options.RequireAgentActive), proof), ct).ConfigureAwait(false);

        var keys = HandshakeCrypto.DeriveSessionKeys(_key, hello.Nonce, teacherNonce, sessionId);
        device.Channel = new SecureChannel(channel, sessionId, keys.TeacherToClient, keys.ClientToTeacher, TimeProvider.System,
            TimeSpan.FromMinutes(2), ProtocolVersion.Parse(_options.ProtocolVersion));
        device.Recorder = channel;
        device.SessionId = sessionId;
        device.Cts = connectionCts;
        device.MarkConnected(true);
        return device;
    }

    private async Task ServeAsync(MockDevice device, CancellationToken ct)
    {
        var channel = device.Channel!;
        while (!ct.IsCancellationRequested)
        {
            var message = await channel.ReceiveAsync(ct).ConfigureAwait(false);
            if (message is null) return;
            switch (message.Type)
            {
                case MessageTypes.Heartbeat:
                    var hb = MessageSerializer.Deserialize<HeartbeatMessage>(message.Payload);
                    device.LastHeartbeatStatus = hb.Status;
                    device.CountHeartbeat();
                    if (_options.AckHeartbeats) await channel.SendAsync(MessageTypes.HeartbeatAck, new { }, ct).ConfigureAwait(false);
                    break;
                case MessageTypes.CommandResponse:
                    var result = MessageSerializer.Deserialize<CommandResult>(message.Payload);
                    if (_pending.TryGetValue(result.CommandId, out var tcs)) tcs.TrySetResult(result);
                    break;
                case MessageTypes.Disconnect:
                    return;
            }
        }
    }

    private async Task DiscoveryLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (ex is SocketException se && se.SocketErrorCode == SocketError.ConnectionReset) continue;
                return;
            }

            try
            {
                var request = MessageSerializer.Deserialize<DiscoveryPacket>(Encoding.UTF8.GetString(received.Buffer));
                if (request.Service != ProtocolConstants.ServiceName || request.Type != MessageTypes.DiscoveryRequest) continue;
                var response = DiscoverySigner.WithSignature(_key, new DiscoveryPacket(ProtocolConstants.ServiceName,
                    ProtocolConstants.DiscoveryVersion, MessageTypes.DiscoveryResponse, ProtocolConstants.DeviceTeacher, TcpPort,
                    _options.ClassroomName, _options.TeacherId, _options.TeacherName, request.Nonce,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), string.Empty));
                await udp.SendAsync(Encoding.UTF8.GetBytes(MessageSerializer.Serialize(response)), received.RemoteEndPoint, ct).ConfigureAwait(false);
                Interlocked.Increment(ref DiscoveryRequestsAnswered);
            }
            catch (Exception ex) when (ex is ProtocolException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // Malformed request or socket closing: ignore.
            }
        }
    }

    private static Task WriteHandshakeAsync<T>(IFramedChannel channel, string type, T payload, CancellationToken ct) where T : class =>
        channel.WriteFrameAsync(MessageSerializer.Serialize(new WireMessage
        {
            ProtocolVersion = ProtocolConstants.Current.ToString(),
            Type = type,
            MessageId = Guid.NewGuid().ToString("N"),
            Payload = MessageSerializer.Serialize(payload),
        }), ct);

    private static X509Certificate2 CreateCertificate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ClassroomControl Mock Teacher", ecdsa, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    private static async Task WaitQuietlyAsync(Task? task)
    {
        if (task is null) return;
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or SocketException or ObjectDisposedException or IOException)
        {
            // Already stopped.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
        if (_ownsCertificate) _certificate.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Remembers the last frame written so a test can replay it.</summary>
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

    public Task ReplayLastAsync(CancellationToken ct) =>
        _last is null ? Task.CompletedTask : _inner.WriteFrameAsync(_last, ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
