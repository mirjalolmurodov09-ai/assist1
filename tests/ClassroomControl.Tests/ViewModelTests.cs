using ClassroomControl.StudentAgent.Models;
using ClassroomControl.StudentAgent.Services;
using ClassroomControl.StudentAgent.ViewModels;
using Xunit;

namespace ClassroomControl.StudentAgent.Tests;

public sealed class ViewModelTests : IDisposable
{
    private readonly TestSupport.TempSettings _env = new();
    private readonly SettingsService _settings;
    private readonly AgentStatusStore _store = new(TimeProvider.System);
    private readonly FakeAgent _agent = new();
    private readonly FakeStartup _startup = new();
    private readonly FakeWindows _windows = new();
    private readonly FakeDevice _device;

    public ViewModelTests()
    {
        _settings = _env.Create();
        _settings.Load();
        _device = new FakeDevice(_settings.Current.DeviceId);
    }

    public void Dispose() => _env.Dispose();

    private MainViewModel Main() => new(_store, _device, _settings, new ImmediateDispatcher(), _windows);

    [Fact]
    public void Main_window_shows_offline_initially()
    {
        var vm = Main();
        Assert.Equal("🔴 Offline", vm.IndicatorText);
        Assert.Equal("Ro‘yxatdan o‘tmagan", vm.StudentName);
        Assert.Equal("PC-01", vm.ComputerName);
        Assert.Equal(_device.DeviceId[..8], vm.DeviceIdShort);
        Assert.Equal("192.168.1.101", vm.LocalIp);
        Assert.Equal("Classroom Control — Student Agent", vm.Title);
    }

    [Fact]
    public void Main_window_follows_connecting_then_connected_then_offline()
    {
        var vm = Main();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        _store.TransitionTo(ConnectionState.Discovering);
        Assert.Equal("🟡 Connecting...", vm.IndicatorText);

        _store.TransitionTo(ConnectionState.TeacherFound, s => s with { TeacherAddress = "192.168.1.10" });
        _store.TransitionTo(ConnectionState.Connecting);
        _store.TransitionTo(ConnectionState.Authenticating);
        _store.TransitionTo(ConnectionState.WaitingForApproval, s => s with { Registration = RegistrationState.Pending, ClassroomName = "8-A", TeacherName = "Teacher PC" });
        Assert.Equal("🟡 Connecting...", vm.IndicatorText);
        Assert.Equal("Waiting for Teacher approval", vm.RegistrationText);

        _store.TransitionTo(ConnectionState.Connected, s => s with { Registration = RegistrationState.Approved });
        Assert.Equal("🟢 Connected", vm.IndicatorText);
        Assert.Equal("Registered", vm.RegistrationText);
        Assert.Equal("192.168.1.10", vm.TeacherAddress);
        Assert.Equal("8-A", vm.ClassroomName);
        Assert.Equal("Teacher PC", vm.TeacherName);
        Assert.Contains(nameof(MainViewModel.IndicatorText), changed);

        _store.TransitionTo(ConnectionState.Reconnecting);
        Assert.Equal("🔴 Offline", vm.IndicatorText);
    }

    [Fact]
    public async Task Student_name_is_shown_only_when_registered()
    {
        var vm = Main();
        await _settings.UpdateAsync(s => s.StudentName = "Ali");
        Assert.Equal("Ro‘yxatdan o‘tmagan", vm.StudentName);
        _store.Update(s => s with { Registration = RegistrationState.Approved });
        Assert.Equal("Ali", vm.StudentName);
    }

    [Fact]
    public void Main_window_commands_open_the_other_windows()
    {
        var vm = Main();
        vm.OpenSettingsCommand.Execute(null);
        vm.OpenRegistrationCommand.Execute(null);
        Assert.Equal(1, _windows.SettingsOpened);
        Assert.Equal(1, _windows.RegistrationOpened);
    }

    [Fact]
    public async Task Registration_rejects_a_malformed_code_without_touching_the_agent()
    {
        var vm = new RegistrationViewModel(_settings, _agent, _store, new ImmediateDispatcher()) { ClassroomCode = "123" };
        await vm.ConnectAsync();
        Assert.Equal("Classroom code noto‘g‘ri.", vm.ErrorMessage);
        Assert.Equal(0, _agent.ConnectRequests);
    }

    [Fact]
    public async Task Registration_saves_the_code_and_starts_connecting()
    {
        var code = TestSupport.NewCode();
        var vm = new RegistrationViewModel(_settings, _agent, _store, new ImmediateDispatcher()) { ClassroomCode = code, StudentName = " Ali " };
        await vm.ConnectAsync();
        Assert.Equal(string.Empty, vm.ErrorMessage);
        Assert.Equal(1, _agent.ConnectRequests);
        Assert.Equal(code, _settings.Current.ClassroomCode);
        Assert.Equal("Ali", _settings.Current.StudentName);
    }

