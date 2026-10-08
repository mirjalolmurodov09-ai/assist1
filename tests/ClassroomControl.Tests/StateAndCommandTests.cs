using System.Text.Json;
using ClassroomControl.StudentAgent.Commands;
using ClassroomControl.Shared.Communication.Messages;
using ClassroomControl.Shared.Communication.Protocol;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.Infrastructure.Logging;
using ClassroomControl.StudentAgent.Models;
using ClassroomControl.StudentAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClassroomControl.StudentAgent.Tests;

public sealed class ConnectionStateMachineTests
{
    [Fact]
    public void Main_path_is_allowed()
    {
        var m = new ConnectionStateMachine();
        foreach (var s in new[]
        {
            ConnectionState.Discovering, ConnectionState.TeacherFound, ConnectionState.Connecting, ConnectionState.Authenticating,
            ConnectionState.WaitingForApproval, ConnectionState.Connected, ConnectionState.Reconnecting, ConnectionState.Disconnected,
        })
        {
            m.Transition(s);
            Assert.Equal(s, m.State);
        }
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Connected)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Connecting)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Reconnecting)]
    [InlineData(ConnectionState.Discovering, ConnectionState.Connected)]
    [InlineData(ConnectionState.TeacherFound, ConnectionState.Authenticating)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Connected)]
    [InlineData(ConnectionState.Connected, ConnectionState.Discovering)]
    [InlineData(ConnectionState.Reconnecting, ConnectionState.Connected)]
    [InlineData(ConnectionState.Reconnecting, ConnectionState.Authenticating)]
    public void Invalid_transitions_throw(ConnectionState from, ConnectionState to)
    {
        Assert.False(ConnectionStateMachine.CanTransition(from, to));
        var m = new ConnectionStateMachine();
        Drive(m, from);
        Assert.Throws<InvalidStateTransitionException>(() => m.Transition(to));
        Assert.Equal(from, m.State);
        Assert.False(m.TryTransition(to));
    }

    [Fact]
    public void Every_state_can_fall_back_to_disconnected()
    {
        foreach (var s in Enum.GetValues<ConnectionState>())
            Assert.True(s == ConnectionState.Disconnected || ConnectionStateMachine.CanTransition(s, ConnectionState.Disconnected));
    }

    private static void Drive(ConnectionStateMachine m, ConnectionState target)
    {
        var path = new Dictionary<ConnectionState, ConnectionState[]>
        {
            [ConnectionState.Disconnected] = [],
            [ConnectionState.Discovering] = [ConnectionState.Discovering],
            [ConnectionState.TeacherFound] = [ConnectionState.Discovering, ConnectionState.TeacherFound],
            [ConnectionState.Connecting] = [ConnectionState.Discovering, ConnectionState.TeacherFound, ConnectionState.Connecting],
            [ConnectionState.Connected] = [ConnectionState.Discovering, ConnectionState.TeacherFound, ConnectionState.Connecting, ConnectionState.Authenticating, ConnectionState.Connected],
            [ConnectionState.Reconnecting] = [ConnectionState.Discovering, ConnectionState.Reconnecting],
        };
        foreach (var s in path[target]) m.Transition(s);
    }

    [Theory]
    [InlineData(ConnectionState.Connected, StatusIndicator.Connected)]
    [InlineData(ConnectionState.Disconnected, StatusIndicator.Offline)]
    [InlineData(ConnectionState.Reconnecting, StatusIndicator.Offline)]
    [InlineData(ConnectionState.Discovering, StatusIndicator.Connecting)]
    [InlineData(ConnectionState.Authenticating, StatusIndicator.Connecting)]
    [InlineData(ConnectionState.WaitingForApproval, StatusIndicator.Connecting)]
    public void Indicator_follows_state(ConnectionState state, StatusIndicator expected) =>
        Assert.Equal(expected, AgentStatusSnapshot.Initial(DateTimeOffset.UtcNow).With(state).Indicator);
}

internal static class SnapshotExtensions
{
    public static AgentStatusSnapshot With(this AgentStatusSnapshot s, ConnectionState state) => s with { State = state };
}

public sealed class ReconnectTests
{
    [Fact]
    public void Delays_follow_the_schedule_and_cap_at_30_seconds()
    {
        var svc = new ReconnectService(Options.Create(new AgentOptions()), TimeProvider.System);
        var delays = Enumerable.Range(0, 9).Select(_ => (int)svc.NextDelay().TotalSeconds).ToArray();
        Assert.Equal([1, 2, 5, 10, 20, 30, 30, 30, 30], delays);
        Assert.Equal(9, svc.Attempt);
    }

