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

/// <summary>Keeps the stream smooth without turning it into a blur. Two different problems are told apart:
/// a slow link (sending takes long) lowers quality, then size, but never below a floor relative to what the teacher asked for;
/// a slow computer (capturing and encoding take long) only lowers the frame rate, because less quality would not make it faster.
/// Everything recovers step by step when the problem goes away.</summary>
public sealed class AdaptiveQuality
{
    public const int MinQuality = 40;
    public const int MinWidth = 320;
    public const int MinFps = 3;
    private const double DegradeAbove = 0.8;
    private const double RecoverBelow = 0.35;
    private const int RecoverAfterFrames = 10;
    private const double Smoothing = 0.3;
    private const double WidthFloor = 0.7;
    private const int QualityDrop = 30;

    private readonly int _targetFps, _targetQuality, _targetWidth;
    private readonly int _qualityFloor, _widthFloor;
    private double _emaTotal, _emaSend;
    private int _calm;

    public AdaptiveQuality(int fps, int quality, int maxWidth)
    {
        _targetFps = Fps = fps;
        _targetQuality = Quality = quality;
        _targetWidth = MaxWidth = maxWidth;
        _qualityFloor = Math.Min(quality, Math.Max(MinQuality, quality - QualityDrop));
        _widthFloor = Math.Min(maxWidth, Math.Max(MinWidth, (int)(maxWidth * WidthFloor)));
    }

    public int Fps { get; private set; }
    public int Quality { get; private set; }
    public int MaxWidth { get; private set; }
    public bool Degraded => Fps < _targetFps || Quality < _targetQuality || MaxWidth < _targetWidth;

    /// <summary>Call after every frame: <paramref name="total"/> is the whole capture + encode + send time, <paramref name="send"/> only the sending part.</summary>
    public void Report(TimeSpan total, TimeSpan send)
    {
        var interval = 1000.0 / Fps;
        _emaTotal = Smooth(_emaTotal, total.TotalMilliseconds);
        _emaSend = Smooth(_emaSend, send.TotalMilliseconds);

        var slowLink = _emaSend > interval * DegradeAbove;
        var slowComputer = _emaTotal > interval * DegradeAbove;

        if (slowLink || slowComputer)
        {
            _calm = 0;
            if (slowLink && Quality > _qualityFloor) Quality = Math.Max(_qualityFloor, Quality - 10);
            else if (slowLink && MaxWidth > _widthFloor) MaxWidth = Math.Max(_widthFloor, (int)(MaxWidth * 0.9));
            else if (Fps > MinFps) Fps = Math.Max(MinFps, Fps / 2);
            else if (Fps > 1) Fps = 1;
            _emaTotal = _emaSend = 0;
            return;
        }

        if (!Degraded) { _calm = 0; return; }

        // Would the frame rate fit again if it were doubled? Sending is judged against the current interval.
        var fasterInterval = 1000.0 / Math.Min(_targetFps, Math.Max(1, Fps) * 2);
        var canRaiseFps = Fps < _targetFps && _emaTotal < fasterInterval * DegradeAbove;
        var linkIsCalm = _emaSend < interval * RecoverBelow;
        if (!canRaiseFps && !(linkIsCalm && (MaxWidth < _targetWidth || Quality < _targetQuality))) { _calm = 0; return; }
        if (++_calm < RecoverAfterFrames) return;
        _calm = 0;
        if (canRaiseFps) Fps = Math.Min(_targetFps, Math.Max(1, Fps) * 2);
        else if (MaxWidth < _targetWidth) MaxWidth = Math.Min(_targetWidth, (int)Math.Ceiling(MaxWidth / 0.9));
        else Quality = Math.Min(_targetQuality, Quality + 10);
    }

    private static double Smooth(double ema, double value) => ema == 0 ? value : ema * (1 - Smoothing) + value * Smoothing;
}
