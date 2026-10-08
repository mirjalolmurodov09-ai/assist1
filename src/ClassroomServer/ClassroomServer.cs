using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ClassroomControl.ClassroomServer.Data;
using ClassroomControl.ClassroomServer.Security;
using Microsoft.Extensions.Logging;

namespace ClassroomControl.ClassroomServer;

/// <summary>The Teacher-side "Local Classroom Server": answers UDP discovery, accepts TLS connections from Student Agents,
/// authenticates them with the classroom code, keeps the approved-computer registry and relays commands, screens and input.
/// Unknown computers stay Pending (no commands, no frames) until a teacher approves them.</summary>
public sealed class ClassroomServer : IAsyncDisposable
{
    private const int MaxNameLength = 64;
    private const int MaxIpLength = 45;
    private static readonly TimeSpan SeenWriteInterval = TimeSpan.FromSeconds(30);

    private readonly ClassroomServerOptions _options;
    private readonly ILogger<ClassroomServer> _logger;
    private readonly TimeProvider _time;
    private readonly ISecretProtector _protector;
    private readonly X509Certificate2 _certificate;
    private readonly ConcurrentDictionary<string, DeviceConnection> _online = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeenWrite = new();
    private readonly FailureThrottle _throttle;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _tasks = [];
    private readonly object _tasksGate = new();
    private ClassroomRecord _classroom;
    private string _code;
    private byte[] _key;
    private TcpListener? _listener;
    private UdpClient? _udp;
    private int _connectionCount;
    private volatile string _actor = "system";
    private readonly ConcurrentDictionary<string, long> _heartbeats = new();
    private readonly ConcurrentDictionary<string, string> _lastHeartbeatStatus = new();

    public ClassroomServer(ClassroomServerOptions options, ClassroomStore store, ISecretProtector protector, ILogger<ClassroomServer> logger, TimeProvider? time = null)
    {
        _options = options;
        Store = store;
        _protector = protector;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _throttle = new FailureThrottle(_time);
        Settings = new ServerSettings(store);

        _classroom = store.GetFirstClassroom() ?? CreateClassroom();
        var code = protector.Unprotect(_classroom.CodeProtected);
        if (code is null || !ClassroomCode.IsValidFormat(code))
        {
            code = ClassroomCode.Generate(_time.GetUtcNow());
            store.UpdateClassroomCode(_classroom.Id, protector.Protect(code));
            _classroom = store.GetFirstClassroom()!;
        }
        _code = code;
        _key = ClassroomCode.DeriveKey(code);
        _certificate = ServerCertificateProvider.LoadOrCreate(options.CertificatePath, store, protector);
        CertificateFingerprint = CertificateFingerprintOf(_certificate);
        TcpPort = options.TcpPort;
        DiscoveryPort = options.DiscoveryPort;
    }

    public ClassroomStore Store { get; }
    public ServerSettings Settings { get; }
    public int TcpPort { get; private set; }
    public int DiscoveryPort { get; private set; }
    public string CertificateFingerprint { get; }
    public X509Certificate2 Certificate => _certificate;
    public ClassroomRecord Classroom => _classroom;
    public string ClassroomCodeText => _code;
    public int DiscoveryRequestsAnswered;

    /// <summary>Name written to the audit log for actions started from the UI.</summary>
    public string Actor
    {
        get => _actor;
        set => _actor = string.IsNullOrWhiteSpace(value) ? "system" : value;
    }

    public event EventHandler<DeviceSnapshot>? DeviceChanged;
    public event EventHandler<string>? DeviceRemoved;
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived;
    public event EventHandler<LogEntry>? LogAdded;

    private static string CertificateFingerprintOf(X509Certificate2 certificate) => Shared.Communication.Security.CertificateFingerprint.Of(certificate);

    private ClassroomRecord CreateClassroom()
    {
        var code = ClassroomCode.Generate(_time.GetUtcNow());
        return Store.AddClassroom(_options.ClassroomName, _protector.Protect(code), Guid.NewGuid().ToString("N"), _time.GetUtcNow());
    }

    // ------------------------------------------------------------------ life cycle

    public void Start()
    {
        _listener = new TcpListener(_options.BindAddress, TcpPort);
        _listener.Start();
        TcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Track(Task.Run(() => AcceptLoopAsync(_listener, _cts.Token)));
        Track(Task.Run(() => WatchdogLoopAsync(_cts.Token)));

        _udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(_options.BindAddress, DiscoveryPort));
        DiscoveryPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        Track(Task.Run(() => DiscoveryLoopAsync(_udp, _cts.Token)));

