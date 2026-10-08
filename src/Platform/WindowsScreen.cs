using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClassroomControl.Platform;

/// <summary>Captures the primary monitor with GDI (works in the interactive user session; the secure desktop is not capturable).</summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenSource : IScreenSource
{
    public string? LastError { get; private set; }

    public RawFrame? Capture(int maxWidth)
    {
        var width = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        var height = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        if (width <= 0 || height <= 0)
        {
            LastError = $"GetSystemMetrics returned {width}x{height}";
            return null;
        }

        try
        {
            using var full = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);

            if (maxWidth > 0 && width > maxWidth)
            {
                var scaledWidth = maxWidth;
                var scaledHeight = Math.Max(1, (int)((long)height * maxWidth / width));
                using var small = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(full, 0, 0, scaledWidth, scaledHeight);
                }
                return ToRaw(small);
            }
            return ToRaw(full);
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or ArgumentException)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return null; // locked workstation / secure desktop / session switch
        }
    }

    private static RawFrame ToRaw(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return new RawFrame(bitmap.Width, bitmap.Height, data.Stride, pixels);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}

/// <summary>JPEG encoder for whole frames or a region of one.</summary>
[SupportedOSPlatform("windows")]
public sealed class JpegFrameEncoder : IFrameEncoder
{
    private static readonly ImageCodecInfo Codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public string Format => "jpeg";

    public byte[] Encode(RawFrame frame, PixelRegion region, int quality)
    {
        var handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            using var whole = new Bitmap(frame.Width, frame.Height, frame.Stride, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
            var isFull = region.X == 0 && region.Y == 0 && region.Width == frame.Width && region.Height == frame.Height;
            using var part = isFull ? null : whole.Clone(new Rectangle(region.X, region.Y, region.Width, region.Height), PixelFormat.Format32bppRgb);
            var image = part ?? whole;

            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
            using var stream = new MemoryStream();
            image.Save(stream, Codec, parameters);
            return stream.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }
}
