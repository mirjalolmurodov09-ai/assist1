namespace ClassroomControl.Shared.Files;

public static class FileTransferLimits
{
    /// <summary>Raw bytes per chunk. Base64 + JSON + signature stay far below the 4 MiB frame limit.</summary>
    public const int ChunkBytes = 192 * 1024;
    public const int MaxChunkBytes = 256 * 1024;
    public const int MaxTransferIdLength = 64;
    public const int MaxFileNameLength = 120;
}

/// <summary>What a computer is willing to receive. Every receiver enforces its own policy; the sender cannot change it.</summary>
public sealed record FileTransferPolicy
{
    public static readonly IReadOnlySet<string> ExecutableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msp", ".bat", ".cmd", ".com", ".scr", ".pif", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".lnk", ".reg", ".jar", ".dll", ".sys", ".cpl", ".msc", ".appx", ".msix", ".application", ".gadget", ".inf",
    };

    public long MaxFileBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Program and script files are refused unless this is switched on explicitly.</summary>
    public bool AllowExecutables { get; init; }

    /// <summary>Free disk space that must remain after the file is stored.</summary>
    public long MinFreeBytes { get; init; } = 100L * 1024 * 1024;

    public int MaxConcurrentTransfers { get; init; } = 3;

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public bool IsBlocked(string fileName) => !AllowExecutables && ExecutableExtensions.Contains(Path.GetExtension(fileName));
}
