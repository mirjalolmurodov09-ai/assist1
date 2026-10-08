using System.Collections.ObjectModel;
using System.Windows.Input;

namespace ClassroomControl.TeacherApp.ViewModels;

/// <summary>"Add computer": shows the classroom code to give to students and the computers waiting for approval.</summary>
public sealed class AddComputerViewModel : ObservableObject, IDisposable
{
    private readonly Server _server;
    private readonly IUiDispatcher _ui;
    private ComputerViewModel? _selected;

    public AddComputerViewModel(Server server, IUiDispatcher ui)
    {
        _server = server;
        _ui = ui;
        ApproveCommand = new AsyncRelayCommand(() => Decide(true), () => Selected is not null);
        RejectCommand = new AsyncRelayCommand(() => Decide(false), () => Selected is not null);
        ApproveAllCommand = new AsyncRelayCommand(ApproveAllAsync, () => Pending.Count > 0);
        _server.DeviceChanged += OnChanged;
        Reload();
    }

    public ObservableCollection<ComputerViewModel> Pending { get; } = [];
    public ICommand ApproveCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand ApproveAllCommand { get; }
    public string ClassroomCode => _server.ClassroomCodeText;
    public string Instructions => Loc.Instance["Add.Instructions"];

    public ComputerViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            (ApproveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RejectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void Reload()
    {
        Pending.Clear();
        foreach (var d in _server.Devices.Where(d => d.Computer.Status == RegistrationState.Pending)) Pending.Add(new ComputerViewModel(d));
        (ApproveAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnChanged(object? sender, DeviceSnapshot snapshot) => _ui.Post(Reload);

    private async Task Decide(bool approve)
    {
        if (Selected is null) return;
        if (approve) await _server.ApproveAsync(Selected.DeviceId).ConfigureAwait(true);
        else await _server.RejectAsync(Selected.DeviceId).ConfigureAwait(true);
    }

    private async Task ApproveAllAsync()
    {
        foreach (var c in Pending.ToList()) await _server.ApproveAsync(c.DeviceId).ConfigureAwait(true);
    }

    public void Dispose() => _server.DeviceChanged -= OnChanged;
}

public sealed class ScreenshotHistoryViewModel : ObservableObject
{
    private readonly Server _server;
    private readonly IDialogService _dialogs;
    private ScreenshotRecord? _selected;

    public ScreenshotHistoryViewModel(Server server, IDialogService dialogs)
    {
        _server = server;
        _dialogs = dialogs;
        OpenCommand = new RelayCommand(() => { if (Selected is not null) _dialogs.OpenFile(Selected.FilePath); }, () => Selected is not null);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => Selected is not null);
        RefreshCommand = new RelayCommand(Refresh);
        Refresh();
    }

    public ObservableCollection<ScreenshotRecord> Items { get; } = [];
    public ICommand OpenCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RefreshCommand { get; }

    public ScreenshotRecord? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            (OpenCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public void Refresh()
    {
        Items.Clear();
        foreach (var s in _server.Store.ListScreenshots(500)) Items.Add(s);
    }

    private async Task DeleteAsync()
    {
        var item = Selected!;
        if (!await _dialogs.ConfirmAsync(Loc.Instance["Shots.DeleteTitle"], Loc.Instance["Shots.DeleteConfirm"]).ConfigureAwait(true)) return;
        try
        {
            if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The database entry is still removed; the file may be open elsewhere.
        }
        _server.Store.DeleteScreenshot(item.Id);
        Refresh();
    }
}

public sealed class AboutViewModel
{
    public const string AppVersion = "1.0.0";
    public string ProductName => Loc.Instance["App.Name"];
    public string Version => AppVersion;
    public string Author => "Mirjalol Murodov Nomoz o‘g‘li";
    public string Email => "mirjalol.murodov09@gmail.com";
    public string Telegram => "@mirjalol.murodov09";
}
