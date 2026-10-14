using System.Collections.ObjectModel;
using System.Windows.Input;
using ClassroomControl.Shared.Files;

namespace ClassroomControl.TeacherApp.ViewModels;

/// <summary>One line of the transfer list (a file going to or coming from one computer).</summary>
public sealed class TransferItemViewModel : ObservableObject
{
    private TransferProgress _progress;

    public TransferItemViewModel(TransferUpdate update, Action<string> openFolder)
    {
        DeviceId = update.DeviceId;
        ComputerTitle = update.ComputerTitle;
        _progress = update.Progress;
        OpenCommand = new RelayCommand(() =>
        {
            if (StoredPath is { } path) openFolder(Path.GetDirectoryName(path) ?? path);
        }, () => CanOpen);
    }

    public ICommand OpenCommand { get; }

    public string DeviceId { get; }
    public string ComputerTitle { get; }
    public string TransferId => _progress.TransferId;
    public string FileName => _progress.FileName;
    public bool IsSending => _progress.Direction == TransferDirection.Sending;
    public bool IsFinished => _progress.IsFinished;
    public bool IsFailed => _progress.State is TransferState.Failed or TransferState.Cancelled;
    public bool IsCompleted => _progress.State == TransferState.Completed;
    public double Percent => _progress.Fraction * 100;
    public string SizeText => FormatSize(_progress.Size);

    /// <summary>The stored file (received) — empty for outgoing transfers.</summary>
    public string? StoredPath => IsCompleted && !IsSending ? _progress.Path : null;
    public bool CanOpen => StoredPath is not null;

    public string DirectionText => Loc.Instance[IsSending ? "Files.Sending" : "Files.Receiving"];

    public string StatusText => _progress.State switch
    {
        TransferState.Preparing => Loc.Instance["Files.StatePreparing"],
        TransferState.Waiting => Loc.Instance["Files.StateWaiting"],
        TransferState.Transferring => $"{Loc.Instance["Files.StateTransferring"]} {Percent:0}%",
        TransferState.Completed => Loc.Instance["Files.StateCompleted"],
        TransferState.Cancelled => Loc.Instance["Files.StateCancelled"],
        _ => $"{Loc.Instance["Files.StateFailed"]}: {ErrorText(_progress)}",
    };

