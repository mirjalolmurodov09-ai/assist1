using System.Collections.Concurrent;
using System.Text.Json;
using ClassroomControl.StudentAgent.Commands;
using ClassroomControl.StudentAgent.Features;
using ClassroomControl.StudentAgent.Models;
using ClassroomControl.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClassroomControl.StudentAgent.Tests;

public sealed class FrameDifferTests
{
    private static RawFrame Frame(int w, int h, Action<byte[], int>? paint = null)
    {
        var pixels = new byte[w * h * 4];
        paint?.Invoke(pixels, w * 4);
        return new RawFrame(w, h, w * 4, pixels);
    }

    private static void Dot(byte[] pixels, int stride, int x, int y) => pixels[y * stride + x * 4] = 255;

    [Fact]
    public void First_frame_is_always_a_key_frame()
    {
        var d = new FrameDiffer().Compare(Frame(320, 200), false);
        Assert.True(d.Changed);
        Assert.True(d.KeyFrame);
        Assert.Equal(new PixelRegion(0, 0, 320, 200), d.Region);
    }

    [Fact]
    public void Identical_frames_produce_nothing_to_send()
    {
        var differ = new FrameDiffer();
        differ.Compare(Frame(320, 200), false);
        Assert.False(differ.Compare(Frame(320, 200), false).Changed);
    }

    [Fact]
    public void One_changed_pixel_yields_just_its_tile()
    {
        var differ = new FrameDiffer();
        differ.Compare(Frame(640, 480), false);
        var d = differ.Compare(Frame(640, 480, (p, s) => Dot(p, s, 100, 70)), false);
        Assert.True(d.Changed);
        Assert.False(d.KeyFrame);
        Assert.Equal(new PixelRegion(96, 64, 32, 32), d.Region); // tile (3,2)
    }

    [Fact]
    public void Two_distant_changes_give_their_bounding_box()
    {
        var differ = new FrameDiffer();
        differ.Compare(Frame(640, 480), false);
        var d = differ.Compare(Frame(640, 480, (p, s) => { Dot(p, s, 10, 10); Dot(p, s, 200, 100); }), false);
        Assert.Equal(new PixelRegion(0, 0, 224, 128), d.Region);
    }

    [Fact]
    public void Edge_tiles_are_clipped_to_the_frame()
    {
        var differ = new FrameDiffer();
        differ.Compare(Frame(100, 70), false);
        var d = differ.Compare(Frame(100, 70, (p, s) => Dot(p, s, 99, 69)), false);
        Assert.Equal(new PixelRegion(96, 64, 4, 6), d.Region);
    }

    [Fact]
    public void Large_changes_and_size_changes_become_key_frames()
    {
        var differ = new FrameDiffer();
        differ.Compare(Frame(320, 200), false);
        var big = differ.Compare(Frame(320, 200, (p, s) => { for (var y = 0; y < 200; y++) for (var x = 0; x < 250; x++) Dot(p, s, x, y); }), false);
        Assert.True(big.KeyFrame);

        var resized = differ.Compare(Frame(160, 100), false);
        Assert.True(resized.KeyFrame);
        Assert.True(differ.Compare(Frame(160, 100), true).KeyFrame); // forced
    }
}

public sealed class AdaptiveQualityTests
{
    [Fact]
    public void Slow_frames_lower_quality_then_size_then_rate()
    {
        var a = new AdaptiveQuality(10, 70, 1280);
        for (var i = 0; i < 6; i++) a.Report(TimeSpan.FromMilliseconds(500));
        Assert.Equal(AdaptiveQuality.MinQuality, a.Quality);
        for (var i = 0; i < 12; i++) a.Report(TimeSpan.FromMilliseconds(500));
        Assert.True(a.MaxWidth < 1280);
        for (var i = 0; i < 60; i++) a.Report(TimeSpan.FromMilliseconds(5000));
        Assert.Equal(AdaptiveQuality.MinWidth, a.MaxWidth);
        Assert.Equal(1, a.Fps);
        Assert.True(a.Degraded);
    }

    [Fact]
    public void A_fast_link_restores_the_requested_settings()
    {
        var a = new AdaptiveQuality(10, 70, 1280);
        for (var i = 0; i < 30; i++) a.Report(TimeSpan.FromMilliseconds(500));
        Assert.True(a.Degraded);
        for (var i = 0; i < 400; i++) a.Report(TimeSpan.FromMilliseconds(1));
        Assert.False(a.Degraded);
        Assert.Equal((10, 70, 1280), (a.Fps, a.Quality, a.MaxWidth));
    }

