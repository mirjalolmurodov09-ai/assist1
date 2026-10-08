using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Input;

namespace ClassroomControl.TeacherApp.ViewModels;

public interface IThemeService
{
    void Apply(string theme);
}

public sealed class LogsViewModel : ObservableObject, IDisposable
{
    private const int MaxRows = 500;
    private readonly Server _server;
    private readonly IUiDispatcher _ui;
    private string _filter = string.Empty;
    private int _pendingRefresh;

    public LogsViewModel(Server server, IUiDispatcher ui)
    {
        _server = server;
        _ui = ui;
        RefreshCommand = new RelayCommand(Refresh);
        _server.LogAdded += OnLogAdded;
        Refresh();
    }

    public ObservableCollection<LogEntry> Entries { get; } = [];
    public ICommand RefreshCommand { get; }

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) Refresh();
        }
    }

    public void Refresh()
    {
        Entries.Clear();
        foreach (var e in _server.Store.QueryLogs(MaxRows, Filter)) Entries.Add(e);
    }

    private void OnLogAdded(object? sender, LogEntry e)
    {
        if (Interlocked.Exchange(ref _pendingRefresh, 1) == 1) return; // coalesce bursts into one refresh
        _ui.Post(() =>
        {
            Volatile.Write(ref _pendingRefresh, 0);
            Refresh();
        });
    }

    public void Dispose() => _server.LogAdded -= OnLogAdded;
}

