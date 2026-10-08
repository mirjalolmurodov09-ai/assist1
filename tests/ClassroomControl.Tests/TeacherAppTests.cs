using System.Text.RegularExpressions;
using ClassroomControl.ClassroomServer;
using ClassroomControl.ClassroomServer.Security;
using ClassroomControl.StudentAgent.Models;
using ClassroomControl.TeacherApp.Localization;
using ClassroomControl.TeacherApp.Services;
using ClassroomControl.TeacherApp.ViewModels;
using ClassroomControl.TestKit;
using Xunit;
using Server = ClassroomControl.ClassroomServer.ClassroomServer;

namespace ClassroomControl.StudentAgent.Tests;

public sealed class LocalizationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClassroomControl.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Every_language_has_exactly_the_same_keys()
    {
        var uz = Loc.Table(Loc.Uzbek).Keys.ToHashSet();
        Assert.Equal(uz, Loc.Table(Loc.English).Keys.ToHashSet());
        Assert.Equal(uz, Loc.Table(Loc.Russian).Keys.ToHashSet());
        Assert.True(uz.Count > 150);
    }

    [Fact]
    public void No_translation_is_empty_and_placeholders_match_across_languages()
    {
        foreach (var (key, uz) in Loc.Table(Loc.Uzbek))
        {
            foreach (var language in new[] { Loc.English, Loc.Russian })
            {
                var text = Loc.Table(language)[key];
                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}:{key} is empty");
                Assert.Equal(Placeholders(uz), Placeholders(text));
            }
        }
    }

    private static string Placeholders(string text) => string.Join(',', Regex.Matches(text, @"\{\d\}").Select(m => m.Value).Order());

    [Fact]
    public void Every_key_used_in_the_source_and_the_views_exists()
    {
        var root = RepoRoot();
        var known = Loc.Table(Loc.Uzbek).Keys.ToHashSet();
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                                 && f.Contains("TeacherApp", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains("Strings.", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"l:Loc ([\w\.]+)\}")) used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, "\"([A-Z][A-Za-z]*\\.[A-Z][A-Za-z]*)\"")) used.Add(m.Groups[1].Value);
        }
        used.RemoveWhere(k => !k.Contains('.', StringComparison.Ordinal) || k.StartsWith("Server.", StringComparison.Ordinal));
        var missing = used.Where(k => !known.Contains(k)).ToList();
        var unused = known.Where(k => !used.Contains(k) && k != "Main.Title").ToList();
        Assert.True(missing.Count == 0, "keys used but not translated: " + string.Join(", ", missing));
        Assert.True(unused.Count == 0, "translations nobody uses: " + string.Join(", ", unused));
    }

    [Fact]
    public void Switching_language_changes_texts_and_notifies_bindings()
    {
        var loc = Loc.Instance;
        var changed = new List<string?>();
        loc.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        try
        {
            loc.Language = Loc.English;
            Assert.Equal("Lock", loc["Action.Lock"]);
            loc.Language = Loc.Russian;
            Assert.Equal("Заблокировать", loc["Action.Lock"]);
            loc.Language = Loc.Uzbek;
            Assert.Equal("Bloklash", loc["Action.Lock"]);
            Assert.Contains("Item[]", changed);
            Assert.Equal("Missing.Key", loc["Missing.Key"]); // a missing key is visible, not an exception
            loc.Language = "xx";
            Assert.Equal(Loc.Uzbek, loc.Language);
        }
        finally
        {
            loc.Language = Loc.Uzbek;
        }
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public bool Confirm { get; set; } = true;
    public Queue<string?> PromptAnswers { get; } = new();
    public List<string> Prompts { get; } = [];
    public List<(ComputerViewModel Computer, bool Remote)> FullScreens { get; } = [];
    public List<SettingsTab> SettingsOpened { get; } = [];
    public int Screenshots, Abouts, AddComputers;
    public string? OpenedFile;

    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Confirm);

    public Task<string?> PromptAsync(string title, string label, string initial, bool multiline)
    {
        Prompts.Add(label);
        return Task.FromResult(PromptAnswers.Count > 0 ? PromptAnswers.Dequeue() : initial);
    }

    public void Info(string title, string message) { }
    public void ShowFullScreen(ComputerViewModel computer, bool withRemoteControl) => FullScreens.Add((computer, withRemoteControl));
    public void ShowSettings(SettingsTab tab) => SettingsOpened.Add(tab);
    public void ShowScreenshots() => Screenshots++;
    public void ShowAbout() => Abouts++;
    public void ShowAddComputer() => AddComputers++;
    public void OpenFile(string path) => OpenedFile = path;
}