    [Fact]
    public void Reset_restarts_the_schedule()
    {
        var svc = new ReconnectService(Options.Create(new AgentOptions()), TimeProvider.System);
        svc.NextDelay();
        svc.NextDelay();
        svc.Reset();
        Assert.Equal(0, svc.Attempt);
        Assert.Equal(1, svc.NextDelay().TotalSeconds);
    }

    [Fact]
    public async Task Waiting_is_cancellable()
    {
        var svc = new ReconnectService(Options.Create(new AgentOptions()), TimeProvider.System);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.WaitAsync(TimeSpan.FromMinutes(5), cts.Token));
    }
}

public sealed class CommandTests
{
    private static CommandDispatcher Dispatcher(params string[] enabled)
    {
        var time = TimeProvider.System;
        var store = new AgentStatusStore(time);
        var device = new FakeDevice();
        var handlers = new List<ICommandHandler>
        {
            new PingCommandHandler(time), new GetStatusCommandHandler(store, device, time), new GetDeviceInfoCommandHandler(device, store),
            new ThrowingHandler(),
        };
        handlers.AddRange(CommandNames.Reserved.Select(n => new ReservedCommandHandler(n)));
        return new CommandDispatcher(handlers, Options.Create(new AgentOptions
        {
            EnabledCommands = enabled.Length == 0 ? [CommandNames.Ping, CommandNames.GetStatus, CommandNames.GetDeviceInfo, "Boom"] : enabled,
        }), NullLogger<CommandDispatcher>.Instance);
    }

    private static readonly SessionContext Approved = new("s1", "dev-1", RegistrationState.Approved);

    [Fact]
    public async Task Ping_succeeds_with_a_full_response()
    {
        var r = await Dispatcher().DispatchAsync(new CommandRequest("123", "Ping", null), Approved, default);
        Assert.Equal(CommandStatus.Success, r.Status);
        Assert.Equal("123", r.CommandId);
        Assert.Equal("dev-1", r.DeviceId);
        Assert.Null(r.ErrorCode);
        Assert.True(r.Payload!.Value.GetProperty("pong").GetBoolean());
    }

