using ClassroomControl.Shared.Files;
using ClassroomControl.StudentAgent.Features;
using ClassroomControl.StudentAgent.Models;
using ClassroomControl.StudentAgent.Resources.Strings;
using ClassroomControl.StudentAgent.Services;
using System.Windows.Input;

namespace ClassroomControl.StudentAgent.ViewModels;

/// <summary>Opens secondary windows; implemented by the WPF layer.</summary>
public interface IWindowService
{
    void ShowMain();
    void ShowSettings(bool aboutTab = false);
    void ShowRegistration();
    /// <summary>Lets the student choose a file to send to the teacher; null when cancelled.</summary>
    string? PickFileToSend();
    void OpenFolder(string path);
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int ShortIdLength = 8;
    private readonly IAgentStatusStore _store;
    private readonly IDeviceInfoService _device;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;
    private readonly FeatureState _features;
    private readonly IFileTransferService _files;
    private readonly IWindowService _windows;
    private CancellationTokenSource? _sendCts;
    private string _fileStatus = UiStrings.FileIdle;
    private int _filePercent;
    private bool _fileBusy;
    private AgentStatusSnapshot _snapshot;

    public MainViewModel(IAgentStatusStore store, IDeviceInfoService device, ISettingsService settings, IUiDispatcher ui, IWindowService windows, FeatureState features, IFileTransferService files)
    {
        _features = features;
        _files = files;
        _windows = windows;
        _store = store;
        _device = device;
        _settings = settings;
        _ui = ui;
        _snapshot = store.Current;
        OpenSettingsCommand = new RelayCommand(() => windows.ShowSettings());
        OpenRegistrationCommand = new RelayCommand(() => windows.ShowRegistration());
        SendFileCommand = new AsyncRelayCommand(SendFileAsync, () => _files.CanSend && !_fileBusy);
        CancelFileCommand = new RelayCommand(() => _sendCts?.Cancel(), () => _fileBusy);
        OpenReceivedFolderCommand = new RelayCommand(() => windows.OpenFolder(_files.ReceiveFolder));
        _files.Progress += OnFileProgress;
        _store.Changed += OnStatusChanged;
        _settings.Changed += OnSettingsChanged;
        _features.Changed += OnFeaturesChanged;
    }

    /// <summary>What the teacher is doing to this computer right now (never hidden from the student).</summary>
    public string ActivityText => string.Join(Environment.NewLine, new[]
    {
        _features.Locked ? UiStrings.ActivityLocked : null,
        _features.Streaming ? UiStrings.ActivityWatched : null,
        _features.RemoteControl ? UiStrings.ActivityRemote : null,
        _features.TeacherScreen ? UiStrings.ActivityTeacherScreen : null,
        _features.BlockedApplications.Count > 0 ? UiStrings.ActivityBlocked(_features.BlockedApplications) : null,
    }.Where(line => line is not null));

    private void OnFeaturesChanged(object? sender, EventArgs e) => _ui.Post(() => Raise(nameof(ActivityText)));

    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenRegistrationCommand { get; }
    public ICommand SendFileCommand { get; }
    public ICommand CancelFileCommand { get; }
    public ICommand OpenReceivedFolderCommand { get; }

    public string FileStatusText
    {
        get => _fileStatus;
        private set => Set(ref _fileStatus, value);
    }

    public int FilePercent
    {
        get => _filePercent;
        private set => Set(ref _filePercent, value);
    }

    public bool IsFileBusy
    {
        get => _fileBusy;
        private set => Set(ref _fileBusy, value);
    }

    private async Task SendFileAsync()
    {
        var path = _windows.PickFileToSend();
        if (string.IsNullOrEmpty(path)) return;
        using var cts = new CancellationTokenSource();
        _sendCts = cts;
        _ui.Post(() =>
        {
            IsFileBusy = true;
            RaiseFileCommands();
        });
        try
        {
            await _files.SendToTeacherAsync(path, cts.Token).ConfigureAwait(true);
        }
        finally
        {
            _sendCts = null;
            _ui.Post(() =>
            {
                IsFileBusy = false;
                RaiseFileCommands();
            });
        }
    }