internal sealed class FakeTheme : IThemeService
{
    public string? Applied;
    public void Apply(string theme) => Applied = theme;
}

/// <summary>The Teacher application's view-models against the real server and real Student Agent services.</summary>
public sealed class TeacherAppTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly string _code = TestSupport.NewCode();
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<IDisposable> _sync = [];
    private TestTeacher _teacher = null!;
    private FakeDialogs _dialogs = null!;
    private MonitoringService _monitoring = null!;
    private TeacherScreenShareService _share = null!;
    private FakePlatform _teacherScreen = null!;

    public async Task InitializeAsync()
    {
        _teacher = new TestTeacher(new TestTeacherOptions { ClassroomCode = _code, Approval = ApprovalMode.Manual });
        _teacher.Start();
        _disposables.Add(_teacher);
        _dialogs = new FakeDialogs();
        _monitoring = new MonitoringService(_teacher.Server);
        _sync.Add(_monitoring);
        _teacherScreen = new FakePlatform();
        _share = new TeacherScreenShareService(_teacher.Server, _teacherScreen.Screen, _teacherScreen.Encoder);
        _disposables.Add(_share);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sync) s.Dispose();
        foreach (var d in Enumerable.Reverse(_disposables)) await d.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Loc.Instance.Language = Loc.Uzbek;
    }

    private Server Srv => _teacher.Server;

    private MainViewModel NewMain()
    {
        var session = new TeacherSession(Srv);
        session.SignIn(Srv.Store.ListComputers(0).Count >= 0 ? new UserRecord(1, "ustoz", "x", Roles.Admin, DateTimeOffset.UtcNow, null, false) : null!);
        var vm = new MainViewModel(Srv, _monitoring, _share, _dialogs, new ImmediateDispatcher(), session);
        _sync.Add(vm);
        return vm;
    }

    private async Task<HeadlessAgent> AgentAsync(string name, bool approve = true)
    {
        var agent = new HeadlessAgent(new HeadlessAgentOptions
        {
            ClassroomCode = _code, ComputerName = name, StudentName = $"Student {name}", DiscoveryPort = _teacher.DiscoveryPort,
            TeacherAddress = "127.0.0.1", TeacherPort = _teacher.TcpPort,
        });
        _disposables.Add(agent);
        await agent.StartAsync();
        Assert.True(await _teacher.WaitForAsync(() => _teacher.Server.GetDevice(agent.DeviceId) is { Online: true }, Wait));
        if (approve)
        {
            await Srv.ApproveAsync(agent.DeviceId);
            Assert.True(await agent.WaitForStateAsync(ConnectionState.Connected, Wait));
        }
        return agent;
    }

    [Fact]
    public async Task New_computers_wait_for_approval_then_appear_in_the_grid_with_counts()
    {
        var main = NewMain();
        var a = await AgentAsync("PC-01", approve: false);
        Assert.True(await _teacher.WaitForAsync(() => main.PendingCount == 1, Wait));
        Assert.True(main.HasPending);
        Assert.Empty(main.Computers); // the grid shows approved computers only

        main.SelectedFilter = main.Filters.Single(f => f.Key == GroupFilter.PendingKey);
        Assert.Single(main.Computers);
        main.SelectOnly(main.Computers[0]);
        main.ApproveCommand.Execute(null);
        Assert.True(await a.WaitForStateAsync(ConnectionState.Connected, Wait));

        main.SelectedFilter = main.Filters[0];
        Assert.True(await _teacher.WaitForAsync(() => main.OnlineCount == 1 && main.TotalCount == 1 && main.PendingCount == 0, Wait));
        Assert.Single(main.Computers);
        Assert.Equal("PC-01", main.Computers[0].Title);
        Assert.Equal("Student PC-01", main.Computers[0].StudentName);
        Assert.Equal("Onlayn", main.Computers[0].StatusText);
    }

    [Fact]
    public async Task Offline_and_online_counts_follow_connections_and_filters()
    {
        var main = NewMain();
        var a = await AgentAsync("PC-01");
        var b = await AgentAsync("PC-02");
        Assert.True(await _teacher.WaitForAsync(() => main.OnlineCount == 2, Wait));

        await b.StopAsync();
        Assert.True(await _teacher.WaitForAsync(() => main.OnlineCount == 1 && main.OfflineCount == 1, Wait));
        main.SelectedFilter = main.Filters.Single(f => f.Key == GroupFilter.OfflineKey);
        Assert.Equal("PC-02", Assert.Single(main.Computers).Title);
        Assert.Equal("Oflayn", main.Computers[0].StatusText);
        Assert.Equal("—", main.Computers[0].PingText);
        main.SelectedFilter = main.Filters.Single(f => f.Key == GroupFilter.OnlineKey);
        Assert.Equal(a.DeviceId, Assert.Single(main.Computers).DeviceId);
    }

    [Fact]
    public async Task Lock_all_and_unlock_all_reach_every_online_computer_and_report_the_result()
    {
        var main = NewMain();
        var agents = new[] { await AgentAsync("PC-01"), await AgentAsync("PC-02"), await AgentAsync("PC-03") };
        Assert.True(await _teacher.WaitForAsync(() => main.OnlineCount == 3, Wait));
        _dialogs.PromptAnswers.Enqueue("Dars boshlanmoqda");

        await ((AsyncRelayCommand)main.LockAllCommand).ExecuteAsync();

        Assert.All(agents, a => Assert.Equal("Dars boshlanmoqda", a.Platform.Lock.Message));
        Assert.Contains("3/3", main.StatusMessage, StringComparison.Ordinal);

        await ((AsyncRelayCommand)main.UnlockAllCommand).ExecuteAsync();
        Assert.All(agents, a => Assert.False(a.Platform.Lock.IsShown));
    }

    [Fact]
    public async Task Selection_commands_only_touch_the_selected_computers()
    {
        var main = NewMain();
        var a = await AgentAsync("PC-01");
        var b = await AgentAsync("PC-02");
        Assert.True(await _teacher.WaitForAsync(() => main.Computers.Count == 2, Wait));
        Assert.False(main.LockSelectedCommand.CanExecute(null)); // nothing selected

        main.SelectOnly(main.Computers.Single(c => c.DeviceId == a.DeviceId));
        Assert.True(main.LockSelectedCommand.CanExecute(null));
        _dialogs.PromptAnswers.Enqueue("faqat bittasi");
        await ((AsyncRelayCommand)main.LockSelectedCommand).ExecuteAsync();

        Assert.True(a.Platform.Lock.IsShown);
        Assert.False(b.Platform.Lock.IsShown);

        main.ToggleSelection(main.Computers.Single(c => c.DeviceId == b.DeviceId));
        Assert.Equal(2, main.Selected().Count);
        main.ClearSelection();
        Assert.Empty(main.Selected());
        main.SelectAll();
        Assert.Equal(2, main.Selected().Count);
    }

    [Fact]
    public async Task Cancelling_the_lock_prompt_locks_nothing()
    {
        var main = NewMain();
        var a = await AgentAsync("PC-01");
        Assert.True(await _teacher.WaitForAsync(() => main.Computers.Count == 1, Wait));
        main.SelectOnly(main.Computers[0]);
        _dialogs.PromptAnswers.Enqueue(null);
        await ((AsyncRelayCommand)main.LockSelectedCommand).ExecuteAsync();
        Assert.False(a.Platform.Lock.IsShown);
    }

    [Fact]
    public async Task Messages_screenshots_and_applications_work_from_the_view_model()
    {
        var main = NewMain();
        var a = await AgentAsync("PC-01");
        Assert.True(await _teacher.WaitForAsync(() => main.Computers.Count == 1, Wait));
        main.SelectOnly(main.Computers[0]);

        _dialogs.PromptAnswers.Enqueue("5 daqiqa");
        await ((AsyncRelayCommand)main.MessageSelectedCommand).ExecuteAsync();
        Assert.Equal("5 daqiqa", Assert.Single(a.Platform.Notifier.Shown).Text);

        await ((AsyncRelayCommand)main.ScreenshotCommand).ExecuteAsync();
        Assert.NotNull(main.LastScreenshotPath);
        Assert.True(File.Exists(main.LastScreenshotPath));

        _dialogs.PromptAnswers.Enqueue("calc");
        await ((AsyncRelayCommand)main.OpenApplicationCommand).ExecuteAsync();
        Assert.Contains("start:calc:", a.Platform.System.Calls);
        _dialogs.PromptAnswers.Enqueue("calc");
        await ((AsyncRelayCommand)main.CloseApplicationCommand).ExecuteAsync();
        Assert.Contains("stop:calc", a.Platform.System.Calls);
    }

    [Fact]
    public async Task A_message_to_everyone_reaches_every_online_computer()
    {
        var main = NewMain();
        var agents = new[] { await AgentAsync("PC-01"), await AgentAsync("PC-02"), await AgentAsync("PC-03") };
        Assert.True(await _teacher.WaitForAsync(() => main.OnlineCount == 3, Wait));
        _dialogs.PromptAnswers.Enqueue("Hammaga: 5 daqiqadan keyin topshiriq");
        await ((AsyncRelayCommand)main.MessageAllCommand).ExecuteAsync();
        Assert.All(agents, a => Assert.Contains("5 daqiqadan", Assert.Single(a.Platform.Notifier.Shown).Text, StringComparison.Ordinal));
        Assert.Contains("3/3", main.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_and_shutdown_ask_for_confirmation_first()
    {
        var main = NewMain();
        var a = await AgentAsync("PC-01");
        Assert.True(await _teacher.WaitForAsync(() => main.Computers.Count == 1, Wait));
        main.SelectOnly(main.Computers[0]);

        _dialogs.Confirm = false;
        await ((AsyncRelayCommand)main.RestartCommand).ExecuteAsync();
        await ((AsyncRelayCommand)main.ShutdownCommand).ExecuteAsync();
        Assert.Empty(a.Platform.System.Calls);

        _dialogs.Confirm = true;
        await ((AsyncRelayCommand)main.RestartCommand).ExecuteAsync();
        Assert.Single(a.Platform.System.Calls);
        Assert.StartsWith("restart:", a.Platform.System.Calls.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_targets_are_reported_instead_of_failing_silently()
    {
        var main = NewMain();
        var a = await AgentAsync("PC-01");
        Assert.True(await _teacher.WaitForAsync(() => main.Computers.Count == 1, Wait));
        main.SelectOnly(main.Computers[0]);
        await a.StopAsync();
        Assert.True(await _teacher.WaitForAsync(() => !main.Computers[0].IsOnline, Wait));

        await ((AsyncRelayCommand)main.UnlockSelectedCommand).ExecuteAsync();
        Assert.Equal(Loc.Instance["Error.NoTarget"], main.StatusMessage);
    }

    [Fact]
    public async Task Groups_filter_the_grid_and_computers_can_be_moved_between_them()
    {
        var main = NewMain();
        var a = await AgentAsync("PC-01");
        await AgentAsync("PC-02");
        Assert.True(await _teacher.WaitForAsync(() => main.Computers.Count == 2, Wait));
        var group = Srv.AddGroup("Python");
        main.ReloadGroupsFromServer();
        Assert.Contains(main.Filters, f => f.GroupId == group.Id);

        main.SelectOnly(main.Computers.Single(c => c.DeviceId == a.DeviceId));
        main.MoveToGroupCommand.Execute(group);
        main.SelectedFilter = main.Filters.Single(f => f.GroupId == group.Id);
        Assert.Equal(a.DeviceId, Assert.Single(main.Computers).DeviceId);
        Assert.Equal("Python", main.Computers[0].GroupName);
        Assert.Equal(1, main.SelectedFilter.Count);

        main.MoveToGroupCommand.Execute(null);
        Assert.Empty(main.Computers);
    }

    [Fact]
    public async Task Rename_and_remove_update_the_registry()
    {
        var main = NewMain();
        var a = await AgentAsync("PC-01");
        Assert.True(await _teacher.WaitForAsync(() => main.Computers.Count == 1, Wait));
        main.SelectOnly(main.Computers[0]);

        _dialogs.PromptAnswers.Enqueue("Birinchi parta");
        await ((AsyncRelayCommand)main.RenameCommand).ExecuteAsync();
        Assert.Equal("Birinchi parta", main.Computers[0].Title);

        await ((AsyncRelayCommand)main.RemoveCommand).ExecuteAsync();
        Assert.True(await _teacher.WaitForAsync(() => main.Computers.Count == 0 || main.Computers[0].IsPending, Wait));
        Assert.True(await a.WaitForStateAsync(ConnectionState.WaitingForApproval, Wait)); // must be approved again
    }

    [Fact]
    public async Task Double_click_actions_open_the_full_screen_view_and_remote_control()
    {
        var main = NewMain();
        await AgentAsync("PC-01");
        Assert.True(await _teacher.WaitForAsync(() => main.Computers.Count == 1, Wait));
        main.SelectOnly(main.Computers[0]);
        main.ViewScreenCommand.Execute(null);
        main.RemoteControlCommand.Execute(null);
        Assert.Equal([false, true], _dialogs.FullScreens.Select(f => f.Remote));
        main.AboutCommand.Execute(null);
        main.ScreenshotsCommand.Execute(null);
        main.SettingsCommand.Execute(null);
        main.LogsCommand.Execute(null);
        main.AddComputerCommand.Execute(null);
        Assert.Equal((1, 1, 1), (_dialogs.Abouts, _dialogs.Screenshots, _dialogs.AddComputers));
        Assert.Equal([SettingsTab.General, SettingsTab.Logs], _dialogs.SettingsOpened);
    }

    [Fact]
    public async Task Monitoring_streams_thumbnails_and_full_screen_focus_raises_the_quality()
    {
        var main = NewMain();
        var a = await AgentAsync("PC-01");
        var frames = new System.Collections.Concurrent.ConcurrentQueue<FrameReceivedEventArgs>();
        _monitoring.FrameArrived += (_, e) => frames.Enqueue(e);
        await _monitoring.StartAllAsync();
        Assert.True(await _teacher.WaitForAsync(() => !frames.IsEmpty, Wait));
        Assert.Contains("q50", System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(frames.First().Frame.ImageBase64)), StringComparison.Ordinal);
        Assert.Equal(480, frames.First().Frame.FullWidth);

        var vm = main.Find(a.DeviceId)!;
        using var full = new FullScreenViewModel(vm, Srv, _monitoring);
        frames.Clear();
        await full.OpenAsync();
        Assert.True(await _teacher.WaitForAsync(() => frames.Any(f => f.Frame.FullWidth == 1280 && f.Frame.KeyFrame), Wait));
        await full.CloseAsync();
        frames.Clear();
        Assert.True(await _teacher.WaitForAsync(() => frames.Any(f => f.Frame.FullWidth == 480), Wait));

        await _monitoring.PauseAsync();
        Assert.True(await _teacher.WaitForAsync(() => !Srv.GetDevice(a.DeviceId)!.Streaming, Wait));
        Assert.False(_monitoring.Enabled);
    }

    [Fact]
    public async Task New_computers_start_streaming_automatically_while_monitoring_is_on()
    {
        await _monitoring.StartAllAsync();
        var a = await AgentAsync("PC-01");
        Assert.True(await _teacher.WaitForAsync(() => Srv.GetDevice(a.DeviceId)!.Streaming, Wait));
    }

    [Fact]
    public async Task Full_screen_remote_control_forwards_input_only_while_active_and_ends_with_the_window()
    {
        var main = NewMain();
        var a = await AgentAsync("PC-01");
        Assert.True(await _teacher.WaitForAsync(() => main.Computers.Count == 1, Wait));
        using var full = new FullScreenViewModel(main.Computers[0], Srv, _monitoring);

        await full.MouseMoveAsync(0.5, 0.5);
        await full.KeyAsync(65, true, false);
        await Task.Delay(300);
        Assert.Empty(a.Platform.Input.Keys); // remote control not on yet

        await ((AsyncRelayCommand)full.ToggleRemoteCommand).ExecuteAsync();
        Assert.True(full.RemoteActive);
        Assert.True(await _teacher.WaitForAsync(() => Srv.GetDevice(a.DeviceId)!.RemoteControlActive, Wait));
        await full.MouseButtonAsync(0.2, 0.3, MouseButtonKind.Left, true);
        await full.MouseWheelAsync(0.2, 0.3, 120);
        await full.KeyAsync(65, true, false);
        Assert.True(await _teacher.WaitForAsync(() => a.Platform.Input.Keys.Count == 1 && a.Platform.Input.Mouse.Count == 2, Wait));

        await full.CloseAsync();
        Assert.False(full.RemoteActive);
        Assert.True(await _teacher.WaitForAsync(() => !Srv.GetDevice(a.DeviceId)!.RemoteControlActive, Wait));
    }

    [Fact]
    public async Task Teacher_screen_sharing_reaches_selected_computers_and_new_joiners_and_stops_cleanly()
    {
        var main = NewMain();
        var a = await AgentAsync("PC-01");
        Assert.True(await _teacher.WaitForAsync(() => main.Computers.Count == 1, Wait));
        _teacherScreen.Screen.Animate = true;

        await ((AsyncRelayCommand)main.ShareScreenAllCommand).ExecuteAsync();
        Assert.True(main.IsSharing || _share.IsSharing);
        Assert.True(await _teacher.WaitForAsync(() => a.Platform.Viewer.IsOpen && a.Platform.Viewer.Frames.Count >= 3, Wait));
        Assert.True(a.Platform.Viewer.Frames.First().KeyFrame);

        var b = await AgentAsync("PC-02"); // joins while sharing
        Assert.True(await _teacher.WaitForAsync(() => b.Platform.Viewer.IsOpen && b.Platform.Viewer.Frames.Any(f => f.KeyFrame), Wait));

        await ((AsyncRelayCommand)main.ShareScreenAllCommand).ExecuteAsync(); // toggles off
        Assert.False(_share.IsSharing);
        Assert.True(await _teacher.WaitForAsync(() => !a.Platform.Viewer.IsOpen && !b.Platform.Viewer.IsOpen, Wait));
    }

    [Fact]
    public async Task Pausing_monitoring_from_the_top_bar_stops_streams()
    {
        var main = NewMain();
        var a = await AgentAsync("PC-01");
        await _monitoring.StartAllAsync();
        Assert.True(await _teacher.WaitForAsync(() => Srv.GetDevice(a.DeviceId)!.Streaming, Wait));
        await ((AsyncRelayCommand)main.ToggleMonitoringCommand).ExecuteAsync();
        Assert.True(main.IsMonitoringPaused);
        Assert.True(await _teacher.WaitForAsync(() => !Srv.GetDevice(a.DeviceId)!.Streaming, Wait));
        await ((AsyncRelayCommand)main.ToggleMonitoringCommand).ExecuteAsync();
        Assert.False(main.IsMonitoringPaused);
        Assert.True(await _teacher.WaitForAsync(() => Srv.GetDevice(a.DeviceId)!.Streaming, Wait));
    }

    [Fact]
    public async Task Language_change_refreshes_card_texts()
    {
        var main = NewMain();
        await AgentAsync("PC-01");
        Assert.True(await _teacher.WaitForAsync(() => main.Computers.Count == 1, Wait));
        var changed = false;
        main.Computers[0].PropertyChanged += (_, e) => changed |= e.PropertyName == nameof(ComputerViewModel.StatusText);
        Loc.Instance.Language = Loc.English;
        Assert.True(changed);
        Assert.Equal("Online", main.Computers[0].StatusText);
        Assert.Equal("All computers (1)", main.Filters[0].Display);
    }

    [Fact]
    public async Task Add_computer_dialog_lists_pending_computers_and_approves_them()
    {
        await AgentAsync("PC-01", approve: false);
        using var add = new AddComputerViewModel(Srv, new ImmediateDispatcher());
        Assert.True(await _teacher.WaitForAsync(() => add.Pending.Count == 1, Wait));
        Assert.Equal(_code, add.ClassroomCode);
        Assert.False(string.IsNullOrWhiteSpace(add.Instructions));
        add.Selected = add.Pending[0];
        await ((AsyncRelayCommand)add.ApproveCommand).ExecuteAsync();
        Assert.True(await _teacher.WaitForAsync(() => add.Pending.Count == 0, Wait));
    }

    [Fact]
    public async Task Screenshot_history_lists_opens_and_deletes_files()
    {
        var a = await AgentAsync("PC-01");
        var (_, record) = await Srv.TakeScreenshotAsync(a.DeviceId);
        var history = new ScreenshotHistoryViewModel(Srv, _dialogs);
        Assert.Single(history.Items);
        history.Selected = history.Items[0];
        history.OpenCommand.Execute(null);
        Assert.Equal(record!.FilePath, _dialogs.OpenedFile);
        await ((AsyncRelayCommand)history.DeleteCommand).ExecuteAsync();
        Assert.Empty(history.Items);
        Assert.False(File.Exists(record.FilePath));
    }

    [Fact]
    public async Task Settings_are_saved_and_applied_and_groups_users_and_code_can_be_managed()
    {
        var users = new UserService(Srv.Store, TimeProvider.System);
        var admin = users.Create("admin", "Sirli-Parol-1", Roles.Admin);
        var session = new TeacherSession(Srv);
        session.SignIn(admin);
        var theme = new FakeTheme();
        using var settings = new SettingsViewModel(Srv, users, session, _monitoring, _dialogs, new ImmediateDispatcher(), theme);

        settings.MonitoringFps = 5;
        settings.MonitoringQuality = 40;
        settings.RequireAgentActive = true;
        settings.AutoUnlockSeconds = 12;
        settings.IsDarkTheme = true;
        settings.Language = Loc.English;
        await ((AsyncRelayCommand)settings.SaveCommand).ExecuteAsync();
        Assert.Equal(5, Srv.Settings.MonitoringFps);
        Assert.Equal(40, Srv.Settings.MonitoringQuality);
        Assert.True(Srv.Settings.RequireAgentActive);
        Assert.Equal(12, Srv.Settings.AutoUnlockSeconds);
        Assert.Equal("Dark", Srv.Settings.Theme);
        Assert.Equal("en", Srv.Settings.Language);
        Assert.Equal("Dark", theme.Applied);

        settings.TcpPort = 70000;
        await ((AsyncRelayCommand)settings.SaveCommand).ExecuteAsync();
        Assert.Equal(Loc.Instance["Settings.BadPort"], settings.Message);

        _dialogs.PromptAnswers.Enqueue("Web dasturlash");
        await ((AsyncRelayCommand)settings.AddGroupCommand).ExecuteAsync();
        Assert.Equal("Web dasturlash", Assert.Single(settings.Groups).Name);
        settings.SelectedGroup = settings.Groups[0];
        _dialogs.PromptAnswers.Enqueue("Web");
        await ((AsyncRelayCommand)settings.RenameGroupCommand).ExecuteAsync();
        Assert.Equal("Web", settings.Groups[0].Name);
        await ((AsyncRelayCommand)settings.DeleteGroupCommand).ExecuteAsync();
        Assert.Empty(settings.Groups);

        _dialogs.PromptAnswers.Enqueue("yordamchi");
        _dialogs.PromptAnswers.Enqueue("Ikkinchi-Parol-2");
        await ((AsyncRelayCommand)settings.AddUserCommand).ExecuteAsync();
        Assert.Equal(2, settings.Users.Count);
        settings.SelectedUser = settings.Users.Single(u => u.Username == "admin");
        await ((AsyncRelayCommand)settings.DeleteUserCommand).ExecuteAsync(); // last admin: refused with a message
        Assert.Equal(2, settings.Users.Count);
        Assert.Contains("administrator", settings.Message, StringComparison.OrdinalIgnoreCase);

        var oldCode = Srv.ClassroomCodeText;
        await ((AsyncRelayCommand)settings.RegenerateCodeCommand).ExecuteAsync();
        Assert.NotEqual(oldCode, settings.ClassroomCode);
        _dialogs.PromptAnswers.Enqueue("CLASS-ABCD-2026");
        await ((AsyncRelayCommand)settings.SetCodeCommand).ExecuteAsync();
        Assert.Equal("CLASS-ABCD-2026", Srv.ClassroomCodeText);
        _dialogs.PromptAnswers.Enqueue("bad");
        await ((AsyncRelayCommand)settings.SetCodeCommand).ExecuteAsync();
        Assert.Equal("CLASS-ABCD-2026", Srv.ClassroomCodeText);
        Assert.Contains(":", settings.CertificateFingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Teachers_who_are_not_administrators_cannot_manage_users()
    {
        var users = new UserService(Srv.Store, TimeProvider.System);
        var teacher = users.Create("ustoz", "Sirli-Parol-1", Roles.Teacher);
        var session = new TeacherSession(Srv);
        session.SignIn(teacher);
        using var settings = new SettingsViewModel(Srv, users, session, _monitoring, _dialogs, new ImmediateDispatcher(), new FakeTheme());
        Assert.False(settings.IsAdmin);
        Assert.False(settings.AddUserCommand.CanExecute(null));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Logs_view_shows_and_filters_the_audit_trail()
    {
        var a = await AgentAsync("PC-01");
        await Srv.LockAsync(a.DeviceId, "x");
        using var logs = new LogsViewModel(Srv, new ImmediateDispatcher());
        Assert.Contains(logs.Entries, e => e.Action == Shared.Communication.Protocol.CommandNames.Lock && e.Result == "OK");
        logs.Filter = "Lock";
        Assert.All(logs.Entries, e => Assert.Contains("Lock", $"{e.Action}{e.Result}{e.Details}{e.Computer}{e.Teacher}", StringComparison.OrdinalIgnoreCase));
        logs.Filter = "zzz-no-such-entry";
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public void Login_and_first_run_setup_view_models_validate_input()
    {
        var store = new ClassroomStore(Path.Combine(TestSupport.TempDir(), "u.db"));
        var users = new UserService(store, TimeProvider.System);
        var setup = new SetupAdminViewModel(users);
        Assert.Null(setup.Create("Sirli-Parol-1", "boshqa"));
        Assert.Equal(Loc.Instance["Setup.Mismatch"], setup.Error);
        Assert.Null(setup.Create("qisqa", "qisqa"));
        Assert.NotEmpty(setup.Error);
        var admin = setup.Create("Sirli-Parol-1", "Sirli-Parol-1");
        Assert.Equal(Roles.Admin, admin!.Role);

        var login = new LoginViewModel(users) { Username = "admin" };
        Assert.Null(login.TryLogin("noto‘g‘ri"));
        Assert.Equal(Loc.Instance["Login.Wrong"], login.Error);
        Assert.NotNull(login.TryLogin("Sirli-Parol-1"));
        Assert.Equal(string.Empty, login.Error);
        for (var i = 0; i < UserService.MaxFailedAttempts; i++) login.TryLogin("x");
        Assert.Equal(Loc.Instance["Login.Locked"], login.Error);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Audit_log_records_the_signed_in_teacher_for_every_action()
    {
        var main = NewMain();
        var a = await AgentAsync("PC-01");
        Assert.True(await _teacher.WaitForAsync(() => main.Computers.Count == 1, Wait));
        main.SelectOnly(main.Computers[0]);
        _dialogs.PromptAnswers.Enqueue("blok");
        await ((AsyncRelayCommand)main.LockSelectedCommand).ExecuteAsync();
        var entry = Srv.Store.QueryLogs(50, "Lock").First(l => l.Action == "Lock");
        Assert.Equal("ustoz", entry.Teacher);
        Assert.Equal("PC-01", entry.Computer);
        Assert.Equal("OK", entry.Result);
        _ = a;
    }

    [Fact]
    public async Task Runtime_creates_everything_persists_the_language_and_keeps_the_code_across_restarts()
    {
        var dir = TestSupport.TempDir();
        var protector = new AesFileSecretProtector(Path.Combine(dir, "secret.key"));
        string code;
        string fingerprint;
        {
            var runtime = TeacherRuntime.Create(dir, protector, configure: o => { o.BindAddress = System.Net.IPAddress.Loopback; o.TcpPort = 0; o.DiscoveryPort = 0; });
            code = runtime.Server.ClassroomCodeText;
            fingerprint = runtime.Server.CertificateFingerprint;
            runtime.Server.Settings.Language = Loc.English;
            await runtime.DisposeAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var again = TeacherRuntime.Create(dir, protector, configure: o => { o.BindAddress = System.Net.IPAddress.Loopback; o.TcpPort = 0; o.DiscoveryPort = 0; });
        Assert.Equal(code, again.Server.ClassroomCodeText); // students keep working after a Teacher restart
        Assert.Equal(fingerprint, again.Server.CertificateFingerprint); // and their pinned certificate stays valid
        Assert.Equal(Loc.English, Loc.Instance.Language);
        await again.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Loc.Instance.Language = Loc.Uzbek;
    }
}
