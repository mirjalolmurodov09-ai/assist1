using ClassroomControl.ClassroomServer.Data;
using ClassroomControl.ClassroomServer.Security;
using ClassroomControl.TestKit;
using Xunit;

namespace ClassroomControl.StudentAgent.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string _dir = TestSupport.TempDir();
    private readonly ClassroomStore _store;

    public StoreTests() => _store = new ClassroomStore(Path.Combine(_dir, "t.db"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void Migrations_create_every_required_table_and_are_idempotent()
    {
        Assert.Equal(1, _store.SchemaVersion);
        var again = new ClassroomStore(_store.DatabasePath); // opening an up-to-date database changes nothing
        Assert.Equal(1, again.SchemaVersion);

        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_store.DatabasePath}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var tables = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) tables.Add(r.GetString(0));
        foreach (var t in new[] { "Users", "Students", "Computers", "Classrooms", "Groups", "Sessions", "Commands", "Logs", "Screenshots", "Settings" })
            Assert.Contains(t, tables);
    }

    [Fact]
    public void Computers_roundtrip_and_keep_their_status_when_they_reconnect()
    {
        var classroom = _store.AddClassroom("8-A", "x", "t", DateTimeOffset.UtcNow);
        var id = Guid.NewGuid().ToString();
        var first = _store.UpsertComputer(id, classroom.Id, "PC-01", "Ali", "192.168.1.101", DateTimeOffset.UtcNow);
        Assert.Equal(RegistrationState.Pending, first.Status);

        _store.SetComputerStatus(id, RegistrationState.Approved);
        var again = _store.UpsertComputer(id, classroom.Id, "PC-01-renamed", "Vali", "192.168.1.150", DateTimeOffset.UtcNow);
        Assert.Equal(RegistrationState.Approved, again.Status);
        Assert.Equal("PC-01-renamed", again.ComputerName);
        Assert.Equal("192.168.1.150", again.IpAddress);
        Assert.Single(_store.ListComputers(classroom.Id));
    }

    [Fact]
    public void Deleting_a_group_unassigns_its_computers()
    {
        var classroom = _store.AddClassroom("8-A", "x", "t", DateTimeOffset.UtcNow);
        var group = _store.AddGroup(classroom.Id, "Python");
        var id = Guid.NewGuid().ToString();
        _store.UpsertComputer(id, classroom.Id, "PC-01", "", "", DateTimeOffset.UtcNow);
        _store.SetComputerGroup(id, group.Id);
        Assert.Equal(group.Id, _store.GetComputer(id)!.GroupId);

        _store.DeleteGroup(group.Id);
        Assert.Null(_store.GetComputer(id)!.GroupId);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => { _store.AddGroup(classroom.Id, "A"); _store.AddGroup(classroom.Id, "A"); });
    }

    [Fact]
    public void Logs_are_searchable_and_newest_first()
    {
        var t = DateTimeOffset.UtcNow;
        _store.AddLog(t, "ustoz", "PC-01", "Computer locked", "OK", "");
        _store.AddLog(t.AddSeconds(1), "ustoz", "PC-02", "Screenshot", "OK", "");
        var all = _store.QueryLogs(10);
        Assert.Equal("Screenshot", all[0].Action);
        Assert.Single(_store.QueryLogs(10, "locked"));
        Assert.Empty(_store.QueryLogs(10, "nonexistent"));
    }

    [Fact]
    public void Settings_screenshots_and_commands_are_stored()
    {
        _store.SetSetting("k", "v1");
        _store.SetSetting("k", "v2");
        Assert.Equal("v2", _store.GetSetting("k"));
        Assert.Null(_store.GetSetting("missing"));

        _store.AddScreenshot(null, "/tmp/a.jpg", DateTimeOffset.UtcNow, "Ali", "PC-01");
        Assert.Single(_store.ListScreenshots(10));

        _store.AddCommand("c1", "d1", "Ping", "ustoz", DateTimeOffset.UtcNow);
        _store.CompleteCommand("c1", "Success", null, DateTimeOffset.UtcNow);
        Assert.Equal("Success", _store.ListCommands(5)[0].Status);
    }
}

