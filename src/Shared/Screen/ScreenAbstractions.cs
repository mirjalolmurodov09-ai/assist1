namespace ClassroomControl.Shared.Screen;

/// <summary>Raw screen pixels, BGRA 32-bit, top-down. <see cref="Width"/>/<see cref="Height"/> are the (possibly downscaled) size.</summary>
public sealed record RawFrame(int Width, int Height, int Stride, byte[] Pixels);

public readonly record struct PixelRegion(int X, int Y, int Width, int Height)
{
    public long Area => (long)Width * Height;
}

/// <summary>Captures the primary screen. Implementations are Windows-specific (GDI); tests use a synthetic screen.</summary>
public interface IScreenSource
{
    /// <summary>Returns the screen scaled to at most <paramref name="maxWidth"/> pixels wide (0 = native size), or null when the
    /// screen cannot be captured right now (secure desktop, locked session...).</summary>
    RawFrame? Capture(int maxWidth);

    /// <summary>Reason the last <see cref="Capture"/> returned null (for diagnostics), if known.</summary>
    string? LastError => null;
}

public interface IFrameEncoder
{
    string Format { get; }
    byte[] Encode(RawFrame frame, PixelRegion region, int quality);
}