    private void RaiseFileCommands()
    {
        (SendFileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnFileProgress(TransferProgress p) => _ui.Post(() =>
    {
        var percent = (int)Math.Round(p.Fraction * 100);
        if (p.Direction == TransferDirection.Sending)
        {
            FilePercent = p.State == TransferState.Completed ? 100 : percent;
            FileStatusText = p.State switch
            {
                TransferState.Preparing or TransferState.Waiting => UiStrings.FilePreparing(p.FileName),
                TransferState.Transferring => UiStrings.FileSending(p.FileName, percent),
                TransferState.Completed => UiStrings.FileSent(p.FileName),
                TransferState.Cancelled => UiStrings.FileCancelled,
                _ => UiStrings.FileFailed(p.FileName, p.Message),
            };
        }
        else if (p.State == TransferState.Transferring)
        {
            FilePercent = percent;
            FileStatusText = UiStrings.FileReceiving(p.FileName, percent);
        }
        else if (p.State == TransferState.Completed)
        {
            FilePercent = 100;
            FileStatusText = UiStrings.FileReceivedShort(p.FileName);
        }
        else if (p.IsFinished)
        {
            FileStatusText = UiStrings.FileReceiveFailed(p.FileName, p.Message);
        }
    });

    public string Title => "Classroom Control — Student Agent";
    public string AgentVersion => _device.AgentVersion;

    public string IndicatorText => IndicatorFor(_snapshot.Indicator);
    public StatusIndicator Indicator => _snapshot.Indicator;
    public string StatusText => _snapshot.StatusText;
    public string ConnectionText => _snapshot.State.ToString();

    public string RegistrationText => _snapshot.Registration switch
    {
        RegistrationState.Approved => UiStrings.Registered,
        RegistrationState.Rejected => UiStrings.Rejected,
        RegistrationState.Pending => UiStrings.WaitingForApproval,
        _ => _snapshot.State == ConnectionState.WaitingForApproval ? UiStrings.WaitingForApproval : UiStrings.NotRegisteredStudent,
    };

    public string ComputerName => _device.ComputerName;
    public string DeviceId => _device.DeviceId;
    public string DeviceIdShort => _device.DeviceId.Length > ShortIdLength ? _device.DeviceId[..ShortIdLength] : _device.DeviceId;
    public string StudentName => string.IsNullOrWhiteSpace(_settings.Current.StudentName) || _snapshot.Registration != RegistrationState.Approved
        ? UiStrings.NotRegisteredStudent
        : _settings.Current.StudentName;
    public string LocalIp => _device.LocalIp;
    public string TeacherAddress => _snapshot.TeacherAddress ?? UiStrings.NotConnected;
    public string ClassroomName => OrDash(_snapshot.ClassroomName ?? _settings.Current.ClassroomName);
    public string TeacherName => OrDash(_snapshot.TeacherName);
    public string LastError => _snapshot.LastErrorMessage ?? string.Empty;
    public bool RequireAgentActive => _snapshot.RequireAgentActive;

    public static string IndicatorFor(StatusIndicator indicator) => indicator switch
    {
        StatusIndicator.Connected => UiStrings.IndicatorConnected,
        StatusIndicator.Connecting => UiStrings.IndicatorConnecting,
        _ => UiStrings.IndicatorOffline,
    };

    private static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? UiStrings.NotConnected : value;

    private void OnStatusChanged(object? sender, AgentStatusSnapshot snapshot) => _ui.Post(() =>
    {
        _snapshot = snapshot;
        RaiseAll();
        RaiseFileCommands();
    });

    private void OnSettingsChanged(object? sender, EventArgs e) => _ui.Post(RaiseAll);

    private void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(IndicatorText), nameof(Indicator), nameof(StatusText), nameof(ConnectionText), nameof(RegistrationText),
            nameof(ComputerName), nameof(StudentName), nameof(LocalIp), nameof(TeacherAddress), nameof(ClassroomName),
            nameof(TeacherName), nameof(LastError), nameof(RequireAgentActive), nameof(DeviceId), nameof(DeviceIdShort), nameof(ActivityText),
        }) Raise(name);
    }

    public void Dispose()
    {
        _store.Changed -= OnStatusChanged;
        _settings.Changed -= OnSettingsChanged;
        _features.Changed -= OnFeaturesChanged;
        _files.Progress -= OnFileProgress;
    }
}