        Audit("Server started", string.Empty, "OK", $"TCP {TcpPort}, UDP {DiscoveryPort}");
    }

    /// <summary>Stops listening and drops every connection (a Teacher restart).</summary>
    public async Task StopAsync()
    {
        _listener?.Stop();
        _udp?.Dispose();
        foreach (var device in _online.Values) device.Disconnect("Teacher stopped");
        Task[] snapshot;
        lock (_tasksGate) snapshot = [.. _tasks];
        try
        {
            await Task.WhenAll(snapshot).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or SocketException or ObjectDisposedException or IOException)
        {
            _logger.LogDebug(ex, "Server tasks did not finish cleanly.");
        }
        _listener = null;
        _udp = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
        _certificate.Dispose();
        _cts.Dispose();
    }

    private void Track(Task task)
    {
        lock (_tasksGate)
        {
            _tasks.RemoveAll(t => t.IsCompleted);
            _tasks.Add(task);
        }
    }

    // ------------------------------------------------------------------ discovery

    private async Task DiscoveryLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue; // ICMP port-unreachable from a client that already left.
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            try
            {
                if (received.Buffer.Length > ProtocolConstants.MaxDatagramBytes) continue;
                var source = received.RemoteEndPoint.Address;
                if (!AddressPolicy.IsPermitted(source, _options.AllowLoopbackClients) || !_throttle.AllowDiscovery(source)) continue;

                var request = MessageSerializer.Deserialize<DiscoveryPacket>(Encoding.UTF8.GetString(received.Buffer));
                if (request.Service != ProtocolConstants.ServiceName || request.Type != MessageTypes.DiscoveryRequest
                    || request.Version != ProtocolConstants.DiscoveryVersion || !HandshakeCrypto.IsValidNonce(request.Nonce)) continue;

                var response = DiscoverySigner.WithSignature(_key, new DiscoveryPacket(ProtocolConstants.ServiceName,
                    ProtocolConstants.DiscoveryVersion, MessageTypes.DiscoveryResponse, ProtocolConstants.DeviceTeacher, TcpPort,
                    _classroom.Name, _classroom.TeacherId, _options.TeacherName, request.Nonce,
                    _time.GetUtcNow().ToUnixTimeMilliseconds(), string.Empty));
                await udp.SendAsync(Encoding.UTF8.GetBytes(MessageSerializer.Serialize(response)), received.RemoteEndPoint, ct).ConfigureAwait(false);
                Interlocked.Increment(ref DiscoveryRequestsAnswered);
            }
            catch (Exception ex) when (ex is ProtocolException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // Malformed request or socket closing: ignore.
            }
        }
    }

    // ------------------------------------------------------------------ connections

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

            if (Volatile.Read(ref _connectionCount) >= _options.MaxConnections)
            {
                client.Dispose();
                continue;
            }
            Interlocked.Increment(ref _connectionCount);
            Track(Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        DeviceConnection? device = null;
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        try
        {
            using (client)
            {
                if (!AddressPolicy.IsPermitted(remote, _options.AllowLoopbackClients))
                {
                    Audit("Connection refused", remote.ToString(), "Denied", "Address is not on a local network");
                    return;
                }
                if (!_throttle.AllowConnection(remote))
                {
                    Audit("Connection refused", remote.ToString(), "Denied", "Too many failed attempts");
                    return;
                }

                using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                var ct = connectionCts.Token;
                var ssl = new SslStream(client.GetStream(), false);
                using (var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(_options.HandshakeTimeoutSeconds));
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    }, handshakeTimeout.Token).ConfigureAwait(false);
                }

                var framed = new FramedConnection(ssl);
                var recorder = _options.Faults?.RecordFrames == true ? new RecordingChannel(framed) : null;
                IFramedChannel transport = recorder ?? (IFramedChannel)framed;
                await using (transport.ConfigureAwait(false))
                {
                    using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    handshakeCts.CancelAfter(TimeSpan.FromSeconds(_options.HandshakeTimeoutSeconds));
                    device = await HandshakeAsync(transport, recorder, connectionCts, remote, handshakeCts.Token).ConfigureAwait(false);
                    if (device is null) return;
                    await ServeAsync(device, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException
                                       or AuthenticationException or EndOfStreamException)
        {
            _logger.LogDebug("Connection from {Remote} ended: {Reason}", remote, ex.Message);
        }
        catch (ProtocolException ex)
        {
            Audit("Security error", device?.DeviceId ?? remote.ToString(), "Dropped", ex.ErrorCode);
            device?.Disconnect(ex.ErrorCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while serving {Remote}", remote);
        }
        finally
        {
            Interlocked.Decrement(ref _connectionCount);
            if (device is not null) OnDisconnected(device);
        }
    }

    private async Task<DeviceConnection?> HandshakeAsync(IFramedChannel channel, RecordingChannel? recorder, CancellationTokenSource connectionCts, IPAddress remote, CancellationToken ct)
    {
        var helloWire = MessageSerializer.DeserializeWire(await channel.ReadFrameAsync(ct).ConfigureAwait(false) ?? throw new EndOfStreamException());
        if (helloWire.Type != MessageTypes.Hello) return null;
        var hello = MessageSerializer.Deserialize<HelloMessage>(helloWire.Payload);

        if (!Guid.TryParse(hello.DeviceId, out _) || !HandshakeCrypto.IsValidNonce(hello.Nonce))
        {
            _throttle.RecordFailure(remote);
            return null;
        }
        if (!ProtocolVersion.TryParse(hello.ProtocolVersion, out var studentVersion)
            || !ProtocolVersion.TryNegotiate(ProtocolConstants.Current, studentVersion, out var negotiated))
        {
            await SendResultAsync(channel, false, ErrorCodes.VersionMismatch,
                $"Student protocol versiyasi ({hello.ProtocolVersion}) Teacher ({ProtocolConstants.Current}) bilan mos emas. Student Agentni yangilang.", null, RegistrationState.NotRegistered, null, ct).ConfigureAwait(false);
            Audit("Authentication failed", hello.DeviceId, "Version mismatch", hello.ProtocolVersion);
            return null;
        }

        var faults = _options.Faults;
        var teacherNonce = HandshakeCrypto.NewNonce();
        var timestamp = (_time.GetUtcNow() + (faults?.ChallengeTimeOffset ?? TimeSpan.Zero)).ToUnixTimeMilliseconds();
        var versionText = faults?.ProtocolVersion ?? negotiated.ToString();
        var certificateFingerprint = CertificateFingerprint;
        await WriteHandshakeAsync(channel, MessageTypes.Challenge, new ChallengeMessage(versionText, _classroom.TeacherId, _options.TeacherName,
            _classroom.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), _classroom.Name, teacherNonce, timestamp,
            HandshakeCrypto.ServerProof(_key, hello.DeviceId, hello.Nonce, teacherNonce, certificateFingerprint, timestamp, _classroom.TeacherId,
                _classroom.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))), ct).ConfigureAwait(false);

        var responseWire = MessageSerializer.DeserializeWire(await channel.ReadFrameAsync(ct).ConfigureAwait(false) ?? throw new EndOfStreamException());
        if (responseWire.Type != MessageTypes.AuthResponse) return null;
        var response = MessageSerializer.Deserialize<AuthResponseMessage>(responseWire.Payload);

        var expected = HandshakeCrypto.ClientProof(_key, hello.DeviceId, hello.Nonce, teacherNonce, certificateFingerprint, timestamp);
        if (response.DeviceId != hello.DeviceId || !HandshakeCrypto.ProofEquals(expected, response.ClientProof))
        {
            _throttle.RecordFailure(remote);
            Audit("Authentication failed", $"{hello.ComputerName} ({remote})", "Wrong classroom code", string.Empty);
            await SendResultAsync(channel, false, ErrorCodes.InvalidClassroomCode, "Classroom code noto‘g‘ri.", null, RegistrationState.NotRegistered, null, ct).ConfigureAwait(false);
            return null;
        }

        var computerName = Clip(response.ComputerName, MaxNameLength);
        var studentName = Clip(response.StudentName, MaxNameLength);
        var now = _time.GetUtcNow();
        var existing = Store.GetComputer(hello.DeviceId);
        var computer = Store.UpsertComputer(hello.DeviceId, _classroom.Id, computerName, studentName, Clip(response.LocalIp, MaxIpLength), now);
        Store.EnsureStudent(studentName, _classroom.Id, now);

        if (computer.Status == RegistrationState.Rejected)
        {
            await SendResultAsync(channel, false, ErrorCodes.Rejected, "Teacher bu kompyuterni rad etdi.", null, RegistrationState.Rejected, null, ct).ConfigureAwait(false);
            Audit("Connection refused", computer.Title, "Rejected device", string.Empty);
            return null;
        }
        if (existing is null) Audit("Registration request", computerName, "Pending", $"Student: {studentName}, IP: {response.LocalIp}");

        var sessionId = Guid.NewGuid().ToString("N");
        var policy = new AgentPolicy(Settings.RequireAgentActive, Settings.AutoUnlockSeconds);
        var proof = HandshakeCrypto.ResultProof(_key, hello.DeviceId, hello.Nonce, teacherNonce, sessionId, computer.Status.ToString(), true);
        await WriteHandshakeAsync(channel, MessageTypes.AuthResult,
            new AuthResultMessage(true, null, null, sessionId, computer.Status, policy, proof), ct).ConfigureAwait(false);

        var keys = HandshakeCrypto.DeriveSessionKeys(_key, hello.Nonce, teacherNonce, sessionId);
        var secure = new SecureChannel(channel, sessionId, keys.TeacherToClient, keys.ClientToTeacher, _time, _options.MaxClockSkewSecondsSpan(), negotiated);
        var device = new DeviceConnection(hello.DeviceId, sessionId, secure, connectionCts, recorder, now) { Registration = computer.Status };

        if (_online.TryGetValue(hello.DeviceId, out var previous)) previous.Disconnect("Replaced by a new connection");
        _online[hello.DeviceId] = device;
        Store.OpenSession(sessionId, hello.DeviceId, now);
        Audit("Computer connected", computer.Title, computer.Status.ToString(), $"IP: {response.LocalIp}");
        Raise(hello.DeviceId);

        if (computer.Status == RegistrationState.Approved && string.IsNullOrEmpty(computer.MacAddress)) _ = Task.Run(() => FetchHardwareAsync(hello.DeviceId));
        return device;
    }

    private async Task ServeAsync(DeviceConnection device, CancellationToken ct)
    {
        var channel = device.Channel;
        while (!ct.IsCancellationRequested)
        {
            var message = await channel.ReceiveAsync(ct).ConfigureAwait(false);
            if (message is null) return;
            device.MarkReceived(_time.GetUtcNow());

            switch (message.Type)
            {
                case MessageTypes.Heartbeat:
                    _heartbeats.AddOrUpdate(device.DeviceId, 1, (_, n) => n + 1);
                    _lastHeartbeatStatus[device.DeviceId] = MessageSerializer.Deserialize<HeartbeatMessage>(message.Payload).Status ?? string.Empty;
                    if (_options.Faults?.AckHeartbeats != false)
                        await channel.SendAsync(MessageTypes.HeartbeatAck, new { }, ct).ConfigureAwait(false);
                    TouchSeen(device.DeviceId);
                    break;

                case MessageTypes.StatusUpdate:
                    device.Status = MessageSerializer.Deserialize<StatusUpdateMessage>(message.Payload);
                    Raise(device.DeviceId);
                    break;

                case MessageTypes.ScreenFrame:
                    if (device.Registration == RegistrationState.Approved)
                        FrameReceived?.Invoke(this, new FrameReceivedEventArgs(device.DeviceId, MessageSerializer.Deserialize<ScreenFrameMessage>(message.Payload)));
                    break;

                case MessageTypes.CommandResponse:
                    var result = MessageSerializer.Deserialize<CommandResult>(message.Payload);
                    if (device.Pending.TryRemove(result.CommandId, out var tcs)) tcs.TrySetResult(result);
                    break;

                case MessageTypes.Disconnect:
                    device.DisconnectReason = "Student disconnected";
                    return;
            }
        }
    }

    private void OnDisconnected(DeviceConnection device)
    {
        foreach (var pending in device.Pending.Values) pending.TrySetCanceled();
        // Only forget the device if this connection is still the registered one (a newer connection may have replaced it).
        var wasCurrent = _online.TryGetValue(device.DeviceId, out var current) && ReferenceEquals(current, device);
        if (wasCurrent) _online.TryRemove(device.DeviceId, out _);
        Store.CloseSession(device.SessionId, device.DisconnectReason, _time.GetUtcNow());
        if (!wasCurrent) return;
        var title = Store.GetComputer(device.DeviceId)?.Title ?? device.DeviceId;
        Audit("Computer disconnected", title, "Offline", device.DisconnectReason);
        Raise(device.DeviceId);
    }

    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatTimeoutSeconds / 3)), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                foreach (var device in _online.Values)
                    if (_time.GetUtcNow() - device.LastReceived > timeout) device.Disconnect("Heartbeat timeout");
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void TouchSeen(string deviceId)
    {
        var now = _time.GetUtcNow();
        if (_lastSeenWrite.TryGetValue(deviceId, out var last) && now - last < SeenWriteInterval) return;
        _lastSeenWrite[deviceId] = now;
        Store.TouchComputer(deviceId, now);
    }

    private async Task FetchHardwareAsync(string deviceId)
    {
        var outcome = await ExecuteAsync(deviceId, CommandNames.GetDeviceInfo, null, actor: "system").ConfigureAwait(false);
        if (!outcome.Success || outcome.Result?.Payload is not { } payload) return;
        try
        {
            var info = payload.Deserialize<DeviceInfo>(MessageSerializer.Options);
            if (info is null) return;
            Store.UpdateComputerHardware(deviceId, Clip(info.MacAddress, MaxNameLength), Clip(info.Cpu, 128), info.RamBytes);
            Raise(deviceId);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Device info payload could not be read.");
        }
    }

    // ------------------------------------------------------------------ registry

    public IReadOnlyList<DeviceSnapshot> Devices => [.. Store.ListComputers(_classroom.Id).Select(ToSnapshot)];

    public DeviceSnapshot? GetDevice(string deviceId) => Store.GetComputer(deviceId) is { } c ? ToSnapshot(c) : null;

    /// <summary>Number of heartbeats received from a computer since the server started, and the status text of the last one.</summary>
    public (long Count, string LastStatus) GetHeartbeatStats(string deviceId) =>
        (_heartbeats.GetValueOrDefault(deviceId), _lastHeartbeatStatus.GetValueOrDefault(deviceId, string.Empty));

    public bool IsOnline(string deviceId) => _online.ContainsKey(deviceId);

    private DeviceSnapshot ToSnapshot(ComputerRecord computer)
    {
        _online.TryGetValue(computer.DeviceId, out var device);
        return new DeviceSnapshot(computer, device is not null, device?.SessionId, device?.Status);
    }

    private void Raise(string deviceId)
    {
        if (GetDevice(deviceId) is { } snapshot) DeviceChanged?.Invoke(this, snapshot);
    }

    public async Task ApproveAsync(string deviceId)
    {
        var computer = Store.GetComputer(deviceId) ?? throw new InvalidOperationException("Noma'lum kompyuter.");
        Store.SetComputerStatus(deviceId, RegistrationState.Approved);
        Audit("Computer approved", computer.Title, "OK", string.Empty);
        if (_online.TryGetValue(deviceId, out var device))
        {
            device.Registration = RegistrationState.Approved;
            await device.Channel.SendAsync(MessageTypes.RegistrationUpdate,
                new RegistrationUpdateMessage(RegistrationState.Approved, null, new AgentPolicy(Settings.RequireAgentActive, Settings.AutoUnlockSeconds)), _cts.Token).ConfigureAwait(false);
            _ = Task.Run(() => FetchHardwareAsync(deviceId));
        }
        Raise(deviceId);
    }

    public async Task RejectAsync(string deviceId)
    {
        var computer = Store.GetComputer(deviceId) ?? throw new InvalidOperationException("Noma'lum kompyuter.");
        Store.SetComputerStatus(deviceId, RegistrationState.Rejected);
        Audit("Computer rejected", computer.Title, "OK", string.Empty);
        if (_online.TryGetValue(deviceId, out var device))
        {
            device.Registration = RegistrationState.Rejected;
            await device.Channel.SendAsync(MessageTypes.RegistrationUpdate,
                new RegistrationUpdateMessage(RegistrationState.Rejected, null, null), _cts.Token).ConfigureAwait(false);
        }
        Raise(deviceId);
    }

    /// <summary>Removes the computer from the registry; if it connects again it must be approved again.</summary>
    public void Remove(string deviceId)
    {
        var computer = Store.GetComputer(deviceId);
        if (computer is null) return;
        if (_online.TryGetValue(deviceId, out var device)) device.Disconnect("Removed by teacher");
        Store.DeleteComputer(deviceId);
        Audit("Computer removed", computer.Title, "OK", string.Empty);
        DeviceRemoved?.Invoke(this, deviceId);
    }

    public void SetGroup(string deviceId, long? groupId)
    {
        Store.SetComputerGroup(deviceId, groupId);
        Raise(deviceId);
    }

    public void Rename(string deviceId, string displayName)
    {
        Store.SetComputerDisplayName(deviceId, Clip(displayName.Trim(), MaxNameLength));
        Raise(deviceId);
    }

    /// <summary>Generates a new classroom code. Connected computers are dropped; they must be given the new code.</summary>
    public string RegenerateClassroomCode()
    {
        var code = ClassroomCode.Generate(_time.GetUtcNow());
        Store.UpdateClassroomCode(_classroom.Id, _protector.Protect(code));
        _classroom = Store.GetFirstClassroom()!;
        _code = code;
        _key = ClassroomCode.DeriveKey(code);
        foreach (var device in _online.Values) device.Disconnect("Classroom code changed");
        Audit("Classroom code changed", string.Empty, "OK", string.Empty);
        return code;
    }

    /// <summary>Uses an explicit classroom code (for example one already printed for the class). Connected computers are dropped.</summary>
    public void SetClassroomCode(string code)
    {
        code = ClassroomCode.Normalize(code);
        if (!ClassroomCode.IsValidFormat(code)) throw new ArgumentException("Classroom code formati noto‘g‘ri.", nameof(code));
        Store.UpdateClassroomCode(_classroom.Id, _protector.Protect(code));
        _classroom = Store.GetFirstClassroom()!;
        _code = code;
        _key = ClassroomCode.DeriveKey(code);
        foreach (var device in _online.Values) device.Disconnect("Classroom code changed");
        Audit("Classroom code changed", string.Empty, "OK", string.Empty);
    }

    public void RenameClassroom(string name)
    {
        Store.RenameClassroom(_classroom.Id, Clip(name.Trim(), MaxNameLength));
        _classroom = Store.GetFirstClassroom()!;
    }

    public GroupRecord AddGroup(string name) => Store.AddGroup(_classroom.Id, Clip(name.Trim(), MaxNameLength));
    public IReadOnlyList<GroupRecord> Groups => Store.ListGroups(_classroom.Id);

    // ------------------------------------------------------------------ commands

    public async Task<CommandOutcome> ExecuteAsync(string deviceId, string name, object? parameters, TimeSpan? timeout = null, string? actor = null)
    {
        var title = Store.GetComputer(deviceId)?.Title ?? deviceId;
        if (!_online.TryGetValue(deviceId, out var device))
            return Finish(deviceId, name, title, CommandOutcome.Failure(deviceId, name, "OFFLINE", "Qurilma hozir offline."), actor);
        if (device.Registration != RegistrationState.Approved)
            return Finish(deviceId, name, title, CommandOutcome.Failure(deviceId, name, ErrorCodes.NotRegistered, "Kompyuter hali tasdiqlanmagan."), actor);

        var commandId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Pending[commandId] = tcs;
        Store.AddCommand(commandId, deviceId, name, actor ?? _actor, _time.GetUtcNow());
        try
        {
            JsonElement? json = parameters is null ? null : JsonSerializer.SerializeToElement(parameters, MessageSerializer.Options);
            await device.Channel.SendAsync(MessageTypes.Command, new CommandRequest(commandId, name, json), _cts.Token).ConfigureAwait(false);
            var result = await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(_options.CommandTimeoutSeconds)).ConfigureAwait(false);
            var ok = result.Status == CommandStatus.Success;
            Store.CompleteCommand(commandId, result.Status.ToString(), result.ErrorCode, _time.GetUtcNow());
            return Finish(deviceId, name, title, new CommandOutcome(deviceId, name, ok, result.ErrorCode, result.Message ?? string.Empty, result), actor);
        }
        catch (TimeoutException)
        {
            Store.CompleteCommand(commandId, "Timeout", "TIMEOUT", _time.GetUtcNow());
            return Finish(deviceId, name, title, CommandOutcome.Failure(deviceId, name, "TIMEOUT", "Kompyuter javob bermadi."), actor);
        }
        catch (Exception ex) when (ex is TaskCanceledException or IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            Store.CompleteCommand(commandId, "Failed", "CONNECTION_LOST", _time.GetUtcNow());
            return Finish(deviceId, name, title, CommandOutcome.Failure(deviceId, name, "CONNECTION_LOST", "Kompyuter bilan aloqa uzildi."), actor);
        }
        finally
        {
            device.Pending.TryRemove(commandId, out _);
        }
    }

    public async Task<IReadOnlyList<CommandOutcome>> ExecuteManyAsync(IEnumerable<string> deviceIds, string name, object? parameters, TimeSpan? timeout = null) =>
        await Task.WhenAll(deviceIds.Distinct().Select(id => ExecuteAsync(id, name, parameters, timeout))).ConfigureAwait(false);

    private CommandOutcome Finish(string deviceId, string name, string title, CommandOutcome outcome, string? actor)
    {
        if (name is CommandNames.GetDeviceInfo or CommandNames.Ping or CommandNames.GetStatus && actor == "system") return outcome;
        Audit(name, title, outcome.Success ? "OK" : outcome.ErrorCode ?? "Failed", outcome.Message, actor);
        return outcome;
    }

    /// <summary>Takes a screenshot, saves it as <c>StudentName_ComputerName_YYYY-MM-DD_HH-mm-ss.jpg</c> and records it in the history.</summary>
    public async Task<(CommandOutcome Outcome, ScreenshotRecord? Record)> TakeScreenshotAsync(string deviceId)
    {
        var outcome = await ExecuteAsync(deviceId, CommandNames.Screenshot, null, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (!outcome.Success || outcome.Result?.Payload is not { } payload) return (outcome, null);

        var shot = payload.Deserialize<ScreenshotPayload>(MessageSerializer.Options);
        if (shot is null || string.IsNullOrEmpty(shot.ImageBase64)) return (CommandOutcome.Failure(deviceId, CommandNames.Screenshot, ErrorCodes.InvalidMessage, "Skrinshot bo'sh."), null);

        var computer = Store.GetComputer(deviceId)!;
        var taken = _time.GetLocalNow();
        var extension = shot.Format.Equals("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
        var fileName = $"{Sanitize(string.IsNullOrWhiteSpace(computer.StudentName) ? "Student" : computer.StudentName)}_{Sanitize(computer.ComputerName)}_{taken:yyyy-MM-dd_HH-mm-ss}.{extension}";
        Directory.CreateDirectory(_options.ScreenshotDirectory);
        var path = Path.Combine(_options.ScreenshotDirectory, fileName);
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(shot.ImageBase64)).ConfigureAwait(false);
        var id = Store.AddScreenshot(computer.Id, path, taken, computer.StudentName, computer.ComputerName);
        return (outcome, new ScreenshotRecord(id, computer.Id, path, taken, computer.StudentName, computer.ComputerName));
    }

    public Task<CommandOutcome> LockAsync(string deviceId, string message) => ExecuteAsync(deviceId, CommandNames.Lock, new LockParameters(message));
    public Task<CommandOutcome> UnlockAsync(string deviceId) => ExecuteAsync(deviceId, CommandNames.Unlock, null);
    public Task<CommandOutcome> SendMessageAsync(string deviceId, string text, string? title = null) =>
        ExecuteAsync(deviceId, CommandNames.SendMessage, new SendMessageParameters(text, title, false));
    public Task<CommandOutcome> RestartAsync(string deviceId, int delaySeconds = 10) => ExecuteAsync(deviceId, CommandNames.Restart, new PowerParameters(delaySeconds, null));
    public Task<CommandOutcome> ShutdownAsync(string deviceId, int delaySeconds = 10) => ExecuteAsync(deviceId, CommandNames.Shutdown, new PowerParameters(delaySeconds, null));
    public Task<CommandOutcome> StartStreamAsync(string deviceId, int fps, int quality, int maxWidth) =>
        ExecuteAsync(deviceId, CommandNames.StartScreenStream, new StartStreamParameters(fps, quality, maxWidth));
    public Task<CommandOutcome> StopStreamAsync(string deviceId) => ExecuteAsync(deviceId, CommandNames.StopScreenStream, null);
    public Task<CommandOutcome> StartRemoteControlAsync(string deviceId) => ExecuteAsync(deviceId, CommandNames.StartRemoteControl, null);
    public Task<CommandOutcome> StopRemoteControlAsync(string deviceId) => ExecuteAsync(deviceId, CommandNames.StopRemoteControl, null);
    public Task<CommandOutcome> StartApplicationAsync(string deviceId, string target, string? arguments = null) =>
        ExecuteAsync(deviceId, CommandNames.StartApplication, new StartApplicationParameters(target, arguments));
    public Task<CommandOutcome> StopApplicationAsync(string deviceId, string processName) =>
        ExecuteAsync(deviceId, CommandNames.StopApplication, new StopApplicationParameters(processName));

    // ------------------------------------------------------------------ streams

    /// <summary>Sends one input event to a computer that is being remotely controlled. Fire-and-forget.</summary>
    public async Task SendMouseAsync(string deviceId, MouseEventMessage mouse)
    {
        if (_online.TryGetValue(deviceId, out var device) && device.Status?.RemoteControlActive == true)
            await SafeSendAsync(device, MessageTypes.MouseEvent, mouse).ConfigureAwait(false);
    }

    public async Task SendKeyboardAsync(string deviceId, KeyboardEventMessage key)
    {
        if (_online.TryGetValue(deviceId, out var device) && device.Status?.RemoteControlActive == true)
            await SafeSendAsync(device, MessageTypes.KeyboardEvent, key).ConfigureAwait(false);
    }

    /// <summary>Delivers a Teacher-screen frame to the given computers. Slow computers skip frames instead of slowing the others.</summary>
    public async Task<int> SendTeacherFrameAsync(IEnumerable<string> deviceIds, ScreenFrameMessage frame)
    {
        var tasks = deviceIds.Select(async id =>
        {
            if (!_online.TryGetValue(id, out var device) || device.Registration != RegistrationState.Approved) return false;
            try
            {
                return await device.TrySendFrameAsync(MessageTypes.TeacherScreenFrame, frame, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
            {
                return false;
            }
        });
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).Count(sent => sent);
    }

    private async Task SafeSendAsync<T>(DeviceConnection device, string type, T payload) where T : class
    {
        try
        {
            await device.Channel.SendAsync(type, payload, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            _logger.LogDebug(ex, "Could not deliver {Type} to {Device}.", type, device.DeviceId);
        }
    }

    /// <summary>Test hook: replays the last frame written to a device (requires <see cref="ServerFaultInjection.RecordFrames"/>).</summary>
    public Task ReplayLastFrameAsync(string deviceId) =>
        _online.TryGetValue(deviceId, out var device) && device.Recorder is { } recorder
            ? recorder.ReplayLastAsync(_cts.Token)
            : throw new InvalidOperationException("No recorded connection.");

    public void DropConnection(string deviceId)
    {
        if (_online.TryGetValue(deviceId, out var device)) device.Disconnect("Dropped by teacher");
    }

    // ------------------------------------------------------------------ audit & helpers

    private void Audit(string action, string computer, string result, string details, string? actor = null)
    {
        var entry = new LogEntry(0, _time.GetUtcNow(), actor ?? _actor, computer, action, result, details);
        try
        {
            Store.AddLog(entry.Timestamp, entry.Teacher, entry.Computer, entry.Action, entry.Result, entry.Details);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _logger.LogError(ex, "Audit entry could not be stored.");
        }
        _logger.LogInformation("{Action} | {Computer} | {Result} | {Details}", action, computer, result, details);
        LogAdded?.Invoke(this, entry);
    }

    private async Task SendResultAsync(IFramedChannel channel, bool success, string? code, string? message, string? sessionId, RegistrationState registration, AgentPolicy? policy, CancellationToken ct) =>
        await WriteHandshakeAsync(channel, MessageTypes.AuthResult, new AuthResultMessage(success, code, message, sessionId, registration, policy, string.Empty), ct).ConfigureAwait(false);

    private static Task WriteHandshakeAsync<T>(IFramedChannel channel, string type, T payload, CancellationToken ct) where T : class =>
        channel.WriteFrameAsync(MessageSerializer.Serialize(new WireMessage
        {
            ProtocolVersion = ProtocolConstants.Current.ToString(),
            Type = type,
            MessageId = Guid.NewGuid().ToString("N"),
            Payload = MessageSerializer.Serialize(payload),
        }), ct);

    private static string Clip(string? value, int max) => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch).ToArray());
        return cleaned.Length == 0 ? "x" : cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }
}

