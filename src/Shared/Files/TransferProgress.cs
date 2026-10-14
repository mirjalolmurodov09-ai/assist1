namespace ClassroomControl.Shared.Files;

public enum TransferDirection { Sending, Receiving }

public enum TransferState { Preparing, Waiting, Transferring, Completed, Failed, Cancelled }

/// <summary>Snapshot of one transfer for the UI. <c>Path</c> is the stored file (receiving) or the source file (sending).</summary>
public sealed record TransferProgress(
    string TransferId,
    TransferDirection Direction,
    string FileName,
    long Size,
    long Transferred,
    TransferState State,
    string? ErrorCode = null,
    string? Message = null,
    string? Path = null)
{
    public bool IsFinished => State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled;
    public double Fraction => Size <= 0 ? (State == TransferState.Completed ? 1 : 0) : Math.Clamp((double)Transferred / Size, 0, 1);
}
