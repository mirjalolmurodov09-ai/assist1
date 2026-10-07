using System.Windows.Input;
using ClassroomControl.StudentAgent.Communication.Protocol;
using ClassroomControl.StudentAgent.Communication.Security;
using ClassroomControl.StudentAgent.Models;
using ClassroomControl.StudentAgent.Resources.Strings;
using ClassroomControl.StudentAgent.Services;

namespace ClassroomControl.StudentAgent.ViewModels;

/// <summary>Classroom code entry + Connect.</summary>
public sealed class RegistrationViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IAgentService _agent;
    private readonly IAgentStatusStore _store;
    private readonly IUiDispatcher _ui;
    private string _classroomCode;
    private string _studentName;
    private string _errorMessage = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isBusy;

    public RegistrationViewModel(ISettingsService settings, IAgentService agent, IAgentStatusStore store, IUiDispatcher ui)
    {
        _settings = settings;
        _agent = agent;
        _store = store;
        _ui = ui;
        var current = settings.Current;
        _classroomCode = current.ClassroomCode;
        _studentName = current.StudentName;
        ConnectCommand = new RelayCommand(() => _ = ConnectAsync(), () => !IsBusy);
        _store.Changed += OnStatusChanged;
        _statusMessage = store.Current.StatusText;
    }

    public ICommand ConnectCommand { get; }
    /// <summary>Raised when the window may close (registered / connected).</summary>
    public event EventHandler? CloseRequested;

    public string ClassroomCode
    {
        get => _classroomCode;
        set => Set(ref _classroomCode, value);
    }

    public string StudentName
    {
        get => _studentName;
        set => Set(ref _studentName, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => Set(ref _errorMessage, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) ((RelayCommand)ConnectCommand).RaiseCanExecuteChanged();
        }
    }

    public async Task ConnectAsync()
    {
        ErrorMessage = string.Empty;
        if (!Communication.Security.ClassroomCode.IsValidFormat(ClassroomCode))
        {
            ErrorMessage = UiStrings.InvalidClassroomCode;
            return;
        }

        IsBusy = true;
        try
        {
            await _settings.UpdateAsync(s =>
            {
                s.ClassroomCode = ClassroomCode;
                s.StudentName = StudentName.Trim();
            });
            _agent.RequestConnect();
        }
        catch (SettingsValidationException ex)
        {
            ErrorMessage = ex.Errors.FirstOrDefault() ?? UiStrings.InvalidClassroomCode;
        }
        catch (IOException)
        {
            ErrorMessage = "Sozlamalarni saqlab bo‘lmadi.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnStatusChanged(object? sender, AgentStatusSnapshot s) => _ui.Post(() =>
    {
        StatusMessage = s.StatusText;
        if (s.LastErrorCode == ErrorCodes.InvalidClassroomCode) ErrorMessage = UiStrings.InvalidClassroomCode;
        if (s.State == ConnectionState.Connected && s.Registration == RegistrationState.Approved) CloseRequested?.Invoke(this, EventArgs.Empty);
    });

    public void Dispose() => _store.Changed -= OnStatusChanged;
}
