using System.Globalization;
using System.Net;
using System.Text.Json;
using ClassroomControl.StudentAgent.Commands;
using ClassroomControl.StudentAgent.Models;
using ClassroomControl.TestKit;

// Simulates a classroom of Student Agents (default 16: PC-01 .. PC-16, 192.168.1.101 .. 192.168.1.116).
//
//   dotnet run --project tools/AgentSimulator -- mock --code CLASS-XXXX-YYYY
//       Starts a local mock Teacher (real TLS + UDP discovery on loopback) and N agents against it.
//   dotnet run --project tools/AgentSimulator -- real --code CLASS-XXXX-YYYY [--teacher 192.168.1.10]
//       Starts N agents that connect to a real Teacher on the LAN (UDP discovery, or --teacher address).
//
// The IP addresses are labels reported to the Teacher (each simulated agent runs in this one process, with its own Device ID
// and settings); they are not bound to network adapters.

var options = Options.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("Usage: AgentSimulator <mock|real> --code CLASS-XXXX-YYYY [--config classroom-lab.json] [--count N] [--first-ip A.B.C.D] [--teacher ADDRESS]");
    Console.Error.WriteLine("The classroom code can also be supplied through the CLASSROOM_CODE environment variable.");
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

TestTeacher? teacher = null;
if (options.Mock)
{
    teacher = new TestTeacher(new TestTeacherOptions { ClassroomCode = options.Code });
    teacher.Start();
    Console.WriteLine($"Mock Teacher listening: TCP {teacher.TcpPort}, UDP discovery {teacher.DiscoveryPort}");
}

var firstIp = IPAddress.Parse(options.FirstIp).GetAddressBytes();
var agents = new List<HeadlessAgent>();
for (var i = 0; i < options.Count; i++)
{
    var ip = new IPAddress([firstIp[0], firstIp[1], firstIp[2], (byte)(firstIp[3] + i)]).ToString();
    agents.Add(new HeadlessAgent(new HeadlessAgentOptions
    {
        ClassroomCode = options.Code,
        ComputerName = $"{options.NamePrefix}{i + 1:00}",
        StudentName = $"Student {i + 1}",
        SimulatedIp = ip,
        DiscoveryPort = teacher?.DiscoveryPort ?? options.DiscoveryPort,
        TeacherAddress = options.Mock ? string.Empty : options.TeacherAddress ?? string.Empty,
        TeacherPort = options.Mock ? 0 : options.TcpPort,
        WriteLogFile = false,
        Configure = o =>
        {
            o.AllowLoopbackTeacher = options.Mock;
            o.DiscoveryTargets = options.Mock ? ["127.0.0.1"] : [];
            o.ReconnectDelaysSeconds = [1, 2, 5, 10, 20, 30];
            o.HeartbeatIntervalSeconds = 5;
            o.HeartbeatTimeoutSeconds = 15;
            o.DiscoveryTimeoutSeconds = 8;
            o.DiscoveryRetryIntervalMilliseconds = 1000;
        },
    }));
}

foreach (var a in agents) await a.StartAsync();
Console.WriteLine($"{agents.Count} agents started. Ctrl+C to stop.");

try
{
    while (!stop.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
        Console.WriteLine();
        Console.WriteLine($"{"Computer",-8} {"IP",-15} {"State",-20} {"Registration",-12} Heartbeats");
        foreach (var a in agents)
        {
            var s = a.Status.Current;
            var beats = teacher?.GetDevice(a.DeviceId)?.HeartbeatCount.ToString(CultureInfo.InvariantCulture) ?? "-";
            Console.WriteLine($"{a.Options.ComputerName,-8} {a.Options.SimulatedIp,-15} {s.State,-20} {s.Registration,-12} {beats}");
        }

        if (teacher is not null && agents.All(a => a.Status.Current.State == ConnectionState.Connected))
        {
            var pings = await Task.WhenAll(agents.Select(a => teacher.SendCommandAsync(a.DeviceId, CommandNames.Ping)));
            Console.WriteLine($"Ping: {pings.Count(p => p.Status == CommandStatus.Success)}/{pings.Length} succeeded");
        }
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C
}

Console.WriteLine("Stopping...");
foreach (var a in agents) await a.DisposeAsync();
if (teacher is not null) await teacher.DisposeAsync();
return 0;

internal sealed record Options(bool Mock, string Code, int Count, string FirstIp, string NamePrefix, string? TeacherAddress, int TcpPort, int DiscoveryPort)
{
    public static Options? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("mock" or "real")) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i + 1 < args.Length; i += 2) map[args[i].TrimStart('-')] = args[i + 1];

        var configPath = map.GetValueOrDefault("config") ?? Path.Combine(AppContext.BaseDirectory, "classroom-lab.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var teacher = doc.RootElement.GetProperty("Teacher");
        var students = doc.RootElement.GetProperty("Students");

        var code = map.GetValueOrDefault("code") ?? Environment.GetEnvironmentVariable("CLASSROOM_CODE") ?? string.Empty;
        if (!ClassroomControl.Shared.Communication.Security.ClassroomCode.IsValidFormat(code)) return null;

        return new Options(
            args[0] == "mock",
            code,
            int.Parse(map.GetValueOrDefault("count") ?? students.GetProperty("Count").GetInt32().ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            map.GetValueOrDefault("first-ip") ?? students.GetProperty("FirstIp").GetString()!,
            students.GetProperty("NamePrefix").GetString()!,
            map.GetValueOrDefault("teacher") ?? teacher.GetProperty("Address").GetString(),
            teacher.GetProperty("TcpPort").GetInt32(),
            teacher.GetProperty("DiscoveryPort").GetInt32());
    }
}
