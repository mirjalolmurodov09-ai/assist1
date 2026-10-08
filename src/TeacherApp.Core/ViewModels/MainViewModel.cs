using System.Collections.ObjectModel;
using System.Windows.Input;

namespace ClassroomControl.TeacherApp.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string DefaultLockKey = "Lock.DefaultMessage";
    private readonly Server _server;
    private readonly MonitoringService _monitoring;
    private readonly TeacherScreenShareService _share;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly TeacherSession _session;
    private readonly Dictionary<string, ComputerViewModel> _byId = [];
    private GroupFilter _selectedFilter;
    private string _statusMessage = string.Empty;
    private bool _monitoringPaused;
    private bool _isSharing;

    public MainViewModel(Server server, MonitoringService monitoring, TeacherScreenShareService share, IDialogService dialogs, IUiDispatcher ui, TeacherSession session)
    {
        _server = server;
        _monitoring = monitoring;
        _share = share;
        _dialogs = dialogs;
        _ui = ui;
        _session = session;

        Filters.Add(new GroupFilter(GroupFilter.AllKey, "Filter.All", null, null));
        Filters.Add(new GroupFilter(GroupFilter.OnlineKey, "Filter.Online", null, null));
        Filters.Add(new GroupFilter(GroupFilter.OfflineKey, "Filter.Offline", null, null));
        Filters.Add(new GroupFilter(GroupFilter.PendingKey, "Filter.Pending", null, null));
        _selectedFilter = Filters[0];

        LockSelectedCommand = Cmd(() => LockAsync(Selected()), HasSelection);
        UnlockSelectedCommand = Cmd(() => UnlockAsync(Selected()), HasSelection);
        LockAllCommand = Cmd(() => LockAsync(Commandable()));
        UnlockAllCommand = Cmd(() => UnlockAsync(Commandable()));
        MessageSelectedCommand = Cmd(() => SendMessageAsync(Selected()), HasSelection);
        MessageAllCommand = Cmd(() => SendMessageAsync(Commandable()));
        ScreenshotCommand = Cmd(ScreenshotAsync, HasSelection);
        RestartCommand = Cmd(() => PowerAsync(restart: true), HasSelection);
        ShutdownCommand = Cmd(() => PowerAsync(restart: false), HasSelection);
        OpenApplicationCommand = Cmd(OpenApplicationAsync, HasSelection);
        CloseApplicationCommand = Cmd(CloseApplicationAsync, HasSelection);
        BlockApplicationCommand = Cmd(() => BlockAsync(block: true), HasSelection);
        UnblockApplicationCommand = Cmd(() => BlockAsync(block: false), HasSelection);
        ViewScreenCommand = new RelayCommand(() => OpenFullScreen(false), () => Selected().Count == 1);
        RemoteControlCommand = new RelayCommand(() => OpenFullScreen(true), () => Selected().Count == 1);
        ShareScreenSelectedCommand = Cmd(() => ToggleShareAsync(ShareTarget.Selected), () => _isSharing || HasSelection());
        ShareScreenAllCommand = Cmd(() => ToggleShareAsync(ShareTarget.All));
        ShareScreenGroupCommand = Cmd(() => ToggleShareAsync(ShareTarget.Group), () => _isSharing || SelectedFilter.GroupId is not null);
        ToggleMonitoringCommand = Cmd(ToggleMonitoringAsync);
        ApproveCommand = Cmd(() => ApproveAsync(true), () => Selected().Any(c => c.IsPending));
        RejectCommand = Cmd(() => ApproveAsync(false), () => Selected().Any(c => c.IsPending));
        RenameCommand = Cmd(RenameAsync, () => Selected().Count == 1);
        RemoveCommand = Cmd(RemoveAsync, HasSelection);
        SelectAllCommand = new RelayCommand(SelectAll);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        AddComputerCommand = new RelayCommand(() => _dialogs.ShowAddComputer());
        SettingsCommand = new RelayCommand(() => _dialogs.ShowSettings(SettingsTab.General));
        LogsCommand = new RelayCommand(() => _dialogs.ShowSettings(SettingsTab.Logs));
        ScreenshotsCommand = new RelayCommand(() => _dialogs.ShowScreenshots());
        AboutCommand = new RelayCommand(() => _dialogs.ShowAbout());
        MoveToGroupCommand = new RelayCommand(p => MoveToGroup(p as GroupRecord), _ => HasSelection());

        _server.DeviceChanged += OnDeviceChanged;
        _server.DeviceRemoved += OnDeviceRemoved;
        _share.SharingChanged += OnSharingChanged;
        Loc.Instance.PropertyChanged += OnLanguageChanged;
        Reload();
    }

    public ObservableCollection<ComputerViewModel> Computers { get; } = [];
    public ObservableCollection<GroupFilter> Filters { get; } = [];
    public ObservableCollection<GroupRecord> Groups { get; } = [];

    public ICommand LockSelectedCommand { get; }
    public ICommand UnlockSelectedCommand { get; }
    public ICommand LockAllCommand { get; }
    public ICommand UnlockAllCommand { get; }
    public ICommand MessageSelectedCommand { get; }
    public ICommand MessageAllCommand { get; }
    public ICommand ScreenshotCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand ShutdownCommand { get; }
    public ICommand OpenApplicationCommand { get; }
    public ICommand CloseApplicationCommand { get; }
    public ICommand BlockApplicationCommand { get; }
    public ICommand UnblockApplicationCommand { get; }
    public ICommand ViewScreenCommand { get; }
    public ICommand RemoteControlCommand { get; }
    public ICommand ShareScreenSelectedCommand { get; }
    public ICommand ShareScreenAllCommand { get; }
    public ICommand ShareScreenGroupCommand { get; }
    public ICommand ToggleMonitoringCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand AddComputerCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand LogsCommand { get; }
    public ICommand ScreenshotsCommand { get; }
    public ICommand AboutCommand { get; }
    public ICommand MoveToGroupCommand { get; }

    public string ClassroomName => _server.Classroom.Name;
    public string ClassroomCode => _server.ClassroomCodeText;
    public string TeacherUser => _session.User?.Username ?? string.Empty;
    public string TeacherStatus => Loc.Instance["Teacher.Ready"];
    public int TotalCount => _byId.Values.Count(c => c.IsApproved);
    public int OnlineCount => _byId.Values.Count(c => c.IsOnline && c.IsApproved);
    public int OfflineCount => _byId.Values.Count(c => !c.IsOnline && c.IsApproved);
    public int PendingCount => _byId.Values.Count(c => c.IsPending);
    public bool IsMonitoringPaused => _monitoringPaused;
    public bool IsSharing => _isSharing;
    public bool HasPending => PendingCount > 0;

    public GroupFilter SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (value is null || !Set(ref _selectedFilter, value)) return;
            ApplyFilter();
            RaiseCommands();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    // ---------------------------------------------------------------- selection

    public IReadOnlyList<ComputerViewModel> Selected() => [.. Computers.Where(c => c.IsSelected)];
    private IReadOnlyList<ComputerViewModel> Commandable() => [.. _byId.Values.Where(c => c.CanBeCommanded)];
    private bool HasSelection() => Computers.Any(c => c.IsSelected);

    public void SelectOnly(ComputerViewModel computer)
    {
        foreach (var c in Computers) c.IsSelected = ReferenceEquals(c, computer);
        RaiseCommands();
    }

    public void ToggleSelection(ComputerViewModel computer)
    {
        computer.IsSelected = !computer.IsSelected;
        RaiseCommands();
    }

    public void SelectAll()
    {
        foreach (var c in Computers) c.IsSelected = true;
        RaiseCommands();
    }

    public void ClearSelection()
    {
        foreach (var c in Computers) c.IsSelected = false;
        RaiseCommands();
    }

    public ComputerViewModel? Find(string deviceId) => _byId.GetValueOrDefault(deviceId);

    // ---------------------------------------------------------------- data

    public void Reload()
    {
        _byId.Clear();
        foreach (var snapshot in _server.Devices) _byId[snapshot.DeviceId] = new ComputerViewModel(snapshot);
        ReloadGroups();
        ApplyFilter();
    }

    private void ReloadGroups()
    {
        Groups.Clear();
        foreach (var g in _server.Groups) Groups.Add(g);
        var keep = SelectedFilter.Key;
        for (var i = Filters.Count - 1; i >= 4; i--) Filters.RemoveAt(i);
        foreach (var g in Groups) Filters.Add(new GroupFilter($"group:{g.Id}", null, g.Name, g.Id));
        _selectedFilter = Filters.FirstOrDefault(f => f.Key == keep) ?? Filters[0];
        Raise(nameof(SelectedFilter));
        var names = Groups.ToDictionary(g => g.Id, g => g.Name);
        foreach (var c in _byId.Values) c.GroupName = c.GroupId is { } id && names.TryGetValue(id, out var n) ? n : string.Empty;
    }

    public void ReloadGroupsFromServer()
    {
        ReloadGroups();
        ApplyFilter();
    }

    private bool Matches(ComputerViewModel c, GroupFilter f) => f.Key switch
    {
        GroupFilter.AllKey => !c.IsPending && c.Snapshot.Computer.Status != RegistrationState.Rejected,
        GroupFilter.OnlineKey => c.IsOnline && c.IsApproved,
        GroupFilter.OfflineKey => !c.IsOnline && c.IsApproved,
        GroupFilter.PendingKey => c.IsPending,
        _ => f.GroupId is not null && c.GroupId == f.GroupId && c.IsApproved,
    };

    private void ApplyFilter()
    {
        var wanted = _byId.Values.Where(c => Matches(c, SelectedFilter)).OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (var i = Computers.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Computers[i])) Computers.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Computers.Count && ReferenceEquals(Computers[i], wanted[i])) continue;
            var existing = Computers.IndexOf(wanted[i]);
            if (existing >= 0) Computers.Move(existing, i);
            else Computers.Insert(i, wanted[i]);
        }
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        foreach (var f in Filters)
            f.Count = _byId.Values.Count(c => Matches(c, f));
        foreach (var n in new[] { nameof(TotalCount), nameof(OnlineCount), nameof(OfflineCount), nameof(PendingCount), nameof(HasPending) }) Raise(n);
        RaiseCommands();
    }

    private void OnDeviceChanged(object? sender, DeviceSnapshot snapshot) => _ui.Post(() =>
    {
        if (!_byId.TryGetValue(snapshot.DeviceId, out var vm))
        {
            vm = new ComputerViewModel(snapshot);
            _byId[snapshot.DeviceId] = vm;
        }
        else
        {
            vm.Update(snapshot);
        }
        var groups = Groups.ToDictionary(g => g.Id, g => g.Name);
        vm.GroupName = vm.GroupId is { } id && groups.TryGetValue(id, out var n) ? n : string.Empty;
        ApplyFilter();
    });

    private void OnDeviceRemoved(object? sender, string deviceId) => _ui.Post(() =>
    {
        _byId.Remove(deviceId);
        ApplyFilter();
    });

    private void OnSharingChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        _isSharing = _share.IsSharing;
        Raise(nameof(IsSharing));
        RaiseCommands();
    });

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => _ui.Post(() =>
    {
        foreach (var c in _byId.Values) c.RefreshLanguage();
        foreach (var f in Filters) f.RefreshLanguage();
        Raise(nameof(TeacherStatus));
    });

    // ---------------------------------------------------------------- commands

    private AsyncRelayCommand Cmd(Func<Task> action, Func<bool>? can = null) =>
        new(action, can, ex => StatusMessage = $"{Loc.Instance["Error.Unexpected"]} {ex.Message}");

    private void RaiseCommands()
    {
        foreach (var command in new ICommand[]
        {
            LockSelectedCommand, UnlockSelectedCommand, MessageSelectedCommand, ScreenshotCommand, RestartCommand, ShutdownCommand, OpenApplicationCommand,
            CloseApplicationCommand, BlockApplicationCommand, UnblockApplicationCommand, ShareScreenSelectedCommand, ShareScreenGroupCommand, ApproveCommand, RejectCommand, RenameCommand, RemoveCommand,
            MoveToGroupCommand, ViewScreenCommand, RemoteControlCommand,
        })
        {
            switch (command)
            {
                case AsyncRelayCommand a: a.RaiseCanExecuteChanged(); break;
                case RelayCommand r: r.RaiseCanExecuteChanged(); break;
            }
        }
    }

    private async Task LockAsync(IReadOnlyList<ComputerViewModel> targets)
    {
        var text = await _dialogs.PromptAsync(Loc.Instance["Lock.Title"], Loc.Instance["Lock.Label"], Loc.Instance[DefaultLockKey], true).ConfigureAwait(true);
        if (text is null) return;
        var message = string.IsNullOrWhiteSpace(text) ? Loc.Instance[DefaultLockKey] : text.Trim();
        await RunAsync(targets.Where(c => c.CanBeCommanded), "Lock.Done", id => _server.LockAsync(id, message)).ConfigureAwait(true);
    }

    private Task UnlockAsync(IReadOnlyList<ComputerViewModel> targets) =>
        RunAsync(targets.Where(c => c.CanBeCommanded), "Unlock.Done", id => _server.UnlockAsync(id));

    private async Task RunAsync(IEnumerable<ComputerViewModel> targets, string doneKey, Func<string, Task<CommandOutcome>> action)
    {
        var list = targets.ToList();
        if (list.Count == 0)
        {
            StatusMessage = Loc.Instance["Error.NoTarget"];
            return;
        }
        var outcomes = await Task.WhenAll(list.Select(c => action(c.DeviceId))).ConfigureAwait(true);
        Report(list, outcomes, doneKey);
    }

    private void Report(IReadOnlyList<ComputerViewModel> targets, IReadOnlyList<CommandOutcome> outcomes, string doneKey)
    {
        var ok = outcomes.Count(o => o.Success);
        var failures = outcomes.Select((o, i) => (o, c: targets[i])).Where(x => !x.o.Success)
            .Select(x => $"{x.c.Title}: {x.o.Message}");
        StatusMessage = ok == outcomes.Count
            ? Loc.Instance.Format(doneKey, ok, outcomes.Count)
            : $"{Loc.Instance.Format(doneKey, ok, outcomes.Count)}  ⚠ {string.Join("; ", failures)}";
    }

    private async Task SendMessageAsync(IReadOnlyList<ComputerViewModel> targets)
    {
        var text = await _dialogs.PromptAsync(Loc.Instance["Message.Title"], Loc.Instance["Message.Label"], string.Empty, true).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(text)) return;
        await RunAsync(targets.Where(c => c.CanBeCommanded), "Message.Done", id => _server.SendMessageAsync(id, text.Trim())).ConfigureAwait(true);
    }

    private async Task ScreenshotAsync()
    {
        var targets = Selected().Where(c => c.CanBeCommanded).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = Loc.Instance["Error.NoTarget"];
            return;
        }
        var results = await Task.WhenAll(targets.Select(c => _server.TakeScreenshotAsync(c.DeviceId))).ConfigureAwait(true);
        Report(targets, [.. results.Select(r => r.Outcome)], "Screenshot.Done");
        if (results.Count(r => r.Record is not null) == 1 && results.Single(r => r.Record is not null).Record is { } one) LastScreenshotPath = one.FilePath;
    }

    public string? LastScreenshotPath { get; private set; }

    private async Task PowerAsync(bool restart)
    {
        var targets = Selected().Where(c => c.CanBeCommanded).ToList();
        if (targets.Count == 0) return;
        var key = restart ? "Confirm.Restart" : "Confirm.Shutdown";
        if (!await _dialogs.ConfirmAsync(Loc.Instance[restart ? "Restart.Title" : "Shutdown.Title"], Loc.Instance.Format(key, targets.Count)).ConfigureAwait(true)) return;
        await RunAsync(targets, restart ? "Restart.Done" : "Shutdown.Done", id => restart ? _server.RestartAsync(id) : _server.ShutdownAsync(id)).ConfigureAwait(true);
    }

    private async Task OpenApplicationAsync()
    {
        var target = await _dialogs.PromptAsync(Loc.Instance["OpenApp.Title"], Loc.Instance["OpenApp.Label"], string.Empty, false).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(target)) return;
        await RunAsync(Selected().Where(c => c.CanBeCommanded), "OpenApp.Done", id => _server.StartApplicationAsync(id, target.Trim())).ConfigureAwait(true);
    }

    private async Task CloseApplicationAsync()
    {
        var name = await _dialogs.PromptAsync(Loc.Instance["CloseApp.Title"], Loc.Instance["CloseApp.Label"], string.Empty, false).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(Selected().Where(c => c.CanBeCommanded), "CloseApp.Done", id => _server.StopApplicationAsync(id, name.Trim())).ConfigureAwait(true);
    }

    private async Task BlockAsync(bool block)
    {
        var name = await _dialogs.PromptAsync(Loc.Instance[block ? "BlockApp.Title" : "UnblockApp.Title"], Loc.Instance["BlockApp.Label"], string.Empty, false).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(Selected().Where(c => c.CanBeCommanded), block ? "BlockApp.Done" : "UnblockApp.Done",
            id => block ? _server.BlockApplicationAsync(id, name.Trim()) : _server.UnblockApplicationAsync(id, name.Trim())).ConfigureAwait(true);
    }

    private void OpenFullScreen(bool withRemoteControl)
    {
        var selected = Selected();
        if (selected.Count != 1) return;
        _dialogs.ShowFullScreen(selected[0], withRemoteControl);
    }

    private enum ShareTarget { Selected, All, Group }

    private async Task ToggleShareAsync(ShareTarget target)
    {
        if (_share.IsSharing)
        {
            await _share.StopAsync().ConfigureAwait(true);
            StatusMessage = Loc.Instance["Share.Stopped"];
            return;
        }

        Func<IReadOnlyCollection<string>> targets;
        switch (target)
        {
            case ShareTarget.Selected:
                var ids = Selected().Where(c => c.CanBeCommanded).Select(c => c.DeviceId).ToList();
                if (ids.Count == 0) { StatusMessage = Loc.Instance["Error.NoTarget"]; return; }
                targets = () => ids;
                break;
            case ShareTarget.Group:
                var groupId = SelectedFilter.GroupId;
                if (groupId is null) { StatusMessage = Loc.Instance["Error.NoGroup"]; return; }
                targets = () => [.. _server.Devices.Where(d => d.Computer.GroupId == groupId && d.Online && d.IsApproved).Select(d => d.DeviceId)];
                break;
            default:
                targets = () => [.. _server.Devices.Where(d => d.Online && d.IsApproved).Select(d => d.DeviceId)];
                break;
        }
        await _share.StartAsync(targets, Loc.Instance["Share.WindowTitle"]).ConfigureAwait(true);
        StatusMessage = Loc.Instance["Share.Started"];
    }

    private async Task ToggleMonitoringAsync()
    {
        if (_monitoringPaused)
        {
            await _monitoring.StartAllAsync().ConfigureAwait(true);
            _monitoringPaused = false;
        }
        else
        {
            await _monitoring.PauseAsync().ConfigureAwait(true);
            _monitoringPaused = true;
        }
        Raise(nameof(IsMonitoringPaused));
    }

    private async Task ApproveAsync(bool approve)
    {
        foreach (var c in Selected().Where(c => c.IsPending))
        {
            if (approve) await _server.ApproveAsync(c.DeviceId).ConfigureAwait(true);
            else await _server.RejectAsync(c.DeviceId).ConfigureAwait(true);
        }
        StatusMessage = Loc.Instance[approve ? "Approve.Done" : "Reject.Done"];
    }

    private async Task RenameAsync()
    {
        var c = Selected().Single();
        var name = await _dialogs.PromptAsync(Loc.Instance["Rename.Title"], Loc.Instance["Rename.Label"], c.Title, false).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        _server.Rename(c.DeviceId, name);
    }

    private async Task RemoveAsync()
    {
        var targets = Selected();
        if (!await _dialogs.ConfirmAsync(Loc.Instance["Remove.Title"], Loc.Instance.Format("Confirm.Remove", targets.Count)).ConfigureAwait(true)) return;
        foreach (var c in targets) _server.Remove(c.DeviceId);
        StatusMessage = Loc.Instance["Remove.Done"];
    }

    private void MoveToGroup(GroupRecord? group)
    {
        foreach (var c in Selected()) _server.SetGroup(c.DeviceId, group?.Id);
        StatusMessage = Loc.Instance["Group.Moved"];
    }

    public void Dispose()
    {
        _server.DeviceChanged -= OnDeviceChanged;
        _server.DeviceRemoved -= OnDeviceRemoved;
        _share.SharingChanged -= OnSharingChanged;
        Loc.Instance.PropertyChanged -= OnLanguageChanged;
    }
}
