namespace ClassroomControl.Shared.Communication.Messages;

// File transfer. The same messages are used in both directions; the receiving side decides whether to accept.

/// <summary>Announces a file. <c>Sha256</c> is the lower-case hex digest of the whole file.</summary>
public sealed record FileOfferMessage(string TransferId, string FileName, long Size, string Sha256);

public sealed record FileReplyMessage(string TransferId, bool Accepted, string? ErrorCode, string? Message);

/// <summary>Chunks are sent strictly in order starting with index 0.</summary>
public sealed record FileChunkMessage(string TransferId, int Index, string DataBase64);

public sealed record FileEndMessage(string TransferId, int ChunkCount);

public sealed record FileCancelMessage(string TransferId, string? Reason);

/// <summary>Final verdict of the receiver after the size and the SHA-256 digest were verified.</summary>
public sealed record FileResultMessage(string TransferId, bool Success, string? ErrorCode, string? Message, string? SavedName);
