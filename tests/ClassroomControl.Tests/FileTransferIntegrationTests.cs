using ClassroomControl.ClassroomServer;
using ClassroomControl.Shared.Communication.Protocol;
using ClassroomControl.Shared.Files;
using ClassroomControl.StudentAgent.Features;
using ClassroomControl.StudentAgent.Models;
using ClassroomControl.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClassroomControl.StudentAgent.Tests;

/// <summary>File transfer end to end over the real TLS connection: real Teacher server, real Student services, fake Windows.</summary>
public sealed class FileTransferIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly string _code = TestSupport.NewCode();
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly string _work = Path.Combine(Path.GetTempPath(), "cc-files-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_work);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var d in Enumerable.Reverse(_disposables)) await d.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_work)) Directory.Delete(_work, true);
    }

    private TestTeacher NewTeacher(Action<TestTeacherOptions>? configure = null)
    {
        var options = new TestTeacherOptions { ClassroomCode = _code };
        configure?.Invoke(options);
        var teacher = new TestTeacher(options);
        teacher.Start();
        _disposables.Add(teacher);
        return teacher;
    }

    private async Task<HeadlessAgent> NewAgentAsync(TestTeacher teacher, string name, bool waitConnected = true)
    {
        var agent = new HeadlessAgent(new HeadlessAgentOptions
        {
            ClassroomCode = _code, ComputerName = name, StudentName = "Ali " + name, DiscoveryPort = teacher.DiscoveryPort,
            TeacherAddress = "127.0.0.1", TeacherPort = teacher.TcpPort,
        });
        _disposables.Add(agent);
        await agent.StartAsync();
        if (waitConnected) Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait), agent.Status.Current.StatusText);
        return agent;
    }

    private string MakeFile(string name, int bytes)
    {
        var path = Path.Combine(_work, name);
        var data = new byte[bytes];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static string StudentInbox(HeadlessAgent agent) => agent.Services.GetRequiredService<ClassroomControl.Infrastructure.IAppPaths>().ReceivedFilesDirectory;

    [Fact]
    public async Task The_teacher_sends_a_file_and_the_student_sees_it_announced()
    {
        var teacher = NewTeacher();
        var agent = await NewAgentAsync(teacher, "PC-01");
        var updates = new List<TransferUpdate>();
        teacher.Server.TransferChanged += (_, u) => { lock (updates) updates.Add(u); };
        var source = MakeFile("topshiriq.pdf", 2 * FileTransferLimits.ChunkBytes + 777);

        var result = await teacher.Server.SendFileAsync(agent.DeviceId, source);

        Assert.Equal(TransferState.Completed, result.State);
        var stored = Path.Combine(StudentInbox(agent), "topshiriq.pdf");
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(stored));
        Assert.True(await teacher.WaitForAsync(() => agent.Platform.Notifier.Shown.Any(n => n.Text.Contains("topshiriq.pdf")), Wait), "the student must be told about the file");
        Assert.Contains(teacher.Server.Store.QueryLogs(100), l => l.Action == "SendFile" && l.Result == "OK" && l.Details.Contains("topshiriq.pdf"));
        lock (updates) Assert.Contains(updates, u => u.Progress.State == TransferState.Completed && u.DeviceId == agent.DeviceId);
    }

    [Fact]
    public async Task A_student_sends_a_file_to_the_teacher()
    {
        var teacher = NewTeacher();
        var agent = await NewAgentAsync(teacher, "PC-02");
        var source = MakeFile("javob.docx", FileTransferLimits.ChunkBytes + 5);
        var files = agent.Services.GetRequiredService<IFileTransferService>();
        Assert.True(files.CanSend);

        var result = await files.SendToTeacherAsync(source, CancellationToken.None);

        Assert.Equal(TransferState.Completed, result.State);
        var stored = Path.Combine(teacher.Server.ReceivedFolderFor(agent.DeviceId), "javob.docx");
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(stored));
        Assert.EndsWith("Ali PC-02_PC-02", teacher.Server.ReceivedFolderFor(agent.DeviceId));
        Assert.True(await teacher.WaitForAsync(() => teacher.Server.Store.QueryLogs(100).Any(l => l.Action == "ReceiveFile" && l.Result == "OK"), Wait));
    }

    [Fact]
    public async Task The_teacher_can_stop_accepting_files_from_students()
    {
        var teacher = NewTeacher();
        var agent = await NewAgentAsync(teacher, "PC-03");
        teacher.Server.Settings.AcceptFilesFromStudents = false;
        var result = await agent.Services.GetRequiredService<IFileTransferService>().SendToTeacherAsync(MakeFile("x.txt", 100), CancellationToken.None);
        Assert.Equal((TransferState.Failed, ErrorCodes.FileRejected), (result.State, result.ErrorCode));
        Assert.False(Directory.Exists(teacher.Server.ReceivedFolderFor(agent.DeviceId)) && Directory.GetFiles(teacher.Server.ReceivedFolderFor(agent.DeviceId)).Length > 0);
    }

    [Fact]
    public async Task Programs_are_not_accepted_by_the_student_even_if_the_teacher_allows_them()
    {
        var teacher = NewTeacher();
        var agent = await NewAgentAsync(teacher, "PC-04");
        teacher.Server.Settings.AllowExecutableFiles = true; // the teacher side lets it go; the student still refuses
        var result = await teacher.Server.SendFileAsync(agent.DeviceId, MakeFile("setup.exe", 100));
        Assert.Equal(ErrorCodes.FileTypeBlocked, result.ErrorCode);
        Assert.False(File.Exists(Path.Combine(StudentInbox(agent), "setup.exe")));
    }

    [Fact]
    public async Task The_teachers_size_limit_applies_before_anything_is_sent()
    {
        var teacher = NewTeacher();
        var agent = await NewAgentAsync(teacher, "PC-05");
        teacher.Server.Settings.MaxFileSizeMb = 1;
        var result = await teacher.Server.SendFileAsync(agent.DeviceId, MakeFile("katta.bin", 1024 * 1024 + 1));
        Assert.Equal(ErrorCodes.FileTooLarge, result.ErrorCode);
    }

    [Fact]
    public async Task A_computer_that_is_not_approved_neither_receives_nor_sends_files()
    {
        var teacher = NewTeacher(o => o.Approval = ApprovalMode.Manual);
        var agent = await NewAgentAsync(teacher, "PC-06", waitConnected: false);
        Assert.True(await teacher.WaitForAsync(() => teacher.Server.Devices.Any(d => d.Online), Wait));

        var toStudent = await teacher.Server.SendFileAsync(agent.DeviceId, MakeFile("a.txt", 10));
        Assert.Equal(ErrorCodes.NotRegistered, toStudent.ErrorCode);

        var fromStudent = await agent.Services.GetRequiredService<IFileTransferService>().SendToTeacherAsync(MakeFile("b.txt", 10), CancellationToken.None);
        Assert.Equal(TransferState.Failed, fromStudent.State);
        Assert.False(Directory.Exists(teacher.Server.ReceivedFolderFor(agent.DeviceId)) && Directory.GetFiles(teacher.Server.ReceivedFolderFor(agent.DeviceId)).Length > 0);
    }

    [Fact]
    public async Task One_file_goes_to_many_computers_and_an_offline_one_is_reported()
    {
        var teacher = NewTeacher();
        var agents = new List<HeadlessAgent>();
        foreach (var name in new[] { "PC-A", "PC-B", "PC-C" }) agents.Add(await NewAgentAsync(teacher, name));
        var source = MakeFile("hammaga.txt", FileTransferLimits.ChunkBytes * 2);

        var results = await teacher.Server.SendFileToManyAsync(agents.Select(a => a.DeviceId).Append("no-such-device"), source);

        Assert.Equal(4, results.Count);
        Assert.Equal(3, results.Count(r => r.Result.State == TransferState.Completed));
        Assert.Equal("OFFLINE", results.Single(r => r.DeviceId == "no-such-device").Result.ErrorCode);
        foreach (var agent in agents) Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(StudentInbox(agent), "hammaga.txt")));
    }

    [Fact]
    public async Task A_student_without_a_teacher_connection_cannot_send()
    {
        var teacher = NewTeacher();
        var agent = await NewAgentAsync(teacher, "PC-07");
        await teacher.StopAsync();
        Assert.True(await agent.WaitForStateAsync(ConnectionState.Disconnected, Wait) || !agent.Services.GetRequiredService<IFileTransferService>().CanSend);
        Assert.True(await teacher.WaitForAsync(() => !agent.Services.GetRequiredService<IFileTransferService>().CanSend, Wait));
        var result = await agent.Services.GetRequiredService<IFileTransferService>().SendToTeacherAsync(MakeFile("c.txt", 10), CancellationToken.None);
        Assert.Equal(TransferState.Failed, result.State);
    }

    [Fact]
    public async Task The_teacher_data_can_never_be_read_by_asking_the_student()
    {
        // The protocol has no "send me file X" request: a received offer from the teacher can only place a file in the inbox.
        Assert.DoesNotContain(CommandNames.All, name => name.Contains("File", StringComparison.OrdinalIgnoreCase));
        await Task.CompletedTask;
    }
}
