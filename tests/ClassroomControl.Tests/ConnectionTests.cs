using ClassroomControl.Shared.Communication.Protocol;
using ClassroomControl.StudentAgent.Models;
using ClassroomControl.TestKit;
using Xunit;

namespace ClassroomControl.StudentAgent.Tests;

/// <summary>Real TCP+TLS+UDP on the loopback interface against the mock Teacher.</summary>
public sealed class ConnectionTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _code = TestSupport.NewCode();
    private readonly List<IAsyncDisposable> _disposables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var d in Enumerable.Reverse(_disposables)) await d.DisposeAsync();
    }

    private MockTeacherServer Teacher(Action<MockTeacherOptions>? configure = null)
    {
        var options = new MockTeacherOptions { ClassroomCode = _code };
        configure?.Invoke(options);
        var server = new MockTeacherServer(options);
        server.Start();
        _disposables.Add(server);
        return server;
    }

    private HeadlessAgent Agent(MockTeacherServer teacher, bool manual, string? code = null, Action<HeadlessAgentOptions>? configure = null)
    {
        var options = new HeadlessAgentOptions
        {
            ClassroomCode = code ?? _code,
            StudentName = "Ali",
            ComputerName = "PC-01",
            DiscoveryPort = teacher.DiscoveryPort,
            TeacherAddress = manual ? "127.0.0.1" : string.Empty,
            TeacherPort = manual ? teacher.TcpPort : 0,
        };
        configure?.Invoke(options);
        var agent = new HeadlessAgent(options);
        _disposables.Add(agent);
        return agent;
    }

    [Fact]
    public async Task Connects_to_a_configured_teacher_address()
    {
        var teacher = Teacher();
        var agent = Agent(teacher, manual: true);
        await agent.StartAsync();

        Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait), agent.Status.Current.StatusText);
        Assert.Equal(RegistrationState.Approved, agent.Status.Current.Registration);
        Assert.Equal("Teacher PC", agent.Status.Current.TeacherName);
        Assert.Equal("8-A", agent.Status.Current.ClassroomName);
        Assert.NotNull(teacher.GetDevice(agent.DeviceId));
    }

    [Fact]
    public async Task Discovers_the_teacher_by_udp_and_connects()
    {
        var teacher = Teacher();
        var agent = Agent(teacher, manual: false);
        await agent.StartAsync();

        Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait), agent.Status.Current.StatusText);
        Assert.True(teacher.DiscoveryRequestsAnswered >= 1);
    }

    [Fact]
    public async Task Wrong_classroom_code_never_connects_and_reports_the_code_error()
    {
        var teacher = Teacher();
        var agent = Agent(teacher, manual: true, code: "CLASS-ZZZZ-9999");
        await agent.StartAsync();

        Assert.True(await teacher.WaitForAsync(() => agent.Status.Current.LastErrorCode == ErrorCodes.InvalidClassroomCode, Wait));
        Assert.Equal("Classroom code noto‘g‘ri.", agent.Status.Current.LastErrorMessage);
        Assert.NotEqual(ConnectionState.Connected, agent.Status.Current.State);
        Assert.Null(teacher.GetDevice(agent.DeviceId)?.SessionId);
    }

    [Fact]
    public async Task Wrong_classroom_code_is_detected_through_discovery_too()
    {
        var teacher = Teacher();
        var agent = Agent(teacher, manual: false, code: "CLASS-ZZZZ-9999");
        await agent.StartAsync();

        Assert.True(await teacher.WaitForAsync(() => agent.Status.Current.LastErrorCode == ErrorCodes.InvalidClassroomCode, Wait));
        Assert.Empty(teacher.Devices);
    }

    [Fact]
    public async Task Waits_for_teacher_approval_then_becomes_registered()
    {
        var teacher = Teacher(o => o.Approval = ApprovalMode.Manual);
        var agent = Agent(teacher, manual: true);
        await agent.StartAsync();

        Assert.True(await agent.WaitForStateAsync(ConnectionState.WaitingForApproval, Wait));
        Assert.Equal(RegistrationState.Pending, agent.Status.Current.Registration);

        await teacher.ApproveAsync(agent.DeviceId);
        Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait));
        Assert.Equal(RegistrationState.Approved, agent.Status.Current.Registration);
    }

    [Fact]
    public async Task Rejected_device_stops_retrying_until_the_user_connects_again()
    {
        var teacher = Teacher(o => o.Approval = ApprovalMode.Reject);
        var agent = Agent(teacher, manual: true);
        await agent.StartAsync();

        Assert.True(await teacher.WaitForAsync(() => agent.Status.Current.LastErrorCode == ErrorCodes.Rejected, Wait));
        Assert.True(await agent.WaitForStateAsync(ConnectionState.Disconnected, Wait));
        Assert.NotEqual(RegistrationState.Approved, agent.Status.Current.Registration);
    }

    [Fact]
    public async Task Teacher_going_away_causes_offline_and_it_reconnects_when_back()
    {
        var teacher = Teacher();
        var agent = Agent(teacher, manual: true);
        await agent.StartAsync();
        Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait));

        var port = teacher.TcpPort;
        await teacher.StopAsync();
        Assert.True(await teacher.WaitForAsync(() => agent.Status.Current.Indicator == StatusIndicator.Offline, Wait));
        Assert.False(string.IsNullOrEmpty(agent.Status.Current.LastErrorCode));

        var restarted = new MockTeacherServer(new MockTeacherOptions { ClassroomCode = _code, TcpPort = port, DiscoveryPort = teacher.DiscoveryPort, Certificate = teacher.Certificate });
        restarted.Start();
        _disposables.Add(restarted);
        Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait), agent.Status.Current.StatusText);
        Assert.NotNull(restarted.GetDevice(agent.DeviceId));
    }

    [Fact]
    public async Task Changed_teacher_certificate_is_refused_after_pinning()
    {
        var teacher = Teacher();
        var agent = Agent(teacher, manual: true);
        await agent.StartAsync();
        Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait));
        Assert.Equal(teacher.CertificateFingerprintHex, agent.Settings.Current.PinnedTeacherCertificate);

        var port = teacher.TcpPort;
        await teacher.StopAsync();
        var impostor = new MockTeacherServer(new MockTeacherOptions { ClassroomCode = _code, TcpPort = port, DiscoveryPort = teacher.DiscoveryPort });
        impostor.Start();
        _disposables.Add(impostor);

        Assert.True(await impostor.WaitForAsync(() => agent.Status.Current.LastErrorCode == ErrorCodes.UntrustedCertificate, Wait));
        Assert.NotEqual(ConnectionState.Connected, agent.Status.Current.State);
        Assert.Empty(impostor.Devices);
    }

    [Fact]
    public async Task Incompatible_teacher_protocol_gives_a_clear_message()
    {
        var teacher = Teacher(o => o.ProtocolVersion = "2.0");
        var agent = Agent(teacher, manual: true);
        await agent.StartAsync();

        Assert.True(await teacher.WaitForAsync(() => agent.Status.Current.LastErrorCode == ErrorCodes.VersionMismatch, Wait));
        Assert.Contains("2.0", agent.Status.Current.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_challenge_from_a_teacher_with_a_bad_clock_is_refused()
    {
        var teacher = Teacher(o => o.ChallengeTimeOffset = TimeSpan.FromMinutes(-30));
        var agent = Agent(teacher, manual: true);
        await agent.StartAsync();
        Assert.True(await teacher.WaitForAsync(() => agent.Status.Current.LastErrorCode == ErrorCodes.ExpiredChallenge, Wait));
    }

    [Fact]
    public async Task Agent_without_a_classroom_code_stays_disconnected_and_does_not_search()
    {
        var teacher = Teacher();
        var agent = Agent(teacher, manual: false, code: string.Empty);
        await agent.StartAsync();
        await Task.Delay(1000);

        Assert.Equal(ConnectionState.Disconnected, agent.Status.Current.State);
        Assert.Equal(0, teacher.DiscoveryRequestsAnswered);
    }

    [Fact]
    public async Task Setting_the_code_later_starts_the_connection()
    {
        var teacher = Teacher();
        var agent = Agent(teacher, manual: true, code: string.Empty);
        await agent.StartAsync();
        Assert.Equal(ConnectionState.Disconnected, agent.Status.Current.State);

        var s = agent.Settings.Current;
        s.ClassroomCode = _code;
        await agent.Settings.SaveAsync(s);
        agent.Agent.NotifySettingsChanged();
        Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait));
    }

    [Fact]
    public async Task Public_teacher_address_is_never_contacted()
    {
        var connection = new ClassroomControl.StudentAgent.Services.ConnectionService(
            Microsoft.Extensions.Options.Options.Create(new ClassroomControl.StudentAgent.Infrastructure.Helpers.AgentOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClassroomControl.StudentAgent.Services.ConnectionService>.Instance);
        var publicTeacher = new ConnectionInfo(System.Net.IPAddress.Parse("8.8.8.8"), 39501, "t", "Teacher", "8-A", TeacherSource.Manual);

        var ex = await Assert.ThrowsAsync<ProtocolException>(() => connection.ConnectAsync(publicTeacher, new StudentSettings(), default));
        Assert.Equal(ErrorCodes.UntrustedNetwork, ex.ErrorCode);
    }

    [Fact]
    public async Task Stopping_the_agent_closes_everything_quickly()
    {
        var teacher = Teacher();
        var agent = Agent(teacher, manual: true);
        await agent.StartAsync();
        Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await agent.StopAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Stop took {watch.Elapsed}");
        Assert.Equal(ConnectionState.Disconnected, agent.Status.Current.State);
        Assert.True(await teacher.WaitForAsync(() => !teacher.GetDevice(agent.DeviceId)!.IsConnected, Wait));
    }
}