    [Fact]
    public async Task GetStatus_reports_state()
    {
        var r = await Dispatcher().DispatchAsync(new CommandRequest("1", "GetStatus", null), Approved, default);
        Assert.Equal(CommandStatus.Success, r.Status);
        Assert.Equal("Status retrieved", r.Message);
        Assert.Equal("Disconnected", r.Payload!.Value.GetProperty("state").GetString());
        Assert.Equal("1.0", r.Payload!.Value.GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task GetDeviceInfo_returns_device_details()
    {
        var r = await Dispatcher().DispatchAsync(new CommandRequest("1", "GetDeviceInfo", null), Approved, default);
        Assert.Equal(CommandStatus.Success, r.Status);
        Assert.Equal("PC-01", r.Payload!.Value.GetProperty("computerName").GetString());
        Assert.Equal("Disconnected", r.Payload!.Value.GetProperty("agentStatus").GetString());
    }

    [Theory]
    [InlineData(RegistrationState.Pending)]
    [InlineData(RegistrationState.NotRegistered)]
    [InlineData(RegistrationState.Rejected)]
    public async Task Commands_are_refused_until_the_teacher_approved_the_device(RegistrationState state)
    {
        var r = await Dispatcher().DispatchAsync(new CommandRequest("1", "Ping", null), Approved with { Registration = state }, default);
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.Equal(ErrorCodes.NotRegistered, r.ErrorCode);
    }

    [Fact]
    public async Task Unknown_command_is_rejected()
    {
        var r = await Dispatcher().DispatchAsync(new CommandRequest("1", "FormatDisk", null), Approved, default);
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.Equal(ErrorCodes.UnknownCommand, r.ErrorCode);
    }

    [Theory]
    [InlineData(CommandNames.Lock)]
    [InlineData(CommandNames.Unlock)]
    [InlineData(CommandNames.Screenshot)]
    [InlineData(CommandNames.StartApplication)]
    [InlineData(CommandNames.StopApplication)]
    [InlineData(CommandNames.Restart)]
    [InlineData(CommandNames.Shutdown)]
    [InlineData(CommandNames.StartScreenStream)]
    [InlineData(CommandNames.StopScreenStream)]
    [InlineData(CommandNames.StartRemoteControl)]
    [InlineData(CommandNames.StopRemoteControl)]
    public async Task Reserved_commands_never_execute(string name)
    {
        var r = await Dispatcher(CommandNames.Ping, name).DispatchAsync(new CommandRequest("1", name, null), Approved, default);
        Assert.Equal(CommandStatus.NotImplemented, r.Status);
        Assert.Equal(ErrorCodes.CommandNotImplemented, r.ErrorCode);
    }

    [Fact]
    public async Task Implemented_command_that_is_not_enabled_is_rejected()
    {
        var r = await Dispatcher(CommandNames.Ping).DispatchAsync(new CommandRequest("1", "GetStatus", null), Approved, default);
        Assert.Equal(ErrorCodes.CommandDisabled, r.ErrorCode);
    }

    [Theory]
    [InlineData("", "Ping")]
    [InlineData("1", "")]
    [InlineData("bad id with spaces", "Ping")]
    [InlineData("1", "Ping; drop")]
    public async Task Malformed_identifiers_are_rejected(string id, string name)
    {
        var r = await Dispatcher().DispatchAsync(new CommandRequest(id, name, null), Approved, default);
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.Equal(ErrorCodes.InvalidMessage, r.ErrorCode);
    }

    [Fact]
    public async Task Handler_exception_is_contained_and_not_leaked()
    {
        var r = await Dispatcher().DispatchAsync(new CommandRequest("1", "Boom", null), Approved, default);
        Assert.Equal(CommandStatus.Failed, r.Status);
        Assert.Equal(ErrorCodes.CommandFailed, r.ErrorCode);
        Assert.DoesNotContain("secret-internal-detail", r.Message, StringComparison.Ordinal);
    }

    private sealed class ThrowingHandler : ICommandHandler
    {
        public string Name => "Boom";
        public bool IsImplemented => true;
        public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("secret-internal-detail");
    }

    private sealed class FakeDevice : IDeviceInfoService
    {
        public string DeviceId => "dev-1";
        public string ComputerName => "PC-01";
        public string LocalIp => "192.168.1.101";
        public string AgentVersion => "1.0.0";
        public DeviceInfo GetDeviceInfo() => new(DeviceId, ComputerName, "student", LocalIp, "AA-BB", "Windows", "10.0", "CPU", 1, "1.0.0", "Unknown", null);
    }
}

public sealed class RedactorTests
{
    [Theory]
    [InlineData("password=hunter2")]
    [InlineData("Token: abc123")]
    [InlineData("login failed, secret = \"top secret\"")]
    [InlineData("private key: MIIEvQ")]
    [InlineData("classroomCode=CLASS-8F4K-2026")]
    [InlineData("code CLASS-8F4K-2026 was entered")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----")]
    public void Secrets_are_masked(string text)
    {
        var redacted = SensitiveDataRedactor.Redact(text);
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("top secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("MIIEvQ", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("8F4K", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAA", redacted, StringComparison.Ordinal);
        Assert.Contains(SensitiveDataRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_text_is_untouched() =>
        Assert.Equal("Teacher Found at 192.168.1.10:39501", SensitiveDataRedactor.Redact("Teacher Found at 192.168.1.10:39501"));

    [Fact]
    public void File_logger_writes_redacted_lines()
    {
        var dir = TestSupport.TempDir();
        using (var provider = new FileLoggerProvider(dir))
        {
            var logger = provider.CreateLogger("Test.Category");
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "Agent Started");
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "token=abc123 leaked?");
        }
        var text = string.Concat(Directory.GetFiles(dir, "agent-*.log").Select(File.ReadAllText));
        Assert.Contains("Agent Started", text, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", text, StringComparison.Ordinal);
        Directory.Delete(dir, true);
    }
}

public sealed class StartupServiceTests
{
    private sealed class FakeStore : IRunKeyStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string command) => Values[name] = command;
        public void Remove(string name) => Values.Remove(name);
    }

    private sealed class FakePath : IExecutablePathProvider
    {
        public string Path => @"C:\Program Files\ClassroomControl\ClassroomControl.StudentAgent.exe";
    }

    [Fact]
    public void Enable_and_disable_toggle_the_run_entry()
    {
        var store = new FakeStore();
        var svc = new StartupService(store, new FakePath());
        Assert.False(svc.IsEnabled);
        svc.SetEnabled(true);
        Assert.True(svc.IsEnabled);
        Assert.Equal("\"C:\\Program Files\\ClassroomControl\\ClassroomControl.StudentAgent.exe\" --minimized", store.Values[StartupService.RunValueName]);
        svc.SetEnabled(false);
        Assert.False(svc.IsEnabled);
    }
}
