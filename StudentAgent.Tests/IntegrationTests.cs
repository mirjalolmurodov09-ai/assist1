using System.Text.Json;
using ClassroomControl.StudentAgent.Commands;
using ClassroomControl.StudentAgent.Communication.Protocol;
using ClassroomControl.StudentAgent.Models;
using ClassroomControl.StudentAgent.Services;
using ClassroomControl.StudentAgent.TeacherMock;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClassroomControl.StudentAgent.Tests;

/// <summary>End-to-end: the real agent services against a real TLS/UDP Teacher implementation (mock).</summary>
public sealed class IntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly string _code = TestSupport.NewCode();
    private readonly List<IAsyncDisposable> _disposables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var d in Enumerable.Reverse(_disposables)) await d.DisposeAsync();
    }

    private MockTeacherServer StartTeacher(Action<MockTeacherOptions>? configure = null)
    {
        var options = new MockTeacherOptions { ClassroomCode = _code };
        configure?.Invoke(options);
        var server = new MockTeacherServer(options);
        server.Start();
        _disposables.Add(server);
        return server;
    }

    private HeadlessAgent NewAgent(MockTeacherServer teacher, Action<HeadlessAgentOptions>? configure = null)
    {
        var options = new HeadlessAgentOptions { ClassroomCode = _code, ComputerName = "PC-01", StudentName = "Ali", DiscoveryPort = teacher.DiscoveryPort };
        configure?.Invoke(options);
        var agent = new HeadlessAgent(options);
        _disposables.Add(agent);
        return agent;
    }

    [Fact]
    public async Task Full_classroom_scenario()
    {
        var teacher = StartTeacher(o => o.Approval = ApprovalMode.Manual);
        var agent = NewAgent(teacher, o => o.SimulatedIp = "192.168.1.101");
        await agent.StartAsync();

        // 1-3. discovery, TLS connection, authentication
        Assert.True(await agent.WaitForStateAsync(ConnectionState.WaitingForApproval, Wait), agent.Status.Current.StatusText);
        Assert.True(teacher.DiscoveryRequestsAnswered >= 1);
        var device = teacher.GetDevice(agent.DeviceId)!;
        Assert.Equal("PC-01", device.ComputerName);
        Assert.Equal("Ali", device.StudentName);
        Assert.Equal("192.168.1.101", device.LocalIp);
        Assert.Equal("1.0.0", device.AgentVersion);

        // 4. registration: commands are refused until approved
        var early = await teacher.SendCommandAsync(agent.DeviceId, CommandNames.Ping);
        Assert.Equal(CommandStatus.Rejected, early.Status);
        Assert.Equal(ErrorCodes.NotRegistered, early.ErrorCode);

        await teacher.ApproveAsync(agent.DeviceId);
        Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait));
        Assert.Equal(RegistrationState.Approved, agent.Status.Current.Registration);

        // 5. heartbeat
        Assert.True(await teacher.WaitForAsync(() => device.HeartbeatCount >= 3, Wait));
        Assert.Equal("Approved", device.LastHeartbeatStatus);

        // 6-8. commands
        var ping = await teacher.SendCommandAsync(agent.DeviceId, CommandNames.Ping);
        Assert.Equal(CommandStatus.Success, ping.Status);
        Assert.Equal(agent.DeviceId, ping.DeviceId);

        var status = await teacher.SendCommandAsync(agent.DeviceId, CommandNames.GetStatus);
        Assert.Equal(CommandStatus.Success, status.Status);
        Assert.Equal("Connected", status.Payload!.Value.GetProperty("state").GetString());
        Assert.Equal("Approved", status.Payload!.Value.GetProperty("registration").GetString());

        var info = await teacher.SendCommandAsync(agent.DeviceId, CommandNames.GetDeviceInfo);
        Assert.Equal(CommandStatus.Success, info.Status);
        Assert.Equal(agent.DeviceId, info.Payload!.Value.GetProperty("deviceId").GetString());
        Assert.Equal("PC-01", info.Payload!.Value.GetProperty("computerName").GetString());
        Assert.Equal("192.168.1.101", info.Payload!.Value.GetProperty("localIp").GetString());
        Assert.Equal("1.0.0", info.Payload!.Value.GetProperty("agentVersion").GetString());

        // reserved commands exist but never run
        var lockResult = await teacher.SendCommandAsync(agent.DeviceId, CommandNames.Lock);
        Assert.Equal(CommandStatus.NotImplemented, lockResult.Status);
        var unknown = await teacher.SendCommandAsync(agent.DeviceId, "FormatDisk");
        Assert.Equal(ErrorCodes.UnknownCommand, unknown.ErrorCode);

        // 9. disconnect (teacher drops the connection)
        teacher.DropConnection(agent.DeviceId);
        Assert.True(await teacher.WaitForAsync(() => agent.Status.Current.Indicator != StatusIndicator.Connected, Wait));

        // 10. reconnect, still registered (the Teacher remembers the approval)
        Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait));
        Assert.True(await teacher.WaitForAsync(() => device.ConnectionCount >= 2, Wait));
        Assert.Equal(CommandStatus.Success, (await teacher.SendCommandAsync(agent.DeviceId, CommandNames.Ping)).Status);
    }

    [Fact]
    public async Task Network_change_closes_the_connection_and_restarts_discovery()
    {
        var teacher = StartTeacher();
        var network = new FakeNetworkMonitor();
        var agent = NewAgent(teacher, o => o.ConfigureServices = s => s.AddSingleton<INetworkMonitor>(network));
        await agent.StartAsync();
        Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait));
        var answeredBefore = teacher.DiscoveryRequestsAnswered;
        var device = teacher.GetDevice(agent.DeviceId)!;

        network.Raise();

        Assert.True(await teacher.WaitForAsync(() => device.ConnectionCount >= 2 && agent.Status.Current.State == ConnectionState.Connected, Wait));
        Assert.True(teacher.DiscoveryRequestsAnswered > answeredBefore, "discovery must run again after a network change");
    }

    [Fact]
    public async Task Silent_teacher_triggers_heartbeat_timeout_and_reconnect()
    {
        var teacher = StartTeacher(o => o.AckHeartbeats = false);
        var agent = NewAgent(teacher, o => o.Configure = a => a.HeartbeatTimeoutSeconds = 2);
        await agent.StartAsync();
        Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait));
        var device = teacher.GetDevice(agent.DeviceId)!;

        Assert.True(await teacher.WaitForAsync(() => device.ConnectionCount >= 2, Wait), "agent should drop the silent connection and reconnect");
    }

    [Fact]
    public async Task Replay_attack_on_the_session_is_detected_and_the_session_is_dropped()
    {
        var teacher = StartTeacher();
        var agent = NewAgent(teacher);
        await agent.StartAsync();
        Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait));
        var device = teacher.GetDevice(agent.DeviceId)!;
        Assert.True(await teacher.WaitForAsync(() => device.HeartbeatCount >= 1, Wait));

        await teacher.ReplayLastFrameAsync(agent.DeviceId);

        Assert.True(await teacher.WaitForAsync(() => device.ConnectionCount >= 2, Wait), "a replayed frame must terminate the session");
    }

    [Fact]
    public async Task Sixteen_agents_in_a_simulated_classroom_all_connect_and_answer_commands()
    {
        var teacher = StartTeacher();
        var agents = Enumerable.Range(1, 16).Select(i => NewAgent(teacher, o =>
        {
            o.ComputerName = $"PC-{i:00}";
            o.SimulatedIp = $"192.168.1.{100 + i}";
            o.StudentName = $"Student {i}";
        })).ToList();

        foreach (var a in agents) await a.StartAsync();
        foreach (var a in agents) Assert.True(await a.WaitForStateAsync(ConnectionState.Connected, Wait), $"{a.Options.ComputerName}: {a.Status.Current.StatusText}");

        Assert.Equal(16, teacher.Devices.Count(d => d.IsConnected));
        Assert.Equal(16, teacher.Devices.Select(d => d.LocalIp).Distinct().Count());
        var pings = await Task.WhenAll(agents.Select(a => teacher.SendCommandAsync(a.DeviceId, CommandNames.Ping)));
        Assert.All(pings, p => Assert.Equal(CommandStatus.Success, p.Status));
        Assert.Equal(16, agents.Select(a => a.DeviceId).Distinct().Count());
    }

    [Fact]
    public async Task Commands_with_parameters_do_not_break_the_session()
    {
        var teacher = StartTeacher();
        var agent = NewAgent(teacher);
        await agent.StartAsync();
        Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait));

        var parameters = JsonSerializer.SerializeToElement(new { value = new string('x', 1000) });
        var result = await teacher.SendCommandAsync(agent.DeviceId, CommandNames.Ping, parameters: parameters);
        Assert.Equal(CommandStatus.Success, result.Status);
    }

    private sealed class FakeNetworkMonitor : INetworkMonitor
    {
        public event EventHandler? NetworkChanged;
        public void Start() { }
        public void Raise() => NetworkChanged?.Invoke(this, EventArgs.Empty);
        public void Dispose() { }
    }
}