public sealed class UserTests : IDisposable
{
    private readonly string _dir = TestSupport.TempDir();
    private readonly ManualTimeProvider _time = new();
    private readonly ClassroomStore _store;
    private readonly UserService _users;

    public UserTests()
    {
        _store = new ClassroomStore(Path.Combine(_dir, "t.db"));
        _users = new UserService(_store, _time);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void Passwords_are_hashed_never_stored_in_plain_text()
    {
        var user = _users.Create("ustoz", "Sirli-Parol-1", Roles.Admin);
        Assert.DoesNotContain("Sirli-Parol-1", user.PasswordHash, StringComparison.Ordinal);
        Assert.StartsWith("pbkdf2-sha256$", user.PasswordHash, StringComparison.Ordinal);
        Assert.NotEqual(PasswordHasher.Hash("same"), PasswordHasher.Hash("same")); // unique salt
        Assert.True(PasswordHasher.Verify("same", PasswordHasher.Hash("same")));
        Assert.False(PasswordHasher.Verify("other", PasswordHasher.Hash("same")));
        Assert.False(PasswordHasher.Verify("x", "garbage"));
    }

    [Fact]
    public void Correct_credentials_log_in_and_wrong_ones_do_not()
    {
        _users.Create("ustoz", "Sirli-Parol-1", Roles.Teacher);
        Assert.NotNull(_users.Authenticate("USTOZ", "Sirli-Parol-1"));
        Assert.Null(_users.Authenticate("ustoz", "wrong"));
        Assert.Null(_users.Authenticate("nobody", "Sirli-Parol-1"));
        Assert.NotNull(_users.List().Single().LastLoginAt);
    }

    [Fact]
    public void Repeated_failures_lock_the_account_for_a_minute()
    {
        _users.Create("ustoz", "Sirli-Parol-1", Roles.Teacher);
        for (var i = 0; i < UserService.MaxFailedAttempts; i++) Assert.Null(_users.Authenticate("ustoz", "bad"));
        Assert.True(_users.IsLockedOut("ustoz"));
        Assert.Null(_users.Authenticate("ustoz", "Sirli-Parol-1")); // even the right password is refused while locked

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(_users.Authenticate("ustoz", "Sirli-Parol-1"));
    }

    [Theory]
    [InlineData("ab", "Sirli-Parol-1")]
    [InlineData("has space", "Sirli-Parol-1")]
    [InlineData("ustoz", "short")]
    public void Weak_input_is_rejected(string name, string password) =>
        Assert.Throws<UserException>(() => _users.Create(name, password, Roles.Teacher));

    [Fact]
    public void The_last_administrator_cannot_be_removed_or_disabled()
    {
        var admin = _users.Create("admin", "Sirli-Parol-1", Roles.Admin);
        Assert.Throws<UserException>(() => _users.Delete(admin.Id));
        Assert.Throws<UserException>(() => _users.SetDisabled(admin.Id, true));

        var second = _users.Create("admin2", "Sirli-Parol-2", Roles.Admin);
        _users.Delete(second.Id);
        Assert.Single(_users.List());
    }

    [Fact]
    public void Duplicate_names_are_rejected_and_disabled_users_cannot_log_in()
    {
        var t = _users.Create("ustoz", "Sirli-Parol-1", Roles.Teacher);
        Assert.Throws<UserException>(() => _users.Create("Ustoz", "Sirli-Parol-1", Roles.Teacher));
        _users.SetDisabled(t.Id, true);
        Assert.Null(_users.Authenticate("ustoz", "Sirli-Parol-1"));
    }
}

public sealed class ServerBehaviourTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly string _code = TestSupport.NewCode();
    private readonly List<IAsyncDisposable> _disposables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var d in Enumerable.Reverse(_disposables)) await d.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private TestTeacher StartTeacher(Action<TestTeacherOptions>? configure = null)
    {
        var options = new TestTeacherOptions { ClassroomCode = _code };
        configure?.Invoke(options);
        var teacher = new TestTeacher(options);
        teacher.Start();
        _disposables.Add(teacher);
        return teacher;
    }

    private HeadlessAgent NewAgent(TestTeacher teacher, string? code = null, string name = "PC-01")
    {
        var agent = new HeadlessAgent(new HeadlessAgentOptions
        {
            ClassroomCode = code ?? _code,
            ComputerName = name,
            StudentName = "Ali",
            DiscoveryPort = teacher.DiscoveryPort,
            TeacherAddress = "127.0.0.1",
            TeacherPort = teacher.TcpPort,
        });
        _disposables.Add(agent);
        return agent;
    }

    [Fact]
    public async Task Unknown_computer_stays_pending_and_cannot_be_commanded_or_stream()
    {
        var teacher = StartTeacher(o => o.Approval = ApprovalMode.Manual);
        var agent = NewAgent(teacher);
        await agent.StartAsync();
        Assert.True(await agent.WaitForStateAsync(Models.ConnectionState.WaitingForApproval, Wait));

        var snapshot = teacher.Server.GetDevice(agent.DeviceId)!;
        Assert.Equal(RegistrationState.Pending, snapshot.Computer.Status);
        Assert.True(snapshot.Online);
        var outcome = await teacher.Server.ExecuteAsync(agent.DeviceId, "Ping", null);
        Assert.False(outcome.Success);
        Assert.Equal(ErrorCodes.NotRegistered, outcome.ErrorCode);
    }

    [Fact]
    public async Task Approval_survives_a_teacher_restart()
    {
        var teacher = StartTeacher(o => o.Approval = ApprovalMode.Manual);
        var agent = NewAgent(teacher);
        await agent.StartAsync();
        Assert.True(await agent.WaitForStateAsync(Models.ConnectionState.WaitingForApproval, Wait));
        await teacher.ApproveAsync(agent.DeviceId);
        Assert.True(await agent.WaitForStateAsync(Models.ConnectionState.Connected, Wait));

        var port = teacher.TcpPort;
        await teacher.StopAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var restarted = new TestTeacher(new TestTeacherOptions
        {
            ClassroomCode = _code, DataDirectory = teacher.DataDirectory, TcpPort = port, DiscoveryPort = teacher.DiscoveryPort, Approval = ApprovalMode.Manual,
        });
        restarted.Start();
        _disposables.Add(restarted);

        // Same data directory => same database and certificate: the computer reconnects already approved, no new approval needed.
        Assert.True(await restarted.WaitForAsync(() => restarted.GetDevice(agent.DeviceId) is { IsConnected: true }, Wait));
        Assert.True(await agent.WaitForStateAsync(Models.ConnectionState.Connected, Wait));
        Assert.Equal(RegistrationState.Approved, restarted.Server.GetDevice(agent.DeviceId)!.Computer.Status);
    }

    [Fact]
    public async Task Removed_computer_must_be_approved_again()
    {
        var teacher = StartTeacher(o => o.Approval = ApprovalMode.Manual);
        var agent = NewAgent(teacher);
        await agent.StartAsync();
        Assert.True(await agent.WaitForStateAsync(Models.ConnectionState.WaitingForApproval, Wait));
        await teacher.ApproveAsync(agent.DeviceId);
        Assert.True(await agent.WaitForStateAsync(Models.ConnectionState.Connected, Wait));

        teacher.Server.Remove(agent.DeviceId);

        Assert.True(await agent.WaitForStateAsync(Models.ConnectionState.WaitingForApproval, Wait));
        Assert.Equal(RegistrationState.Pending, teacher.Server.GetDevice(agent.DeviceId)!.Computer.Status);
    }

    [Fact]
    public async Task Regenerating_the_classroom_code_disconnects_agents_that_still_use_the_old_one()
    {
        var teacher = StartTeacher();
        var agent = NewAgent(teacher);
        await agent.StartAsync();
        Assert.True(await agent.WaitForStateAsync(Models.ConnectionState.Connected, Wait));

        var newCode = teacher.Server.RegenerateClassroomCode();

        Assert.NotEqual(_code, newCode);
        Assert.True(ClassroomCode.IsValidFormat(newCode));
        Assert.True(await teacher.WaitForAsync(() => agent.Status.Current.LastErrorCode == ErrorCodes.InvalidClassroomCode, Wait));
    }

    /// <summary>A hostile client that knows the protocol but not the classroom code.</summary>
    private static async Task<AuthResultMessage?> AttackAsync(int port)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port);
        var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(ProtocolConstants.TlsTargetName);
        var channel = new FramedConnection(ssl);
        var deviceId = Guid.NewGuid().ToString();
        var nonce = HandshakeCrypto.NewNonce();
        async Task Send(string type, object payload) => await channel.WriteFrameAsync(MessageSerializer.Serialize(new WireMessage
        {
            ProtocolVersion = "1.0", Type = type, MessageId = Guid.NewGuid().ToString("N"), Payload = MessageSerializer.Serialize(payload),
        }), default);

        await Send(MessageTypes.Hello, new HelloMessage(deviceId, "1.0.0", "1.0", "EVIL", nonce, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var challenge = await channel.ReadFrameAsync(default);
        if (challenge is null) return null;
        await Send(MessageTypes.AuthResponse, new AuthResponseMessage(deviceId, Convert.ToBase64String(new byte[32]), "EVIL", "", "10.0.0.66", "x", "1.0.0"));
        var result = await channel.ReadFrameAsync(default);
        return result is null ? null : MessageSerializer.Deserialize<AuthResultMessage>(MessageSerializer.DeserializeWire(result).Payload);
    }

    [Fact]
    public async Task Client_without_the_classroom_code_is_refused_logged_and_eventually_throttled()
    {
        var teacher = StartTeacher();

        var first = await AttackAsync(teacher.TcpPort);
        Assert.False(first!.Success);
        Assert.Equal(ErrorCodes.InvalidClassroomCode, first.ErrorCode);
        Assert.Contains(teacher.Server.Store.QueryLogs(50), l => l.Action == "Authentication failed");
        Assert.Empty(teacher.Server.Devices); // an unauthenticated client never even becomes a Pending computer

        for (var i = 0; i < 12; i++) await AttackAsync(teacher.TcpPort).ContinueWith(_ => 0); // brute force
        Assert.Contains(teacher.Server.Store.QueryLogs(100), l => l.Action == "Connection refused" && l.Result == "Denied");

        // ...but the throttle is per source address and a legitimate computer on another address would be unaffected; here the
        // attacker shares the loopback address, so just check the log never contains secrets.
        Assert.DoesNotContain(teacher.Server.Store.QueryLogs(200), l => l.Details.Contains(_code, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Connected_computers_are_written_to_the_audit_log()
    {
        var teacher = StartTeacher();
        var good = NewAgent(teacher, name: "PC-02");
        await good.StartAsync();
        Assert.True(await good.WaitForStateAsync(Models.ConnectionState.Connected, Wait));
        var logs = teacher.Server.Store.QueryLogs(100);
        Assert.Contains(logs, l => l.Action == "Computer connected" && l.Computer == "PC-02");
        Assert.Contains(logs, l => l.Action == "Computer approved");
    }

    [Fact]
    public async Task Classroom_code_is_not_stored_in_plain_text_in_the_database()
    {
        var teacher = StartTeacher();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(teacher.Server.Store.DatabasePath);
        Assert.DoesNotContain(_code, System.Text.Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_code, teacher.Server.ClassroomCodeText);
    }

    [Fact]
    public async Task Reported_hardware_details_are_stored_after_approval()
    {
        var teacher = StartTeacher();
        var agent = NewAgent(teacher);
        await agent.StartAsync();
        Assert.True(await agent.WaitForStateAsync(Models.ConnectionState.Connected, Wait));
        Assert.True(await teacher.WaitForAsync(() => teacher.Server.GetDevice(agent.DeviceId)!.Computer.RamBytes > 0, Wait));
        Assert.False(string.IsNullOrWhiteSpace(teacher.Server.GetDevice(agent.DeviceId)!.Computer.Cpu));
    }

    [Fact]
    public void Generated_classroom_codes_are_valid_and_random()
    {
        var codes = Enumerable.Range(0, 50).Select(_ => ClassroomCode.Generate(DateTimeOffset.UtcNow)).ToList();
        Assert.All(codes, c => Assert.True(ClassroomCode.IsValidFormat(c), c));
        Assert.Equal(50, codes.Distinct().Count());
    }
}