    [Fact]
    public void Healthy_links_are_never_degraded()
    {
        var a = new AdaptiveQuality(5, 50, 480);
        for (var i = 0; i < 100; i++) a.Report(TimeSpan.FromMilliseconds(40));
        Assert.False(a.Degraded);
    }
}

public sealed class RemoteInputServiceTests
{
    private static (RemoteInputService Service, RecordingInput Input, FeatureState State) Create()
    {
        var input = new RecordingInput();
        var state = new FeatureState();
        return (new RemoteInputService(input, state, NullLogger<RemoteInputService>.Instance), input, state);
    }

    [Fact]
    public void Input_is_ignored_until_remote_control_is_started()
    {
        var (service, input, _) = Create();
        service.Handle(new MouseEventMessage(MouseAction.Down, 0.5, 0.5, MouseButtonKind.Left, 0));
        service.Handle(new KeyboardEventMessage(65, true, false));
        Assert.Empty(input.Mouse);
        Assert.Empty(input.Keys);
    }

    [Fact]
    public void Input_flows_while_active_and_stops_after_stop()
    {
        var (service, input, state) = Create();
        service.Start();
        Assert.True(state.RemoteControl);
        service.Handle(new MouseEventMessage(MouseAction.Move, 0.25, 0.75, MouseButtonKind.None, 0));
        service.Handle(new KeyboardEventMessage(65, true, false));
        Assert.Single(input.Mouse);
        Assert.Single(input.Keys);

        service.Stop();
        service.Handle(new KeyboardEventMessage(66, true, false));
        Assert.Single(input.Keys);
        Assert.False(state.RemoteControl);
    }

    [Fact]
    public void Nonsense_values_are_clamped_or_dropped()
    {
        var (service, input, _) = Create();
        service.Start();
        service.Handle(new MouseEventMessage(MouseAction.Move, 9, -3, MouseButtonKind.None, 0));
        Assert.Equal((1.0, 0.0), (input.Mouse.Single().X, input.Mouse.Single().Y));
        service.Handle(new MouseEventMessage(MouseAction.Move, double.NaN, 0, MouseButtonKind.None, 0));
        service.Handle(new MouseEventMessage(MouseAction.Wheel, 0, 0, MouseButtonKind.None, 999_999));
        service.Handle(new KeyboardEventMessage(0, true, false));
        service.Handle(new KeyboardEventMessage(300, true, false));
        Assert.Single(input.Mouse);
        Assert.Empty(input.Keys);
    }
}

