using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomControl.Shared.Communication.Messages;

namespace ClassroomControl.Wpf
{
    /// <summary>A decoded frame region, ready to be written into the bitmap on the UI thread.</summary>
    public sealed class DecodedRegion
    {
        public DecodedRegion(ScreenFrameMessage frame, byte[] pixels, int stride)
        {
            Frame = frame;
            Pixels = pixels;
            Stride = stride;
        }

        public ScreenFrameMessage Frame { get; }
        public byte[] Pixels { get; }
        public int Stride { get; }
    }

    /// <summary>Rebuilds the remote screen from key frames and changed regions. <see cref="Decode"/> may run on any thread;
    /// <see cref="Apply"/> must run on the thread that owns the bitmap (the UI thread).</summary>
    public sealed class FrameCompositor
    {
        private const int MaxDimension = 16384;

        public WriteableBitmap? Bitmap { get; private set; }
        public int FullWidth => Bitmap?.PixelWidth ?? 0;
        public int FullHeight => Bitmap?.PixelHeight ?? 0;

        public static DecodedRegion? Decode(ScreenFrameMessage frame)
        {
            if (frame.FullWidth is < 1 or > MaxDimension || frame.FullHeight is < 1 or > MaxDimension
                || frame.Width < 1 || frame.Height < 1 || frame.X < 0 || frame.Y < 0
                || frame.X + frame.Width > frame.FullWidth || frame.Y + frame.Height > frame.FullHeight) return null;
            try
            {
                var bytes = Convert.FromBase64String(frame.ImageBase64);
                using (var stream = new MemoryStream(bytes))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgr32, null, 0);
                    if (converted.PixelWidth != frame.Width || converted.PixelHeight != frame.Height) return null;
                    var stride = frame.Width * 4;
                    var pixels = new byte[stride * frame.Height];
                    converted.CopyPixels(pixels, stride, 0);
                    return new DecodedRegion(frame, pixels, stride);
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is NotSupportedException || ex is InvalidOperationException || ex is IOException)
            {
                return null;
            }
        }

        /// <summary>Returns true when the bitmap changed. A region that arrives before any key frame (or after a size change) is skipped.</summary>
        public bool Apply(DecodedRegion region)
        {
            var f = region.Frame;
            if (Bitmap is null || Bitmap.PixelWidth != f.FullWidth || Bitmap.PixelHeight != f.FullHeight)
            {
                if (!f.KeyFrame) return false;
                Bitmap = new WriteableBitmap(f.FullWidth, f.FullHeight, 96, 96, PixelFormats.Bgr32, null);
            }
            Bitmap.WritePixels(new Int32Rect(f.X, f.Y, f.Width, f.Height), region.Pixels, region.Stride, 0);
            return true;
        }

        public void Reset() => Bitmap = null;
    }
}
