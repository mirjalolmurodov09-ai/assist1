using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ClassroomControl.ClassroomServer.Data;

/// <summary>SQLite persistence (users, classrooms, groups, students, computers, sessions, commands, logs, screenshots, settings).
/// Every call opens its own pooled connection, so the store is safe to use from any thread.</summary>
public sealed class ClassroomStore
{
    private readonly string _connectionString;

    public ClassroomStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true, ForeignKeys = true }.ToString();
        Migrate();
    }

    public string DatabasePath { get; }

    // ---------- plumbing ----------

    private T Run<T>(Func<SqliteConnection, T> body)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return body(connection);
    }

    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = c.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseIso(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? ParseIsoOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : ParseIso(r.GetString(i));

    private void Migrate()
    {
        Run(c =>
        {
            Cmd(c, "PRAGMA journal_mode=WAL;").ExecuteNonQuery();
            Cmd(c, "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL);").ExecuteNonQuery();
            var current = Convert.ToInt32(Cmd(c, "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;").ExecuteScalar(), CultureInfo.InvariantCulture);
            foreach (var (version, sql) in Schema.Migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
            {
                using var tx = c.BeginTransaction();
                using (var command = c.CreateCommand())
                {
                    command.Transaction = tx;
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }
                using (var record = c.CreateCommand())
                {
                    record.Transaction = tx;
                    record.CommandText = "INSERT INTO SchemaVersion (Version) VALUES ($v);";
                    record.Parameters.AddWithValue("$v", version);
                    record.ExecuteNonQuery();
                }
                tx.Commit();
            }
            return 0;
        });
    }

    public int SchemaVersion => Run(c => Convert.ToInt32(Cmd(c, "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;").ExecuteScalar(), CultureInfo.InvariantCulture));

    // ---------- users ----------

    public int CountUsers() => Run(c => Convert.ToInt32(Cmd(c, "SELECT COUNT(*) FROM Users;").ExecuteScalar(), CultureInfo.InvariantCulture));

    public UserRecord? GetUser(string username) => Run(c =>
    {
        using var r = Cmd(c, "SELECT Id, Username, PasswordHash, Role, CreatedAt, LastLoginAt, Disabled FROM Users WHERE Username = $u COLLATE NOCASE;", ("$u", username)).ExecuteReader();
        return r.Read() ? ReadUser(r) : null;
    });

    public IReadOnlyList<UserRecord> ListUsers() => Run(c =>
    {
        var list = new List<UserRecord>();
        using var r = Cmd(c, "SELECT Id, Username, PasswordHash, Role, CreatedAt, LastLoginAt, Disabled FROM Users ORDER BY Username;").ExecuteReader();
        while (r.Read()) list.Add(ReadUser(r));
        return list;
    });

    private static UserRecord ReadUser(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
        ParseIso(r.GetString(4)), ParseIsoOrNull(r, 5), r.GetInt64(6) != 0);

    public long AddUser(string username, string passwordHash, string role, DateTimeOffset now) => Run(c =>
        Convert.ToInt64(Cmd(c, "INSERT INTO Users (Username, PasswordHash, Role, CreatedAt, Disabled) VALUES ($u, $p, $r, $t, 0); SELECT last_insert_rowid();",
            ("$u", username), ("$p", passwordHash), ("$r", role), ("$t", Iso(now))).ExecuteScalar(), CultureInfo.InvariantCulture));

    public void UpdatePassword(long userId, string passwordHash) => Run(c => Cmd(c, "UPDATE Users SET PasswordHash = $p WHERE Id = $i;", ("$p", passwordHash), ("$i", userId)).ExecuteNonQuery());
    public void SetUserDisabled(long userId, bool disabled) => Run(c => Cmd(c, "UPDATE Users SET Disabled = $d WHERE Id = $i;", ("$d", disabled ? 1 : 0), ("$i", userId)).ExecuteNonQuery());
    public void DeleteUser(long userId) => Run(c => Cmd(c, "DELETE FROM Users WHERE Id = $i;", ("$i", userId)).ExecuteNonQuery());
    public void TouchLogin(long userId, DateTimeOffset now) => Run(c => Cmd(c, "UPDATE Users SET LastLoginAt = $t WHERE Id = $i;", ("$t", Iso(now)), ("$i", userId)).ExecuteNonQuery());

    // ---------- classrooms ----------

    public ClassroomRecord? GetFirstClassroom() => Run(c =>
    {
        using var r = Cmd(c, "SELECT Id, Name, CodeProtected, TeacherId, CreatedAt FROM Classrooms ORDER BY Id LIMIT 1;").ExecuteReader();
        return r.Read() ? new ClassroomRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), ParseIso(r.GetString(4))) : null;
    });

    public ClassroomRecord AddClassroom(string name, string codeProtected, string teacherId, DateTimeOffset now) => Run(c =>
    {
        var id = Convert.ToInt64(Cmd(c, "INSERT INTO Classrooms (Name, CodeProtected, TeacherId, CreatedAt) VALUES ($n, $c, $t, $d); SELECT last_insert_rowid();",
            ("$n", name), ("$c", codeProtected), ("$t", teacherId), ("$d", Iso(now))).ExecuteScalar(), CultureInfo.InvariantCulture);
        return new ClassroomRecord(id, name, codeProtected, teacherId, now);
    });

    public void UpdateClassroomCode(long classroomId, string codeProtected) =>
        Run(c => Cmd(c, "UPDATE Classrooms SET CodeProtected = $c WHERE Id = $i;", ("$c", codeProtected), ("$i", classroomId)).ExecuteNonQuery());

    public void RenameClassroom(long classroomId, string name) =>
        Run(c => Cmd(c, "UPDATE Classrooms SET Name = $n WHERE Id = $i;", ("$n", name), ("$i", classroomId)).ExecuteNonQuery());

    // ---------- groups ----------

    public IReadOnlyList<GroupRecord> ListGroups(long classroomId) => Run(c =>
    {
        var list = new List<GroupRecord>();
        using var r = Cmd(c, "SELECT Id, ClassroomId, Name FROM Groups WHERE ClassroomId = $c ORDER BY Name;", ("$c", classroomId)).ExecuteReader();
        while (r.Read()) list.Add(new GroupRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(2)));
        return list;
    });

    public GroupRecord AddGroup(long classroomId, string name) => Run(c =>
    {
        var id = Convert.ToInt64(Cmd(c, "INSERT INTO Groups (ClassroomId, Name) VALUES ($c, $n); SELECT last_insert_rowid();", ("$c", classroomId), ("$n", name)).ExecuteScalar(), CultureInfo.InvariantCulture);
        return new GroupRecord(id, classroomId, name);
    });

    public void RenameGroup(long groupId, string name) => Run(c => Cmd(c, "UPDATE Groups SET Name = $n WHERE Id = $i;", ("$n", name), ("$i", groupId)).ExecuteNonQuery());
    public void DeleteGroup(long groupId) => Run(c => Cmd(c, "DELETE FROM Groups WHERE Id = $i;", ("$i", groupId)).ExecuteNonQuery());

    // ---------- computers ----------

    private const string ComputerColumns = "Id, DeviceId, ClassroomId, ComputerName, DisplayName, StudentName, IpAddress, MacAddress, Cpu, RamBytes, GroupId, Status, LastSeen, CreatedAt";

    private static ComputerRecord ReadComputer(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
        r.GetString(8), r.GetInt64(9), r.IsDBNull(10) ? null : r.GetInt64(10), Enum.Parse<RegistrationState>(r.GetString(11)),
        ParseIsoOrNull(r, 12), ParseIso(r.GetString(13)));

    public ComputerRecord? GetComputer(string deviceId) => Run(c =>
    {
        using var r = Cmd(c, $"SELECT {ComputerColumns} FROM Computers WHERE DeviceId = $d;", ("$d", deviceId)).ExecuteReader();
        return r.Read() ? ReadComputer(r) : null;
    });

    public IReadOnlyList<ComputerRecord> ListComputers(long classroomId) => Run(c =>
    {
        var list = new List<ComputerRecord>();
        using var r = Cmd(c, $"SELECT {ComputerColumns} FROM Computers WHERE ClassroomId = $c ORDER BY ComputerName;", ("$c", classroomId)).ExecuteReader();
        while (r.Read()) list.Add(ReadComputer(r));
        return list;
    });

    /// <summary>Inserts a new (Pending) computer or refreshes the reported details of a known one. Status of known computers is kept.</summary>
    public ComputerRecord UpsertComputer(string deviceId, long classroomId, string computerName, string studentName, string ipAddress, DateTimeOffset now) => Run(c =>
    {
        Cmd(c, @"INSERT INTO Computers (DeviceId, ClassroomId, ComputerName, DisplayName, StudentName, IpAddress, MacAddress, Cpu, RamBytes, Status, LastSeen, CreatedAt)
                 VALUES ($d, $c, $n, '', $s, $ip, '', '', 0, 'Pending', $t, $t)
                 ON CONFLICT(DeviceId) DO UPDATE SET ComputerName = $n, StudentName = $s, IpAddress = $ip, LastSeen = $t;",
            ("$d", deviceId), ("$c", classroomId), ("$n", computerName), ("$s", studentName), ("$ip", ipAddress), ("$t", Iso(now))).ExecuteNonQuery();
        using var r = Cmd(c, $"SELECT {ComputerColumns} FROM Computers WHERE DeviceId = $d;", ("$d", deviceId)).ExecuteReader();
        r.Read();
        return ReadComputer(r);
    });

    public void SetComputerStatus(string deviceId, RegistrationState status) =>
        Run(c => Cmd(c, "UPDATE Computers SET Status = $s WHERE DeviceId = $d;", ("$s", status.ToString()), ("$d", deviceId)).ExecuteNonQuery());

    public void SetComputerGroup(string deviceId, long? groupId) =>
        Run(c => Cmd(c, "UPDATE Computers SET GroupId = $g WHERE DeviceId = $d;", ("$g", groupId), ("$d", deviceId)).ExecuteNonQuery());

    public void SetComputerDisplayName(string deviceId, string displayName) =>
        Run(c => Cmd(c, "UPDATE Computers SET DisplayName = $n WHERE DeviceId = $d;", ("$n", displayName), ("$d", deviceId)).ExecuteNonQuery());

    public void UpdateComputerHardware(string deviceId, string macAddress, string cpu, long ramBytes) =>
        Run(c => Cmd(c, "UPDATE Computers SET MacAddress = $m, Cpu = $c, RamBytes = $r WHERE DeviceId = $d;",
            ("$m", macAddress), ("$c", cpu), ("$r", ramBytes), ("$d", deviceId)).ExecuteNonQuery());

    public void TouchComputer(string deviceId, DateTimeOffset now) =>
        Run(c => Cmd(c, "UPDATE Computers SET LastSeen = $t WHERE DeviceId = $d;", ("$t", Iso(now)), ("$d", deviceId)).ExecuteNonQuery());

    public void DeleteComputer(string deviceId) => Run(c => Cmd(c, "DELETE FROM Computers WHERE DeviceId = $d;", ("$d", deviceId)).ExecuteNonQuery());

    // ---------- students ----------

    public void EnsureStudent(string fullName, long classroomId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return;
        Run(c => Cmd(c, "INSERT OR IGNORE INTO Students (FullName, ClassroomId, CreatedAt) VALUES ($n, $c, $t);",
            ("$n", fullName), ("$c", classroomId), ("$t", Iso(now))).ExecuteNonQuery());
    }

    public int CountStudents() => Run(c => Convert.ToInt32(Cmd(c, "SELECT COUNT(*) FROM Students;").ExecuteScalar(), CultureInfo.InvariantCulture));

    // ---------- sessions & commands ----------

    public void OpenSession(string sessionId, string deviceId, DateTimeOffset now) => Run(c =>
        Cmd(c, "INSERT INTO Sessions (SessionId, DeviceId, ConnectedAt) VALUES ($s, $d, $t);", ("$s", sessionId), ("$d", deviceId), ("$t", Iso(now))).ExecuteNonQuery());

    public void CloseSession(string sessionId, string reason, DateTimeOffset now) => Run(c =>
        Cmd(c, "UPDATE Sessions SET DisconnectedAt = $t, Reason = $r WHERE SessionId = $s;", ("$t", Iso(now)), ("$r", reason), ("$s", sessionId)).ExecuteNonQuery());

    public int CountSessions(string deviceId) => Run(c => Convert.ToInt32(Cmd(c, "SELECT COUNT(*) FROM Sessions WHERE DeviceId = $d;", ("$d", deviceId)).ExecuteScalar(), CultureInfo.InvariantCulture));

    public int CountSessions() => Run(c => Convert.ToInt32(Cmd(c, "SELECT COUNT(*) FROM Sessions;").ExecuteScalar(), CultureInfo.InvariantCulture));

    public void AddCommand(string commandId, string deviceId, string name, string requestedBy, DateTimeOffset now) => Run(c =>
        Cmd(c, "INSERT INTO Commands (CommandId, DeviceId, Name, Status, RequestedAt, RequestedBy) VALUES ($i, $d, $n, 'Sent', $t, $u);",
            ("$i", commandId), ("$d", deviceId), ("$n", name), ("$t", Iso(now)), ("$u", requestedBy)).ExecuteNonQuery());

    public void CompleteCommand(string commandId, string status, string? errorCode, DateTimeOffset now) => Run(c =>
        Cmd(c, "UPDATE Commands SET Status = $s, ErrorCode = $e, CompletedAt = $t WHERE CommandId = $i;",
            ("$s", status), ("$e", errorCode), ("$t", Iso(now)), ("$i", commandId)).ExecuteNonQuery());

    public IReadOnlyList<CommandRecord> ListCommands(int limit) => Run(c =>
    {
        var list = new List<CommandRecord>();
        using var r = Cmd(c, "SELECT Id, CommandId, DeviceId, Name, Status, ErrorCode, RequestedAt, CompletedAt, RequestedBy FROM Commands ORDER BY Id DESC LIMIT $l;", ("$l", limit)).ExecuteReader();
        while (r.Read())
            list.Add(new CommandRecord(r.GetInt64(0), r.GetString(1), null, r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                ParseIso(r.GetString(6)), ParseIsoOrNull(r, 7), r.GetString(8)));
        return list;
    });

    // ---------- logs ----------

    public void AddLog(DateTimeOffset now, string teacher, string computer, string action, string result, string details) => Run(c =>
        Cmd(c, "INSERT INTO Logs (Timestamp, Teacher, Computer, Action, Result, Details) VALUES ($t, $te, $c, $a, $r, $d);",
            ("$t", Iso(now)), ("$te", teacher), ("$c", computer), ("$a", action), ("$r", result), ("$d", details)).ExecuteNonQuery());

    public IReadOnlyList<LogEntry> QueryLogs(int limit, string? text = null) => Run(c =>
    {
        var list = new List<LogEntry>();
        var like = string.IsNullOrWhiteSpace(text) ? null : $"%{text.Trim()}%";
        using var r = Cmd(c, @"SELECT Id, Timestamp, Teacher, Computer, Action, Result, Details FROM Logs
                               WHERE $q IS NULL OR Teacher LIKE $q OR Computer LIKE $q OR Action LIKE $q OR Result LIKE $q OR Details LIKE $q
                               ORDER BY Id DESC LIMIT $l;", ("$q", like), ("$l", limit)).ExecuteReader();
        while (r.Read()) list.Add(new LogEntry(r.GetInt64(0), ParseIso(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
        return list;
    });

    // ---------- screenshots ----------

    public long AddScreenshot(long? computerId, string filePath, DateTimeOffset takenAt, string studentName, string computerName) => Run(c =>
        Convert.ToInt64(Cmd(c, "INSERT INTO Screenshots (ComputerId, FilePath, TakenAt, StudentName, ComputerName) VALUES ($c, $f, $t, $s, $n); SELECT last_insert_rowid();",
            ("$c", computerId), ("$f", filePath), ("$t", Iso(takenAt)), ("$s", studentName), ("$n", computerName)).ExecuteScalar(), CultureInfo.InvariantCulture));

    public IReadOnlyList<ScreenshotRecord> ListScreenshots(int limit) => Run(c =>
    {
        var list = new List<ScreenshotRecord>();
        using var r = Cmd(c, "SELECT Id, ComputerId, FilePath, TakenAt, StudentName, ComputerName FROM Screenshots ORDER BY Id DESC LIMIT $l;", ("$l", limit)).ExecuteReader();
        while (r.Read())
            list.Add(new ScreenshotRecord(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), ParseIso(r.GetString(3)), r.GetString(4), r.GetString(5)));
        return list;
    });

    public void DeleteScreenshot(long id) => Run(c => Cmd(c, "DELETE FROM Screenshots WHERE Id = $i;", ("$i", id)).ExecuteNonQuery());

    // ---------- settings ----------

    public string? GetSetting(string key) => Run(c => Cmd(c, "SELECT Value FROM Settings WHERE Key = $k;", ("$k", key)).ExecuteScalar() as string);

    public void SetSetting(string key, string value) => Run(c =>
        Cmd(c, "INSERT INTO Settings (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = $v;", ("$k", key), ("$v", value)).ExecuteNonQuery());
}