public sealed class UserItem : ObservableObject
{
    public UserItem(UserRecord record) => Record = record;
    public UserRecord Record { get; }
    public string Username => Record.Username;
    public string Role => Record.Role;
    public bool Disabled => Record.Disabled;
    public string LastLogin => Record.LastLoginAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
}

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly Server _server;
    private readonly UserService _users;
    private readonly TeacherSession _session;
    private readonly MonitoringService _monitoring;
    private readonly IDialogService _dialogs;
    private readonly IThemeService _theme;
    private string _classroomName;
    private string _language;
    private bool _dark;
    private int _tcpPort, _discoveryPort, _autoUnlock;
    private bool _requireActive;
    private int _monFps, _monQuality, _monWidth, _fullFps, _fullQuality, _fullWidth, _shareFps, _shareQuality, _shareWidth;
    private string _message = string.Empty;
    private GroupRecord? _selectedGroup;
    private UserItem? _selectedUser;
    private ComputerRecord? _selectedAgent;

    public SettingsViewModel(Server server, UserService users, TeacherSession session, MonitoringService monitoring, IDialogService dialogs, IUiDispatcher ui, IThemeService theme)
    {
        _server = server;
        _users = users;
        _session = session;
        _monitoring = monitoring;
        _dialogs = dialogs;
        _theme = theme;
        Logs = new LogsViewModel(server, ui);

        var s = server.Settings;
        _classroomName = server.Classroom.Name;
        _language = Loc.Instance.Language;
        _dark = s.Theme == "Dark";
        _tcpPort = s.TcpPort;
        _discoveryPort = s.DiscoveryPort;
        _requireActive = s.RequireAgentActive;
        _autoUnlock = s.AutoUnlockSeconds;
        (_monFps, _monQuality, _monWidth) = (s.MonitoringFps, s.MonitoringQuality, s.MonitoringMaxWidth);
        (_fullFps, _fullQuality, _fullWidth) = (s.FullScreenFps, s.FullScreenQuality, s.FullScreenMaxWidth);
        (_shareFps, _shareQuality, _shareWidth) = (s.TeacherScreenFps, s.TeacherScreenQuality, s.TeacherScreenMaxWidth);

        SaveCommand = new AsyncRelayCommand(SaveAsync, onError: ex => Message = ex.Message);
        RegenerateCodeCommand = new AsyncRelayCommand(RegenerateAsync, onError: ex => Message = ex.Message);
        SetCodeCommand = new AsyncRelayCommand(SetCodeAsync, onError: ex => Message = ex.Message);
        AddGroupCommand = new AsyncRelayCommand(AddGroupAsync, onError: ex => Message = ex.Message);
        RenameGroupCommand = new AsyncRelayCommand(RenameGroupAsync, () => SelectedGroup is not null, ex => Message = ex.Message);
        DeleteGroupCommand = new AsyncRelayCommand(DeleteGroupAsync, () => SelectedGroup is not null, ex => Message = ex.Message);
        AddUserCommand = new AsyncRelayCommand(AddUserAsync, () => _session.IsAdmin, ex => Message = ex.Message);
        DeleteUserCommand = new AsyncRelayCommand(DeleteUserAsync, () => _session.IsAdmin && SelectedUser is not null, ex => Message = ex.Message);
        ResetPasswordCommand = new AsyncRelayCommand(ResetPasswordAsync, () => _session.IsAdmin && SelectedUser is not null, ex => Message = ex.Message);
        ToggleUserCommand = new AsyncRelayCommand(ToggleUserAsync, () => _session.IsAdmin && SelectedUser is not null, ex => Message = ex.Message);
        RemoveAgentCommand = new AsyncRelayCommand(RemoveAgentAsync, () => SelectedAgent is not null, ex => Message = ex.Message);
        RenameAgentCommand = new AsyncRelayCommand(RenameAgentAsync, () => SelectedAgent is not null, ex => Message = ex.Message);
        ReloadLists();
    }

    public LogsViewModel Logs { get; }
    public ObservableCollection<GroupRecord> Groups { get; } = [];
    public ObservableCollection<UserItem> Users { get; } = [];
    public ObservableCollection<ComputerRecord> Agents { get; } = [];
    public IReadOnlyList<string> LanguageCodes => Loc.Languages;

    public ICommand SaveCommand { get; }
    public ICommand RegenerateCodeCommand { get; }
    public ICommand SetCodeCommand { get; }
    public ICommand AddGroupCommand { get; }
    public ICommand RenameGroupCommand { get; }
    public ICommand DeleteGroupCommand { get; }
    public ICommand AddUserCommand { get; }
    public ICommand DeleteUserCommand { get; }
    public ICommand ResetPasswordCommand { get; }
    public ICommand ToggleUserCommand { get; }
    public ICommand RemoveAgentCommand { get; }
    public ICommand RenameAgentCommand { get; }

    public string ClassroomName { get => _classroomName; set => Set(ref _classroomName, value); }
    public string ClassroomCode => _server.ClassroomCodeText;
    public string Language { get => _language; set { if (Set(ref _language, value)) Loc.Instance.Language = value; } }
    public bool IsDarkTheme { get => _dark; set { if (Set(ref _dark, value)) _theme.Apply(value ? "Dark" : "Light"); } }
    public static string DisplayName(string code) => Loc.DisplayName(code);

    public int TcpPort { get => _tcpPort; set => Set(ref _tcpPort, value); }
    public int DiscoveryPort { get => _discoveryPort; set => Set(ref _discoveryPort, value); }
    public string LocalAddresses => string.Join(", ", LocalIPv4());
    public string FirewallHint => Loc.Instance.Format("Network.FirewallHint", _server.DiscoveryPort, _server.TcpPort);

    public bool RequireAgentActive { get => _requireActive; set => Set(ref _requireActive, value); }
    public int AutoUnlockSeconds { get => _autoUnlock; set => Set(ref _autoUnlock, value); }
    public string CertificateFingerprint => string.Join(':', Enumerable.Range(0, _server.CertificateFingerprint.Length / 2).Select(i => _server.CertificateFingerprint.Substring(i * 2, 2)));
    public bool IsAdmin => _session.IsAdmin;

    public int MonitoringFps { get => _monFps; set => Set(ref _monFps, value); }
    public int MonitoringQuality { get => _monQuality; set => Set(ref _monQuality, value); }
    public int MonitoringMaxWidth { get => _monWidth; set => Set(ref _monWidth, value); }
    public int FullScreenFps { get => _fullFps; set => Set(ref _fullFps, value); }
    public int FullScreenQuality { get => _fullQuality; set => Set(ref _fullQuality, value); }
    public int FullScreenMaxWidth { get => _fullWidth; set => Set(ref _fullWidth, value); }
    public int TeacherScreenFps { get => _shareFps; set => Set(ref _shareFps, value); }
    public int TeacherScreenQuality { get => _shareQuality; set => Set(ref _shareQuality, value); }
    public int TeacherScreenMaxWidth { get => _shareWidth; set => Set(ref _shareWidth, value); }

    public GroupRecord? SelectedGroup { get => _selectedGroup; set { if (Set(ref _selectedGroup, value)) Refresh(RenameGroupCommand, DeleteGroupCommand); } }
    public UserItem? SelectedUser { get => _selectedUser; set { if (Set(ref _selectedUser, value)) Refresh(DeleteUserCommand, ResetPasswordCommand, ToggleUserCommand); } }
    public ComputerRecord? SelectedAgent { get => _selectedAgent; set { if (Set(ref _selectedAgent, value)) Refresh(RemoveAgentCommand, RenameAgentCommand); } }

    public string Message { get => _message; private set => Set(ref _message, value); }

    private static void Refresh(params ICommand[] commands)
    {
        foreach (var c in commands) (c as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private static IEnumerable<string> LocalIPv4() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => a.Address.ToString());

    private void ReloadLists()
    {
        Groups.Clear();
        foreach (var g in _server.Groups) Groups.Add(g);
        Users.Clear();
        foreach (var u in _users.List()) Users.Add(new UserItem(u));
        Agents.Clear();
        foreach (var a in _server.Devices) Agents.Add(a.Computer);
    }

    private async Task SaveAsync()
    {
        var s = _server.Settings;
        if (_tcpPort is < 1 or > 65535 || _discoveryPort is < 1 or > 65535) { Message = Loc.Instance["Settings.BadPort"]; return; }
        var portsChanged = _tcpPort != s.TcpPort || _discoveryPort != s.DiscoveryPort;
        if (!string.IsNullOrWhiteSpace(ClassroomName) && ClassroomName.Trim() != _server.Classroom.Name) _server.RenameClassroom(ClassroomName);
        s.TcpPort = _tcpPort;
        s.DiscoveryPort = _discoveryPort;
        s.RequireAgentActive = _requireActive;
        s.AutoUnlockSeconds = _autoUnlock;
        (s.MonitoringFps, s.MonitoringQuality, s.MonitoringMaxWidth) = (_monFps, _monQuality, _monWidth);
        (s.FullScreenFps, s.FullScreenQuality, s.FullScreenMaxWidth) = (_fullFps, _fullQuality, _fullWidth);
        (s.TeacherScreenFps, s.TeacherScreenQuality, s.TeacherScreenMaxWidth) = (_shareFps, _shareQuality, _shareWidth);
        s.Language = _language;
        s.Theme = _dark ? "Dark" : "Light";
        await _monitoring.ApplySettingsAsync().ConfigureAwait(true);
        Message = Loc.Instance[portsChanged ? "Settings.SavedRestart" : "Settings.Saved"];
    }

    private async Task RegenerateAsync()
    {
        if (!await _dialogs.ConfirmAsync(Loc.Instance["Code.RegenerateTitle"], Loc.Instance["Code.RegenerateConfirm"]).ConfigureAwait(true)) return;
        _server.RegenerateClassroomCode();
        Raise(nameof(ClassroomCode));
        Message = Loc.Instance["Code.Changed"];
    }

    private async Task SetCodeAsync()
    {
        var code = await _dialogs.PromptAsync(Loc.Instance["Code.SetTitle"], Loc.Instance["Code.SetLabel"], string.Empty, false).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(code)) return;
        if (!Shared.Communication.Security.ClassroomCode.IsValidFormat(code)) { Message = Loc.Instance["Code.Invalid"]; return; }
        _server.SetClassroomCode(code);
        Raise(nameof(ClassroomCode));
        Message = Loc.Instance["Code.Changed"];
    }

    private async Task AddGroupAsync()
    {
        var name = await _dialogs.PromptAsync(Loc.Instance["Group.AddTitle"], Loc.Instance["Group.NameLabel"], string.Empty, false).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            _server.AddGroup(name);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            Message = Loc.Instance["Group.Exists"];
            return;
        }
        ReloadLists();
    }

    private async Task RenameGroupAsync()
    {
        var group = SelectedGroup!;
        var name = await _dialogs.PromptAsync(Loc.Instance["Group.RenameTitle"], Loc.Instance["Group.NameLabel"], group.Name, false).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        _server.Store.RenameGroup(group.Id, name.Trim());
        ReloadLists();
    }

    private async Task DeleteGroupAsync()
    {
        var group = SelectedGroup!;
        if (!await _dialogs.ConfirmAsync(Loc.Instance["Group.DeleteTitle"], Loc.Instance.Format("Group.DeleteConfirm", group.Name)).ConfigureAwait(true)) return;
        _server.Store.DeleteGroup(group.Id);
        ReloadLists();
    }

    private async Task AddUserAsync()
    {
        var name = await _dialogs.PromptAsync(Loc.Instance["User.AddTitle"], Loc.Instance["User.NameLabel"], string.Empty, false).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        var password = await _dialogs.PromptAsync(Loc.Instance["User.AddTitle"], Loc.Instance["User.PasswordLabel"], string.Empty, false).ConfigureAwait(true);
        if (password is null) return;
        try
        {
            _users.Create(name, password, Roles.Teacher);
            Message = Loc.Instance["User.Added"];
        }
        catch (UserException ex)
        {
            Message = ex.Message;
        }
        ReloadLists();
    }

    private async Task DeleteUserAsync()
    {
        var user = SelectedUser!;
        if (!await _dialogs.ConfirmAsync(Loc.Instance["User.DeleteTitle"], Loc.Instance.Format("User.DeleteConfirm", user.Username)).ConfigureAwait(true)) return;
        try { _users.Delete(user.Record.Id); }
        catch (UserException ex) { Message = ex.Message; }
        ReloadLists();
    }

    private async Task ResetPasswordAsync()
    {
        var user = SelectedUser!;
        var password = await _dialogs.PromptAsync(Loc.Instance["User.ResetTitle"], Loc.Instance["User.PasswordLabel"], string.Empty, false).ConfigureAwait(true);
        if (password is null) return;
        try
        {
            _users.ChangePassword(user.Record.Id, password);
            Message = Loc.Instance["User.PasswordChanged"];
        }
        catch (UserException ex)
        {
            Message = ex.Message;
        }
    }

    private Task ToggleUserAsync()
    {
        var user = SelectedUser!;
        try { _users.SetDisabled(user.Record.Id, !user.Disabled); }
        catch (UserException ex) { Message = ex.Message; }
        ReloadLists();
        return Task.CompletedTask;
    }

    private async Task RemoveAgentAsync()
    {
        var agent = SelectedAgent!;
        if (!await _dialogs.ConfirmAsync(Loc.Instance["Remove.Title"], Loc.Instance.Format("Confirm.Remove", 1)).ConfigureAwait(true)) return;
        _server.Remove(agent.DeviceId);
        ReloadLists();
    }

    private async Task RenameAgentAsync()
    {
        var agent = SelectedAgent!;
        var name = await _dialogs.PromptAsync(Loc.Instance["Rename.Title"], Loc.Instance["Rename.Label"], agent.Title, false).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        _server.Rename(agent.DeviceId, name);
        ReloadLists();
    }

    public void Dispose() => Logs.Dispose();
}
