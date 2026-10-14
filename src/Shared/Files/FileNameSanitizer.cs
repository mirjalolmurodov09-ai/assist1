using System.Globalization;
using System.Text;

namespace ClassroomControl.Shared.Files;

/// <summary>Turns a name received from the network into one safe file name: no folders, no device names, no hidden characters.</summary>
public static class FileNameSanitizer
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] Invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*".ToCharArray()).Distinct().ToArray();

    public static string Sanitize(string? name)
    {
        name ??= string.Empty;
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) name = name[(slash + 1)..];

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            var category = char.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) continue; // incl. right-to-left override
            builder.Append(Array.IndexOf(Invalid, ch) >= 0 ? '_' : ch);
        }

        var clean = builder.ToString().Trim().TrimEnd('.', ' ');
        clean = clean.TrimStart('.'); // no hidden/dot files, no "..", no ".ssh"-style names
        if (clean.Length == 0) return "file";

        var extension = Path.GetExtension(clean);
        var stem = clean[..^extension.Length];
        if (extension.Length > 20) { stem = clean; extension = string.Empty; }
        if (Reserved.Contains(stem)) stem = "_" + stem;
        if (stem.Length + extension.Length > FileTransferLimits.MaxFileNameLength)
            stem = stem[..Math.Max(1, FileTransferLimits.MaxFileNameLength - extension.Length)];
        stem = stem.TrimEnd('.', ' ');
        return (stem.Length == 0 ? "file" : stem) + extension;
    }

    /// <summary>Creates a new empty file with the given name or, when it exists, "name (1).ext", "name (2).ext"... Never overwrites.</summary>
    public static string CreateUniqueFile(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var candidate = Path.Combine(directory, attempt == 0 ? fileName : $"{stem} ({attempt}){extension}");
            try
            {
                using (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Taken: try the next number.
            }
        }
        throw new IOException("Could not find a free file name.");
    }
}
