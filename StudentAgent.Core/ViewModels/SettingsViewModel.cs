using System.Windows.Input;
using ClassroomControl.StudentAgent.Models;
using ClassroomControl.StudentAgent.Resources.Strings;
using ClassroomControl.StudentAgent.Services;

namespace ClassroomControl.StudentAgent.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    public const string Developer = "Mirjalol Murodov Nomoz o‘g‘li";
    public const string SupportEmail = "mirjalol.murodov09@gmail.com";
    public const string SupportTelegram = "@mirjalol.murodov09";

    private readonly ISettingsService _settings;
    private readonly IAgentService _agent;
    private readonly IStartupService _startup;
    private readonly IDeviceInfoService _device;
    private readonly IAgentStatusStore _store;
    private StudentSettings _model;
    private string _classroomCode;
    private string _errorMessage = string.Empty;
    private string _savedMessage = string.Empty;

    public SettingsViewModel(ISettingsService settings, IAgentService agent, IStartupService startup, IDeviceInfoService device, IAgentStatusStore store)
    {
        _settings = settings;
        _agent = agent;
        _startup = startup;
        _device = device;
        _store = store;
        _model = settings.Current;
        _classroomCode = _model.ClassroomCode;
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        ResetCertificateCommand = new RelayCommand(() => _ = ResetCertificateAsync());
    }

    public ICommand SaveCommand { get; }
    public ICommand ResetCertificateCommand { get; }
    /// <summary>Raised after a successful save so the window can close.</summary>
    public event EventHandler? Saved;

    // General
    public string ComputerName { get => _model.ComputerName; set { _model.ComputerName = value; Raise(); } }
    public string StudentName { get => _model.StudentName; set { _model.StudentName = value; Raise(); } }
    public string ClassroomName => string.IsNullOrEmpty(_model.ClassroomName) ? UiStrings.NotConnected : _model.ClassroomName;
    public string ClassroomCode { get => _classroomCode; set => Set(ref _classroomCode, value); }
    public bool StartWithWindows { get => _model.StartWithWindows; set { _model.StartWithWindows = value; Raise(); } }

    // Network (text bound so the user can type; parsed on save)
    public string TeacherAddress { get => _model.TeacherAddress; set { _model.TeacherAddress = value; Raise(); } }
    public int TeacherPort { get => _model.TeacherPort; set { _model.TeacherPort = value; Raise(); } }
    public int DiscoveryPort { get => _model.DiscoveryPort; set { _model.DiscoveryPort = value; Raise(); } }
    public int ConnectionTimeoutSeconds { get => _model.ConnectionTimeoutSeconds; set { _model.ConnectionTimeoutSeconds = value; Raise(); } }

    // Security
    public string DeviceId => _model.DeviceId;
    public string RegistrationStatus => _store.Current.Registration switch
    {
        RegistrationState.Approved => UiStrings.Registered,
        RegistrationState.Rejected => UiStrings.Rejected,
        RegistrationState.Pending => UiStrings.WaitingForApproval,
        _ => UiStrings.NotRegisteredStudent,
    };

    public string CertificateStatus => string.IsNullOrEmpty(_model.PinnedTeacherCertificate)
        ? "Not pinned yet (set on first successful connection)"
        : $"Pinned: {Shorten(_model.PinnedTeacherCertificate)}";

    // About
    public string Version => _device.AgentVersion;
    public string DeveloperName => Developer;
    public string Email => SupportEmail;
    public string Telegram => SupportTelegram;

    public string ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }
    public string SavedMessage { get => _savedMessage; private set => Set(ref _savedMessage, value); }

    public async Task SaveAsync()
    {
        ErrorMessage = string.Empty;
        SavedMessage = string.Empty;
        try
        {
            _model.ClassroomCode = ClassroomCode;
            await _settings.SaveAsync(_model);
            _startup.SetEnabled(_model.StartWithWindows);
            _agent.NotifySettingsChanged();
            SavedMessage = "Saqlandi.";
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (SettingsValidationException ex)
        {
            ErrorMessage = string.Join(Environment.NewLine, ex.Errors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            ErrorMessage = $"Sozlamalarni saqlab bo‘lmadi: {ex.Message}";
        }
    }

    /// <summary>Forget the pinned Teacher certificate (use when the Teacher PC was reinstalled).</summary>
    public async Task ResetCertificateAsync()
    {
        await _settings.UpdateAsync(s => s.PinnedTeacherCertificate = string.Empty);
        _model.PinnedTeacherCertificate = string.Empty;
        Raise(nameof(CertificateStatus));
        _agent.RequestConnect();
    }

    private static string Shorten(string fingerprint) => fingerprint.Length > 16 ? $"{fingerprint[..16]}…" : fingerprint;
}