    [Fact]
    public void Registration_shows_the_teacher_side_code_error_and_closes_when_registered()
    {
        var vm = new RegistrationViewModel(_settings, _agent, _store, new ImmediateDispatcher());
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        _store.TransitionTo(ConnectionState.Discovering);
        _store.TransitionTo(ConnectionState.Reconnecting, s => s with { LastErrorCode = "INVALID_CLASSROOM_CODE" });
        Assert.Equal("Classroom code noto‘g‘ri.", vm.ErrorMessage);
        Assert.False(closed);

        _store.TransitionTo(ConnectionState.Discovering);
        _store.TransitionTo(ConnectionState.TeacherFound);
        _store.TransitionTo(ConnectionState.Connecting);
        _store.TransitionTo(ConnectionState.Authenticating);
        _store.TransitionTo(ConnectionState.Connected, s => s with { Registration = RegistrationState.Approved });
        Assert.True(closed);
    }

    [Fact]
    public async Task Settings_save_persists_applies_startup_and_notifies_the_agent()
    {
        var vm = new SettingsViewModel(_settings, _agent, _startup, _device, _store)
        {
            StudentName = "Vali", StartWithWindows = false, TeacherPort = 40000, ClassroomCode = TestSupport.NewCode(),
        };
        var saved = false;
        vm.Saved += (_, _) => saved = true;

        await vm.SaveAsync();

        Assert.True(saved);
        Assert.Equal(string.Empty, vm.ErrorMessage);
        Assert.Equal("Vali", _settings.Current.StudentName);
        Assert.Equal(40000, _settings.Current.TeacherPort);
        Assert.Equal(false, _startup.LastValue);
        Assert.Equal(1, _agent.SettingsNotifications);
    }

    [Fact]
    public async Task Settings_with_invalid_values_show_errors_and_do_not_save()
    {
        var vm = new SettingsViewModel(_settings, _agent, _startup, _device, _store) { DiscoveryPort = 0, TeacherAddress = "8.8.8.8" };
        await vm.SaveAsync();
        Assert.Contains("Discovery port", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Teacher IP", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, _agent.SettingsNotifications);
        Assert.Null(_startup.LastValue);
    }

    [Fact]
    public void Settings_expose_security_and_about_information()
    {
        var vm = new SettingsViewModel(_settings, _agent, _startup, _device, _store);
        Assert.Equal(_settings.Current.DeviceId, vm.DeviceId);
        Assert.Contains("Not pinned", vm.CertificateStatus, StringComparison.Ordinal);
        Assert.Equal("1.0.0", vm.Version);
        Assert.Equal("Mirjalol Murodov Nomoz o‘g‘li", vm.DeveloperName);
        Assert.Equal("mirjalol.murodov09@gmail.com", vm.Email);
        Assert.Equal("@mirjalol.murodov09", vm.Telegram);
    }

    [Fact]
    public async Task Resetting_the_certificate_clears_the_pin()
    {
        await _settings.UpdateAsync(s => s.PinnedTeacherCertificate = "ABCDEF0123456789ABCDEF0123456789");
        var vm = new SettingsViewModel(_settings, _agent, _startup, _device, _store);
        Assert.StartsWith("Pinned:", vm.CertificateStatus, StringComparison.Ordinal);
        await vm.ResetCertificateAsync();
        Assert.Contains("Not pinned", vm.CertificateStatus, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _settings.Current.PinnedTeacherCertificate);
        Assert.Equal(1, _agent.ConnectRequests);
    }

    private sealed class FakeAgent : IAgentService
    {
        public int ConnectRequests;
        public int SettingsNotifications;
        public void RequestConnect() => ConnectRequests++;
        public void NotifySettingsChanged() => SettingsNotifications++;
    }

    private sealed class FakeStartup : IStartupService
    {
        public bool? LastValue;
        public bool IsEnabled => LastValue ?? false;
        public void SetEnabled(bool enabled) => LastValue = enabled;
    }

    private sealed class FakeWindows : IWindowService
    {
        public int SettingsOpened;
        public int RegistrationOpened;
        public void ShowMain() { }
        public void ShowSettings(bool aboutTab = false) => SettingsOpened++;
        public void ShowRegistration() => RegistrationOpened++;
    }

    private sealed class FakeDevice(string id) : IDeviceInfoService
    {
        public string DeviceId { get; } = id;
        public string ComputerName => "PC-01";
        public string LocalIp => "192.168.1.101";
        public string AgentVersion => "1.0.0";
        public DeviceInfo GetDeviceInfo() => throw new NotSupportedException();
    }
}