    public void Update(TransferProgress progress)
    {
        _progress = progress;
        foreach (var name in new[] { nameof(FileName), nameof(IsFinished), nameof(IsFailed), nameof(IsCompleted), nameof(Percent), nameof(SizeText), nameof(StatusText), nameof(StoredPath), nameof(CanOpen) })
            Raise(name);
        (OpenCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void RefreshLanguage()
    {
        Raise(nameof(DirectionText));
        Raise(nameof(StatusText));
    }

    private static string ErrorText(TransferProgress p)
    {
        var text = p.ErrorCode switch
        {
            ErrorCodes.FileTooLarge => Loc.Instance["Files.ErrTooLarge"],
            ErrorCodes.FileTypeBlocked => Loc.Instance["Files.ErrBlocked"],
            ErrorCodes.FileNoSpace => Loc.Instance["Files.ErrNoSpace"],
            ErrorCodes.FileCorrupt => Loc.Instance["Files.ErrCorrupt"],
            ErrorCodes.FileCancelled => Loc.Instance["Files.ErrCancelled"],
            ErrorCodes.FileNotFound => Loc.Instance["Files.ErrNotFound"],
            ErrorCodes.TooManyTransfers => Loc.Instance["Files.ErrTooMany"],
            ErrorCodes.TransferTimeout => Loc.Instance["Files.ErrTimeout"],
            ErrorCodes.ConnectionLost => Loc.Instance["Files.ErrConnection"],
            "OFFLINE" => Loc.Instance["Files.ErrOffline"],
            ErrorCodes.NotRegistered => Loc.Instance["Files.ErrNotApproved"],
            ErrorCodes.FileRejected => Loc.Instance["Files.ErrRejected"],
            _ => null,
        };
        if (text is not null) return text;
        return string.IsNullOrWhiteSpace(p.Message) ? p.ErrorCode ?? string.Empty : p.Message;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.0} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>Sending files to computers, the list of transfers (both directions) and the file settings. Files from students land in one
/// folder per computer; the teacher is told when one arrives.</summary>
public sealed class FileTransferViewModel : ObservableObject, IDisposable
{
    private const int KeepItems = 200;
    private readonly Server _server;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly Func<IReadOnlyList<string>> _selectedDevices;
    private readonly Func<long?> _selectedGroup;
    private readonly Dictionary<string, TransferItemViewModel> _byId = [];
    private readonly object _batchGate = new();
    private readonly List<CancellationTokenSource> _batches = [];
    private string _statusMessage = string.Empty;
    private string _maxSizeText;

    public FileTransferViewModel(Server server, IDialogService dialogs, IUiDispatcher ui, Func<IReadOnlyList<string>> selectedDevices, Func<long?> selectedGroup)
    {
        _server = server;
        _dialogs = dialogs;
        _ui = ui;
        _selectedDevices = selectedDevices;
        _selectedGroup = selectedGroup;
        _maxSizeText = server.Settings.MaxFileSizeMb.ToString(System.Globalization.CultureInfo.InvariantCulture);

        SendToSelectedCommand = new AsyncRelayCommand(() => SendAsync(Targets.Selected), onError: OnError);
        SendToGroupCommand = new AsyncRelayCommand(() => SendAsync(Targets.Group), onError: OnError);
        SendToAllCommand = new AsyncRelayCommand(() => SendAsync(Targets.All), onError: OnError);
        CancelCommand = new RelayCommand(CancelSending, () => IsSending);
        ClearFinishedCommand = new RelayCommand(ClearFinished);
        OpenReceivedFolderCommand = new RelayCommand(() => _dialogs.OpenFile(Directory.CreateDirectory(_server.ReceivedFilesDirectory).FullName));

        _server.TransferChanged += OnTransferChanged;
        Loc.Instance.PropertyChanged += OnLanguageChanged;
    }

    private enum Targets { Selected, Group, All }

    public ObservableCollection<TransferItemViewModel> Items { get; } = [];

    public ICommand SendToSelectedCommand { get; }
    public ICommand SendToGroupCommand { get; }
    public ICommand SendToAllCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearFinishedCommand { get; }
    public ICommand OpenReceivedFolderCommand { get; }

    /// <summary>Raised on the UI thread when a student's file arrived completely.</summary>
    public event EventHandler<TransferUpdate>? FileReceived;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public bool IsSending
    {
        get
        {
            lock (_batchGate) return _batches.Count > 0;
        }
    }

    public bool HasItems => Items.Count > 0;
    public string ReceivedFolder => _server.ReceivedFilesDirectory;

    public bool AcceptFromStudents
    {
        get => _server.Settings.AcceptFilesFromStudents;
        set
        {
            _server.Settings.AcceptFilesFromStudents = value;
            Raise();
        }
    }

    public bool AllowPrograms
    {
        get => _server.Settings.AllowExecutableFiles;
        set
        {
            _server.Settings.AllowExecutableFiles = value;
            Raise();
        }
    }

    /// <summary>Largest file in MB. Invalid text is ignored; the value is kept within the server's limits.</summary>
    public string MaxSizeText
    {
        get => _maxSizeText;
        set
        {
            if (!Set(ref _maxSizeText, value)) return;
            if (int.TryParse(value, out var mb) && mb > 0) _server.Settings.MaxFileSizeMb = mb;
        }
    }

    // ---------------------------------------------------------------- sending

    private void OnError(Exception ex) => StatusMessage = $"{Loc.Instance["Error.Unexpected"]} {ex.Message}";

    private async Task SendAsync(Targets target)
    {
        var ids = target switch
        {
            Targets.Selected => _selectedDevices(),
            Targets.Group => _selectedGroup() is { } group
                ? [.. _server.Devices.Where(d => d.Computer.GroupId == group && d.Online && d.IsApproved).Select(d => d.DeviceId)]
                : [],
            _ => [.. _server.Devices.Where(d => d.Online && d.IsApproved).Select(d => d.DeviceId)],
        };
        if (ids.Count == 0)
        {
            StatusMessage = Loc.Instance[target == Targets.Group && _selectedGroup() is null ? "Error.NoGroup" : "Error.NoTarget"];
            return;
        }

        var files = await _dialogs.PickFilesAsync(Loc.Instance["Files.Pick"]).ConfigureAwait(true);
        if (files.Count == 0) return;

        using var cts = new CancellationTokenSource();
        lock (_batchGate) _batches.Add(cts);
        RaiseSending();
        try
        {
            var done = 0;
            var total = 0;
            foreach (var file in files)
            {
                var results = await _server.SendFileToManyAsync(ids, file, cts.Token).ConfigureAwait(true);
                total += results.Count;
                done += results.Count(r => r.Result.State == TransferState.Completed);
            }
            StatusMessage = Loc.Instance.Format("Files.Done", done, total);
        }
        finally
        {
            lock (_batchGate) _batches.Remove(cts);
            RaiseSending();
        }
    }

    private void CancelSending()
    {
        lock (_batchGate)
        {
            foreach (var cts in _batches) cts.Cancel();
        }
    }

    private void RaiseSending()
    {
        _ui.Post(() =>
        {
            Raise(nameof(IsSending));
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        });
    }

    // ---------------------------------------------------------------- list

    private void OnTransferChanged(object? sender, TransferUpdate update) => _ui.Post(() =>
    {
        if (_byId.TryGetValue(update.Progress.TransferId, out var item))
        {
            item.Update(update.Progress);
        }
        else
        {
            item = new TransferItemViewModel(update, _dialogs.OpenFile);
            _byId[update.Progress.TransferId] = item;
            Items.Insert(0, item);
            Trim();
            Raise(nameof(HasItems));
        }
        if (update.Progress is { Direction: TransferDirection.Receiving, State: TransferState.Completed })
            FileReceived?.Invoke(this, update);
    });

    private void Trim()
    {
        while (Items.Count > KeepItems)
        {
            var oldest = Items.LastOrDefault(i => i.IsFinished);
            if (oldest is null) return;
            Items.Remove(oldest);
            _byId.Remove(oldest.TransferId);
        }
    }

    private void ClearFinished()
    {
        foreach (var item in Items.Where(i => i.IsFinished).ToList())
        {
            Items.Remove(item);
            _byId.Remove(item.TransferId);
        }
        Raise(nameof(HasItems));
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => _ui.Post(() =>
    {
        foreach (var item in Items) item.RefreshLanguage();
    });

    public void Dispose()
    {
        _server.TransferChanged -= OnTransferChanged;
        Loc.Instance.PropertyChanged -= OnLanguageChanged;
        CancelSending();
    }
}