/// <summary>Every new feature end to end: real Teacher server, real Student services, fake Windows.</summary>
public sealed class FeatureIntegrationTests : IAsyncLifetime
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

    private async Task<(TestTeacher Teacher, HeadlessAgent Agent)> ConnectedAsync(Action<TestTeacherOptions>? teacher = null, Action<HeadlessAgentOptions>? agent = null)
    {
        var to = new TestTeacherOptions { ClassroomCode = _code };
        teacher?.Invoke(to);
        var t = new TestTeacher(to);
        t.Start();
        _disposables.Add(t);
        var ao = new HeadlessAgentOptions { ClassroomCode = _code, ComputerName = "PC-01", StudentName = "Ali", DiscoveryPort = t.DiscoveryPort, TeacherAddress = "127.0.0.1", TeacherPort = t.TcpPort };
        agent?.Invoke(ao);
        var a = new HeadlessAgent(ao);
        _disposables.Add(a);
        await a.StartAsync();
        Assert.True(await a.WaitForStateAsync(ConnectionState.Connected, Wait), a.Status.Current.StatusText);
        return (t, a);
    }

    [Fact]
    public async Task Lock_shows_the_teachers_message_and_unlock_removes_it()
    {
        var (t, a) = await ConnectedAsync();
        var r = await t.Server.LockAsync(a.DeviceId, "5 daqiqa kuting");
        Assert.True(r.Success);
        Assert.True(a.Platform.Lock.IsShown);
        Assert.Equal("5 daqiqa kuting", a.Platform.Lock.Message);
        Assert.True(await t.WaitForAsync(() => t.Server.GetDevice(a.DeviceId)!.Locked, Wait)); // status update reaches the Teacher grid

        Assert.True((await t.Server.UnlockAsync(a.DeviceId)).Success);
        Assert.False(a.Platform.Lock.IsShown);
        Assert.True(await t.WaitForAsync(() => !t.Server.GetDevice(a.DeviceId)!.Locked, Wait));
    }

    [Fact]
    public async Task Lock_without_text_uses_the_default_message()
    {
        var (t, a) = await ConnectedAsync();
        await t.Server.LockAsync(a.DeviceId, "  ");
        Assert.Equal(LockCommandHandler.DefaultMessage, a.Platform.Lock.Message);
    }

    [Fact]
    public async Task Lock_all_locks_every_connected_computer_at_once()
    {
        var teacher = new TestTeacher(new TestTeacherOptions { ClassroomCode = _code });
        teacher.Start();
        _disposables.Add(teacher);
        var agents = Enumerable.Range(1, 5).Select(i =>
        {
            var agent = new HeadlessAgent(new HeadlessAgentOptions { ClassroomCode = _code, ComputerName = $"PC-{i:00}", DiscoveryPort = teacher.DiscoveryPort, TeacherAddress = "127.0.0.1", TeacherPort = teacher.TcpPort });
            _disposables.Add(agent);
            return agent;
        }).ToList();
        foreach (var a in agents) await a.StartAsync();
        foreach (var a in agents) Assert.True(await a.WaitForStateAsync(ConnectionState.Connected, Wait));

        var results = await teacher.Server.ExecuteManyAsync(agents.Select(a => a.DeviceId), CommandNames.Lock, new LockParameters("Lock all"));
        Assert.All(results, r => Assert.True(r.Success));
        Assert.All(agents, a => Assert.True(a.Platform.Lock.IsShown));

        await teacher.Server.ExecuteManyAsync(agents.Select(a => a.DeviceId), CommandNames.Unlock, null);
        Assert.All(agents, a => Assert.False(a.Platform.Lock.IsShown));
    }

    [Fact]
    public async Task Messages_are_shown_to_the_student_and_empty_ones_are_rejected()
    {
        var (t, a) = await ConnectedAsync();
        Assert.True((await t.Server.SendMessageAsync(a.DeviceId, "Diqqat! 5 daqiqadan keyin topshiriq boshlanadi.")).Success);
        var shown = Assert.Single(a.Platform.Notifier.Shown);
        Assert.Contains("5 daqiqadan", shown.Text, StringComparison.Ordinal);

        var empty = await t.Server.SendMessageAsync(a.DeviceId, "   ");
        Assert.False(empty.Success);
        Assert.Equal(ErrorCodes.InvalidParameters, empty.ErrorCode);
        Assert.Single(a.Platform.Notifier.Shown);
    }

    [Fact]
    public async Task Screenshot_is_saved_with_the_documented_name_and_listed_in_the_history()
    {
        var (t, a) = await ConnectedAsync();
        var (outcome, record) = await t.Server.TakeScreenshotAsync(a.DeviceId);
        Assert.True(outcome.Success, outcome.Message);
        Assert.NotNull(record);
        Assert.True(File.Exists(record.FilePath));
        Assert.Matches(@"^Ali_PC-01_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.jpg$", Path.GetFileName(record.FilePath));
        Assert.StartsWith("JPEG q85 0,0 1280x720", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(record.FilePath)), StringComparison.Ordinal);
        Assert.Single(t.Server.Store.ListScreenshots(10));
    }

    [Fact]
    public async Task Screenshot_of_an_uncapturable_screen_reports_a_clear_error()
    {
        var (t, a) = await ConnectedAsync();
        a.Platform.Screen.Available = false;
        var (outcome, record) = await t.Server.TakeScreenshotAsync(a.DeviceId);
        Assert.False(outcome.Success);
        Assert.Null(record);
    }

    [Fact]
    public async Task A_static_screen_is_sent_once_and_changes_arrive_as_small_regions()
    {
        var (t, a) = await ConnectedAsync();
        var frames = new ConcurrentQueue<ScreenFrameMessage>();
        t.Server.FrameReceived += (_, e) => frames.Enqueue(e.Frame);

        Assert.True((await t.Server.StartStreamAsync(a.DeviceId, 10, 60, 480)).Success);
        Assert.True(await t.WaitForAsync(() => !frames.IsEmpty, Wait));
        await Task.Delay(700);
        Assert.Single(frames); // nothing changed => nothing sent
        var first = frames.First();
        Assert.True(first.KeyFrame);
        Assert.Equal((480, 270), (first.FullWidth, first.FullHeight));
        Assert.Contains("q60", System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(first.ImageBase64)), StringComparison.Ordinal);

        a.Platform.Screen.Animate = true;
        Assert.True(await t.WaitForAsync(() => frames.Count >= 4, Wait));
        var partial = frames.Skip(1).Where(f => !f.KeyFrame).ToList();
        Assert.NotEmpty(partial);
        Assert.All(partial, f => Assert.True((long)f.Width * f.Height < 480L * 270 / 2));
        Assert.True(await t.WaitForAsync(() => t.Server.GetDevice(a.DeviceId)!.Streaming, Wait));

        Assert.True((await t.Server.StopStreamAsync(a.DeviceId)).Success);
        await Task.Delay(300);
        var count = frames.Count;
        await Task.Delay(500);
        Assert.Equal(count, frames.Count);
        Assert.False(a.Services.GetRequiredService<FeatureState>().Streaming);
    }

    [Fact]
    public async Task Stream_parameters_are_clamped_to_safe_limits()
    {
        var (t, a) = await ConnectedAsync();
        var frames = new ConcurrentQueue<ScreenFrameMessage>();
        t.Server.FrameReceived += (_, e) => frames.Enqueue(e.Frame);
        await t.Server.StartStreamAsync(a.DeviceId, 1000, 1000, 100_000);
        Assert.True(await t.WaitForAsync(() => !frames.IsEmpty, Wait));
        Assert.Equal(1280, frames.First().FullWidth); // limited to the 3840 cap, native screen is narrower
        Assert.Contains("q95", System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(frames.First().ImageBase64)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remote_control_moves_the_mouse_and_types_only_while_active()
    {
        var (t, a) = await ConnectedAsync();
        await t.Server.SendMouseAsync(a.DeviceId, new MouseEventMessage(MouseAction.Move, 0.5, 0.5, MouseButtonKind.None, 0)); // not started yet
        await Task.Delay(300);
        Assert.Empty(a.Platform.Input.Mouse);

        Assert.True((await t.Server.StartRemoteControlAsync(a.DeviceId)).Success);
        Assert.True(await t.WaitForAsync(() => t.Server.GetDevice(a.DeviceId)!.RemoteControlActive, Wait));
        await t.Server.SendMouseAsync(a.DeviceId, new MouseEventMessage(MouseAction.Down, 0.1, 0.9, MouseButtonKind.Left, 0));
        await t.Server.SendMouseAsync(a.DeviceId, new MouseEventMessage(MouseAction.Up, 0.1, 0.9, MouseButtonKind.Left, 0));
        await t.Server.SendKeyboardAsync(a.DeviceId, new KeyboardEventMessage(0x41, true, false));
        await t.Server.SendKeyboardAsync(a.DeviceId, new KeyboardEventMessage(0x41, false, false));
        Assert.True(await t.WaitForAsync(() => a.Platform.Input.Mouse.Count == 2 && a.Platform.Input.Keys.Count == 2, Wait));
        Assert.Equal(MouseAction.Down, a.Platform.Input.Mouse.First().Action);
        Assert.Equal(0x41, a.Platform.Input.Keys.First().VirtualKey);

        Assert.True((await t.Server.StopRemoteControlAsync(a.DeviceId)).Success);
        Assert.True(await t.WaitForAsync(() => !t.Server.GetDevice(a.DeviceId)!.RemoteControlActive, Wait));
        await t.Server.SendKeyboardAsync(a.DeviceId, new KeyboardEventMessage(0x42, true, false));
        await Task.Delay(300);
        Assert.Equal(2, a.Platform.Input.Keys.Count);
    }

    [Fact]
    public async Task Teacher_screen_is_shown_updated_and_closed()
    {
        var (t, a) = await ConnectedAsync();
        var frame = new ScreenFrameMessage(1, 640, 360, 0, 0, 640, 360, true, "jpeg", Convert.ToBase64String([1, 2, 3]));

        Assert.Equal(1, await t.Server.SendTeacherFrameAsync([a.DeviceId], frame)); // sent, but ignored: not started
        await Task.Delay(300);
        Assert.Empty(a.Platform.Viewer.Frames);

        Assert.True((await t.Server.ExecuteAsync(a.DeviceId, CommandNames.StartTeacherScreen, new StartTeacherScreenParameters("Dars"))).Success);
        Assert.True(a.Platform.Viewer.IsOpen);
        Assert.Equal("Dars", a.Platform.Viewer.Title);
        await t.Server.SendTeacherFrameAsync([a.DeviceId], frame with { Sequence = 2 });
        await t.Server.SendTeacherFrameAsync([a.DeviceId], frame with { Sequence = 3, Width = 99_999 }); // impossible dimensions
        Assert.True(await t.WaitForAsync(() => a.Platform.Viewer.Frames.Count == 1, Wait));
        Assert.Equal(2, a.Platform.Viewer.Frames.Single().Sequence);

        Assert.True((await t.Server.ExecuteAsync(a.DeviceId, CommandNames.StopTeacherScreen, null)).Success);
        Assert.False(a.Platform.Viewer.IsOpen);
    }

    [Fact]
    public async Task Restart_and_shutdown_always_give_the_student_a_warning_period()
    {
        var (t, a) = await ConnectedAsync();
        Assert.True((await t.Server.RestartAsync(a.DeviceId, 0)).Success);
        Assert.True((await t.Server.ShutdownAsync(a.DeviceId, 99_999)).Success);
        Assert.True((await t.Server.RestartAsync(a.DeviceId, 60)).Success);
        Assert.Equal(["restart:5", "shutdown:300", "restart:60"], a.Platform.System.Calls.ToArray());
    }

    [Fact]
    public async Task Applications_can_be_started_and_stopped_with_validated_parameters()
    {
        var (t, a) = await ConnectedAsync();
        Assert.True((await t.Server.StartApplicationAsync(a.DeviceId, "notepad", "notes.txt")).Success);
        Assert.False((await t.Server.StartApplicationAsync(a.DeviceId, "missing-app")).Success);
        var bad = await t.Server.StartApplicationAsync(a.DeviceId, "x\ny");
        Assert.Equal(ErrorCodes.InvalidParameters, bad.ErrorCode);

        Assert.True((await t.Server.StopApplicationAsync(a.DeviceId, "notepad")).Success);
        Assert.Equal(ErrorCodes.InvalidParameters, (await t.Server.StopApplicationAsync(a.DeviceId, @"..\evil")).ErrorCode);
        a.Platform.System.StopResult = 0;
        Assert.False((await t.Server.StopApplicationAsync(a.DeviceId, "notrunning")).Success);
    }

    [Fact]
    public async Task Commands_the_student_has_disabled_are_refused()
    {
        var (t, a) = await ConnectedAsync(agent: o => o.Configure = c => c.EnabledCommands = [CommandNames.Ping, CommandNames.GetStatus, CommandNames.GetDeviceInfo]);
        var r = await t.Server.LockAsync(a.DeviceId, "x");
        Assert.False(r.Success);
        Assert.Equal(ErrorCodes.CommandDisabled, r.ErrorCode);
        Assert.False(a.Platform.Lock.IsShown);
        Assert.True((await t.Server.ExecuteAsync(a.DeviceId, CommandNames.Ping, null)).Success);
    }

    [Fact]
    public async Task Everything_the_teacher_switched_on_stops_when_the_connection_is_lost()
    {
        var (t, a) = await ConnectedAsync();
        var state = a.Services.GetRequiredService<FeatureState>();
        await t.Server.StartStreamAsync(a.DeviceId, 5, 50, 480);
        await t.Server.StartRemoteControlAsync(a.DeviceId);
        await t.Server.ExecuteAsync(a.DeviceId, CommandNames.StartTeacherScreen, new StartTeacherScreenParameters(null));
        Assert.True(state.Streaming && state.RemoteControl && state.TeacherScreen);

        await t.StopAsync(); // the Teacher PC goes away

        Assert.True(await t.WaitForAsync(() => !state.Streaming && !state.RemoteControl && !state.TeacherScreen, Wait));
        Assert.False(a.Platform.Viewer.IsOpen);
    }

    [Fact]
    public async Task A_lock_is_released_automatically_if_the_teacher_never_comes_back()
    {
        var (t, a) = await ConnectedAsync(o => o.AutoUnlockSeconds = 1);
        await t.Server.LockAsync(a.DeviceId, "x");
        Assert.True(a.Platform.Lock.IsShown);

        await t.StopAsync();

        Assert.True(await t.WaitForAsync(() => !a.Platform.Lock.IsShown, Wait));
        Assert.False(a.Services.GetRequiredService<FeatureState>().Locked);
    }

    [Fact]
    public async Task A_lock_stays_when_the_teacher_returns_before_the_safety_timeout()
    {
        var (t, a) = await ConnectedAsync(o => o.AutoUnlockSeconds = 5);
        await t.Server.LockAsync(a.DeviceId, "x");
        t.DropConnection(a.DeviceId); // brief network blip
        Assert.True(await t.WaitForAsync(() => t.Server.GetDevice(a.DeviceId)!.Online && t.Server.GetDevice(a.DeviceId)!.Computer.Status == RegistrationState.Approved, Wait));
        await Task.Delay(1500);
        Assert.True(a.Platform.Lock.IsShown);
    }

    [Fact]
    public async Task Status_updates_carry_cpu_ram_and_ping_to_the_teacher_grid()
    {
        var (t, a) = await ConnectedAsync();
        Assert.True(await t.WaitForAsync(() => t.Server.GetDevice(a.DeviceId)!.Status is { CpuPercent: 17 }, Wait));
        var status = t.Server.GetDevice(a.DeviceId)!.Status!;
        Assert.Equal(8L * 1024 * 1024 * 1024, status.RamTotalBytes);
        Assert.True(await t.WaitForAsync(() => t.Server.GetDevice(a.DeviceId)!.PingMilliseconds >= 0, Wait));
    }

    [Fact]
    public async Task Blocked_applications_are_closed_repeatedly_shown_to_the_student_and_released_with_the_session()
    {
        var (t, a) = await ConnectedAsync();
        var state = a.Services.GetRequiredService<FeatureState>();
        a.Platform.System.StopResult = 0;

        Assert.True((await t.Server.BlockApplicationAsync(a.DeviceId, "notepad.exe")).Success);
        Assert.Equal(["notepad"], state.BlockedApplications);
        Assert.True(await t.WaitForAsync(() => a.Platform.System.StopCalls >= 2, Wait), "the blocked name must be enforced repeatedly");
        var main = new ViewModels.MainViewModel(a.Services.GetRequiredService<Services.IAgentStatusStore>(), a.Services.GetRequiredService<Services.IDeviceInfoService>(),
            a.Settings, new ImmediateDispatcher(), new NoWindows(), state);
        Assert.Contains("notepad", main.ActivityText, StringComparison.Ordinal);
        Assert.True(await t.WaitForAsync(() => t.Server.GetDevice(a.DeviceId)!.Status?.BlockedApplications == 1, Wait));

        Assert.True((await t.Server.UnblockApplicationAsync(a.DeviceId, "notepad")).Success);
        Assert.Empty(state.BlockedApplications);
        var calls = a.Platform.System.StopCalls;
        await Task.Delay(2600);
        Assert.Equal(calls, a.Platform.System.StopCalls); // no longer enforced

        await t.Server.BlockApplicationAsync(a.DeviceId, "calc");
        Assert.Equal(ErrorCodes.InvalidParameters, (await t.Server.BlockApplicationAsync(a.DeviceId, @"..\x")).ErrorCode);
        await t.StopAsync(); // Teacher disappears: restrictions end with the session
        Assert.True(await t.WaitForAsync(() => state.BlockedApplications.Count == 0, Wait));
    }

    [Fact]
    public async Task A_changed_boot_time_is_recorded_as_a_restart_of_the_computer()
    {
        var (t, a) = await ConnectedAsync();
        Assert.True(await t.WaitForAsync(() => t.Server.Store.GetSetting($"boot.{a.DeviceId}") is not null, Wait));
        await t.Server.LockAsync(a.DeviceId, "x"); // forces a status update
        await t.Server.UnlockAsync(a.DeviceId);
        Assert.DoesNotContain(t.Server.Store.QueryLogs(100), l => l.Action == "Computer restarted"); // same boot time: nothing

        a.Platform.Metrics.BootTime += 3600; // Windows was restarted an hour later
        await t.Server.LockAsync(a.DeviceId, "x");
        Assert.True(await t.WaitForAsync(() => t.Server.Store.QueryLogs(100).Any(l => l.Action == "Computer restarted"), Wait));
    }

    private sealed class NoWindows : ViewModels.IWindowService
    {
        public void ShowMain() { }
        public void ShowSettings(bool aboutTab = false) { }
        public void ShowRegistration() { }
    }

    [Fact]
    public async Task Every_command_in_the_protocol_has_a_handler_on_the_student()
    {
        var (_, a) = await ConnectedAsync();
        var names = a.Services.GetServices<ICommandHandler>().Select(h => h.Name).ToHashSet();
        foreach (var command in CommandNames.All) Assert.Contains(command, names);
        Assert.All(a.Services.GetServices<ICommandHandler>(), h => Assert.True(h.IsImplemented, h.Name));
    }
}
