namespace ClassroomControl.Shared.Screen;

public readonly record struct DiffResult(bool Changed, bool KeyFrame, PixelRegion Region);

/// <summary>Finds what changed between two consecutive screen captures. Compares 32x32 tiles and returns the bounding box of the
/// changed tiles, so only that region is encoded and sent (dirty-region / differential update).</summary>
public sealed class FrameDiffer
{
    public const int TileSize = 32;
    private const double KeyFrameCoverage = 0.6;
    private RawFrame? _previous;

    public void Reset() => _previous = null;

    public DiffResult Compare(RawFrame current, bool forceKeyFrame)
    {
        var full = new PixelRegion(0, 0, current.Width, current.Height);
        var previous = _previous;
        _previous = current;

        if (forceKeyFrame || previous is null || previous.Width != current.Width || previous.Height != current.Height || previous.Stride != current.Stride)
            return new DiffResult(true, true, full);

        int minTx = int.MaxValue, minTy = int.MaxValue, maxTx = -1, maxTy = -1;
        var tilesX = (current.Width + TileSize - 1) / TileSize;
        var tilesY = (current.Height + TileSize - 1) / TileSize;
        for (var ty = 0; ty < tilesY; ty++)
        {
            for (var tx = 0; tx < tilesX; tx++)
            {
                if (!TileDiffers(previous, current, tx, ty)) continue;
                if (tx < minTx) minTx = tx;
                if (tx > maxTx) maxTx = tx;
                if (ty < minTy) minTy = ty;
                if (ty > maxTy) maxTy = ty;
            }
        }

        if (maxTx < 0) return new DiffResult(false, false, default);

        var x = minTx * TileSize;
        var y = minTy * TileSize;
        var region = new PixelRegion(x, y, Math.Min(current.Width, (maxTx + 1) * TileSize) - x, Math.Min(current.Height, (maxTy + 1) * TileSize) - y);
        return region.Area >= full.Area * KeyFrameCoverage
            ? new DiffResult(true, true, full)
            : new DiffResult(true, false, region);
    }

    private static bool TileDiffers(RawFrame a, RawFrame b, int tx, int ty)
    {
        var x = tx * TileSize;
        var y = ty * TileSize;
        var width = Math.Min(TileSize, b.Width - x);
        var height = Math.Min(TileSize, b.Height - y);
        var bytes = width * 4;
        for (var row = 0; row < height; row++)
        {
            var offset = (y + row) * b.Stride + x * 4;
            if (!a.Pixels.AsSpan(offset, bytes).SequenceEqual(b.Pixels.AsSpan(offset, bytes))) return true;
        }
        return false;
    }
}

/// <summary>Lowers quality, then size, then frame rate when sending a frame takes too long for the current frame interval
/// (busy Wi-Fi, slow student PC), and recovers step by step when the link is fast again.</summary>
public sealed class AdaptiveQuality
{
    public const int MinQuality = 30;
    public const int MinWidth = 320;
    private const double DegradeAbove = 0.8;
    private const double RecoverBelow = 0.35;
    private const int RecoverAfterFrames = 10;
    private const double Smoothing = 0.3;

    private readonly int _targetFps, _targetQuality, _targetWidth;
    private double _ema;
    private int _calm;

    public AdaptiveQuality(int fps, int quality, int maxWidth)
    {
        _targetFps = Fps = fps;
        _targetQuality = Quality = quality;
        _targetWidth = MaxWidth = maxWidth;
    }

    public int Fps { get; private set; }
    public int Quality { get; private set; }
    public int MaxWidth { get; private set; }
    public bool Degraded => Fps < _targetFps || Quality < _targetQuality || MaxWidth < _targetWidth;

    /// <summary>Call after every frame with the time spent capturing, encoding and sending it.</summary>
    public void Report(TimeSpan cost)
    {
        var interval = 1000.0 / Fps;
        _ema = _ema == 0 ? cost.TotalMilliseconds : _ema * (1 - Smoothing) + cost.TotalMilliseconds * Smoothing;

        if (_ema > interval * DegradeAbove)
        {
            _calm = 0;
            if (Quality > MinQuality) Quality = Math.Max(MinQuality, Quality - 10);
            else if (MaxWidth > MinWidth) MaxWidth = Math.Max(MinWidth, (int)(MaxWidth * 0.8));
            else if (Fps > 1) Fps = Math.Max(1, Fps / 2);
            _ema = 0;
        }
        else if (Degraded && _ema < interval * RecoverBelow)
        {
            if (++_calm < RecoverAfterFrames) return;
            _calm = 0;
            if (Fps < _targetFps) Fps = Math.Min(_targetFps, Fps * 2);
            else if (MaxWidth < _targetWidth) MaxWidth = Math.Min(_targetWidth, (int)(MaxWidth / 0.8));
            else if (Quality < _targetQuality) Quality = Math.Min(_targetQuality, Quality + 10);
        }
        else
        {
            _calm = 0;
        }
    }
}
