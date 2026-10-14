using ClassroomControl.Shared.Communication.Security;
using ClassroomControl.Shared.Files;
using ClassroomControl.StudentAgent.Resources.Strings;
using Microsoft.Extensions.Logging;

namespace ClassroomControl.StudentAgent.Features;

/// <summary>File transfer of the Student Agent. Files from the Teacher are stored in one fixed folder under a safe, unique name and
/// are always announced to the student; the student can also send a file to the Teacher, but only by choosing it themselves.
/// The Teacher can never read files from this computer.</summary>
public interface IFileTransferService
{
    event Action<TransferProgress>? Progress;
    string ReceiveFolder { get; }
    /// <summary>True when the session is connected and this computer is approved by the Teacher.</summary>
    bool CanSend { get; }
    IReadOnlyList<TransferProgress> Recent { get; }
    void OnSessionStarted(SecureChannel channel, RegistrationState registration);
    void OnRegistrationChanged(RegistrationState registration);
    void OnSessionEnded();
    Task HandleAsync(VerifiedMessage message, CancellationToken cancellationToken);
    Task<TransferProgress> SendToTeacherAsync(string filePath, CancellationToken cancellationToken);
}

public sealed class FileTransferService : IFileTransferService
{
    private const int KeepRecent = 20;
    private readonly object _gate = new();
    private readonly FileTransferPolicy _policy;
    private readonly IUserNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<FileTransferService> _logger;
    private readonly List<TransferProgress> _recent = [];
    private FileTransferCoordinator? _coordinator;
    private RegistrationState _registration;

    public FileTransferService(IAppPaths paths, IUserNotifier notifier, TimeProvider time, ILogger<FileTransferService> logger, FileTransferPolicy? policy = null)
    {
        ReceiveFolder = paths.ReceivedFilesDirectory;
        _notifier = notifier;
        _time = time;
        _logger = logger;
        _policy = policy ?? new FileTransferPolicy();
    }

    public event Action<TransferProgress>? Progress;

    public string ReceiveFolder { get; }

    public bool CanSend
    {
        get { lock (_gate) return _coordinator is not null && _registration == RegistrationState.Approved; }
    }

    public IReadOnlyList<TransferProgress> Recent
    {
        get { lock (_gate) return [.. _recent]; }
    }

    public void OnSessionStarted(SecureChannel channel, RegistrationState registration)
    {
        var receiver = new FileReceiver(_policy, ReceiveFolder, _time);
        receiver.DeleteStalePartialFiles(TimeSpan.FromHours(1));
        var coordinator = new FileTransferCoordinator(receiver, new SecureChannelSink(channel), _policy);
        coordinator.Progress += OnProgress;
        FileTransferCoordinator? previous;
        lock (_gate)
        {
            previous = _coordinator;
            _coordinator = coordinator;
            _registration = registration;
        }
        previous?.Dispose();
    }

    public void OnRegistrationChanged(RegistrationState registration)
    {
        lock (_gate) _registration = registration;
    }

    public void OnSessionEnded()
    {
        FileTransferCoordinator? coordinator;
        lock (_gate)
        {
            coordinator = _coordinator;
            _coordinator = null;
        }
        coordinator?.Dispose();
    }

    public async Task HandleAsync(VerifiedMessage message, CancellationToken cancellationToken)
    {
        FileTransferCoordinator? coordinator;
        RegistrationState registration;
        lock (_gate)
        {
            coordinator = _coordinator;
            registration = _registration;
        }
        if (coordinator is null) return;

        // Only an approved computer takes files; a pending one refuses every offer.
        if (registration != RegistrationState.Approved)
        {
            if (message.Type == MessageTypes.FileOffer)
            {
                var offer = MessageSerializer.Deserialize<FileOfferMessage>(message.Payload);
                await coordinator.RefuseOfferAsync(offer.TransferId, ErrorCodes.NotRegistered, UiStrings.FileNotApproved, cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        await coordinator.HandleAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransferProgress> SendToTeacherAsync(string filePath, CancellationToken cancellationToken)
    {
        FileTransferCoordinator? coordinator;
        RegistrationState registration;
        lock (_gate)
        {
            coordinator = _coordinator;
            registration = _registration;
        }
        if (coordinator is null || registration != RegistrationState.Approved)
        {
            var refused = new TransferProgress(Guid.NewGuid().ToString("N"), TransferDirection.Sending, Path.GetFileName(filePath), 0, 0,
                TransferState.Failed, coordinator is null ? "OFFLINE" : ErrorCodes.NotRegistered, UiStrings.FileNoTeacher, filePath);
            OnProgress(refused);
            return refused;
        }
        return await coordinator.SendFileAsync(filePath, cancellationToken).ConfigureAwait(false);
    }

    private void OnProgress(TransferProgress progress)
    {
        lock (_gate)
        {
            var index = _recent.FindIndex(p => p.TransferId == progress.TransferId);
            if (index >= 0) _recent[index] = progress;
            else _recent.Add(progress);
            while (_recent.Count > KeepRecent) _recent.RemoveAt(0);
        }

        if (progress.IsFinished)
        {
            _logger.LogInformation("File {Direction}: {Name} ({Size} bytes) {State} {Code}", progress.Direction, progress.FileName, progress.Size, progress.State, progress.ErrorCode);
            if (progress is { Direction: TransferDirection.Receiving, State: TransferState.Completed })
                SafeNotify(UiStrings.FileReceivedTitle, UiStrings.FileReceived(progress.FileName, ReceiveFolder));
        }
        Progress?.Invoke(progress);
    }

    private void SafeNotify(string title, string text)
    {
        try
        {
            _notifier.Show(title, text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "The file notification could not be shown.");
        }
    }
}
