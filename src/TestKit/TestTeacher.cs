using ClassroomControl.ClassroomServer;
using ClassroomControl.ClassroomServer.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassroomControl.TestKit;

public enum ApprovalMode { Auto, Manual, Reject }

public sealed class TestTeacherOptions
{
    public string ClassroomCode { get; set; } = string.Empty;
    public string DataDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "cc-teacher-" + Guid.NewGuid().ToString("N"));
    public int TcpPort { get; set; }
    public int DiscoveryPort { get; set; }
    public ApprovalMode Approval { get; set; } = ApprovalMode.Auto;
    public string? ProtocolVersion { get; set; }
    public TimeSpan ChallengeTimeOffset { get; set; }
    public bool AckHeartbeats { get; set; } = true;
    public bool RecordFrames { get; set; }
    public bool RequireAgentActive { get; set; }
    public int AutoUnlockSeconds { get; set; } = 30;
    public int HeartbeatTimeoutSeconds { get; set; } = 15;
}

public sealed class TestDeviceView
{
    private readonly TestTeacher _owner;
    private readonly string _deviceId;

    public TestDeviceView(TestTeacher owner, string deviceId)
    {
        _owner = owner;
        _deviceId = deviceId;
    }

    public DeviceSnapshot Snapshot => _owner.Server.GetDevice(_deviceId)!;
    public string ComputerName => Snapshot.Computer.ComputerName;
    public string StudentName => Snapshot.Computer.StudentName;
    public string LocalIp => Snapshot.Computer.IpAddress;
    public bool IsConnected => _owner.Server.IsOnline(_deviceId);
    public int HeartbeatCount => (int)_owner.Server.GetHeartbeatStats(_deviceId).Count;
    public string LastHeartbeatStatus => _owner.Server.GetHeartbeatStats(_deviceId).LastStatus;
    public int ConnectionCount => _owner.Server.Store.CountSessions(_deviceId);
}

/// <summary>A real Local Classroom Server (TLS + UDP discovery + SQLite) on the loopback interface, for tests and the simulator.</summary>
public sealed class TestTeacher : IAsyncDisposable
{
    public TestTeacher(TestTeacherOptions options)
    {
        Options = options;
        Approval = options.Approval;
        var serverOptions = new ClassroomServerOptions
        {
            DataDirectory = options.DataDirectory,
            BindAddress = System.Net.IPAddress.Loopback,
            AllowLoopbackClients = true,
            TcpPort = options.TcpPort,
            DiscoveryPort = options.DiscoveryPort,
            TeacherName = "Teacher PC",
            ClassroomName = "8-A",
            HeartbeatTimeoutSeconds = options.HeartbeatTimeoutSeconds,
            Faults = new ServerFaultInjection
            {
                ProtocolVersion = options.ProtocolVersion,
                ChallengeTimeOffset = options.ChallengeTimeOffset,
                AckHeartbeats = options.AckHeartbeats,
                RecordFrames = options.RecordFrames,
            },
        };
        Directory.CreateDirectory(options.DataDirectory);
        var store = new ClassroomStore(serverOptions.DatabasePath);
        var protector = new AesFileSecretProtector(Path.Combine(options.DataDirectory, "secret.key"));
        Server = new ClassroomServer.ClassroomServer(serverOptions, store, protector, NullLogger<ClassroomServer.ClassroomServer>.Instance);
        if (!string.IsNullOrEmpty(options.ClassroomCode)) Server.SetClassroomCode(options.ClassroomCode);
        Server.Settings.RequireAgentActive = options.RequireAgentActive;
        Server.Settings.AutoUnlockSeconds = options.AutoUnlockSeconds;
        Server.DeviceChanged += OnDeviceChanged;
    }

    public TestTeacherOptions Options { get; }
    public ClassroomServer.ClassroomServer Server { get; }
    public ApprovalMode Approval { get; set; }
    public string DataDirectory => Options.DataDirectory;
    public int TcpPort => Server.TcpPort;
    public int DiscoveryPort => Server.DiscoveryPort;
    public string CertificateFingerprintHex => Server.CertificateFingerprint;
    public int DiscoveryRequestsAnswered => Server.DiscoveryRequestsAnswered;
    public IReadOnlyCollection<TestDeviceView> Devices => [.. Server.Devices.Select(d => new TestDeviceView(this, d.DeviceId))];
    public TestDeviceView? GetDevice(string deviceId) => Server.GetDevice(deviceId) is null ? null : new TestDeviceView(this, deviceId);

    public void Start() => Server.Start();
    public Task StopAsync() => Server.StopAsync();
    public Task ApproveAsync(string deviceId) => Server.ApproveAsync(deviceId);
    public Task RejectAsync(string deviceId) => Server.RejectAsync(deviceId);
    public void DropConnection(string deviceId) => Server.DropConnection(deviceId);
    public Task ReplayLastFrameAsync(string deviceId) => Server.ReplayLastFrameAsync(deviceId);

    /// <summary>Runs a command and always returns a <see cref="CommandResult"/> (a Failed one when the server could not deliver it).</summary>
    public async Task<CommandResult> SendCommandAsync(string deviceId, string name, TimeSpan? timeout = null, System.Text.Json.JsonElement? parameters = null)
    {
        var outcome = await Server.ExecuteAsync(deviceId, name, parameters, timeout).ConfigureAwait(false);
        return outcome.Result ?? new CommandResult("none", deviceId, CommandStatus.Failed, DateTimeOffset.UtcNow, outcome.ErrorCode, outcome.Message, null);
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

    private void OnDeviceChanged(object? sender, DeviceSnapshot snapshot)
    {
        if (snapshot.Computer.Status != RegistrationState.Pending || !snapshot.Online) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (Approval == ApprovalMode.Auto) await Server.ApproveAsync(snapshot.DeviceId).ConfigureAwait(false);
                else if (Approval == ApprovalMode.Reject) await Server.RejectAsync(snapshot.DeviceId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
            {
                // The device left before it could be approved.
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync().ConfigureAwait(false);
        try { Directory.Delete(Options.DataDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp data */ }
    }
}