internal static class OptionsExtensions
{
    public static TimeSpan MaxClockSkewSecondsSpan(this ClassroomServerOptions o) => TimeSpan.FromSeconds(o.MaxClockSkewSeconds);
}

/// <summary>Slows down brute-force attempts on the classroom code and floods of discovery requests.</summary>
internal sealed class FailureThrottle
{
    private const int MaxFailures = 10;
    private const int MaxDiscoveryPerSecond = 50;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, (int Count, DateTimeOffset Since)> _failures = [];
    private readonly Dictionary<IPAddress, (int Count, DateTimeOffset Second)> _discovery = [];

    public FailureThrottle(TimeProvider time) => _time = time;

    public bool AllowConnection(IPAddress address)
    {
        lock (_gate) return !_failures.TryGetValue(address, out var f) || f.Count < MaxFailures || _time.GetUtcNow() - f.Since > Window;
    }

    public void RecordFailure(IPAddress address)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _failures[address] = _failures.TryGetValue(address, out var f) && now - f.Since <= Window ? (f.Count + 1, f.Since) : (1, now);
        }
    }

    public bool AllowDiscovery(IPAddress address)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_discovery.TryGetValue(address, out var d) && now - d.Second < TimeSpan.FromSeconds(1))
            {
                _discovery[address] = (d.Count + 1, d.Second);
                return d.Count + 1 <= MaxDiscoveryPerSecond;
            }
            _discovery[address] = (1, now);
            return true;
        }
    }
}
