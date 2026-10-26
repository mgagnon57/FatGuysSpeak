#if WINDOWS
using SysDrawing = System.Drawing;
using SysImaging = System.Drawing.Imaging;

namespace Squelch.Client.Services;

/// <summary>Downscales a captured frame to the adaptive max width and JPEG-encodes it for the stream.
/// Shared by Share Screen (GDI) and Stream Game (Windows.Graphics.Capture).</summary>
internal static class JpegFrameEncoder
{
    private static readonly SysImaging.ImageCodecInfo JpegCodec =
        SysImaging.ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == SysImaging.ImageFormat.Jpeg.Guid);

    /// <summary>Encodes and disposes <paramref name="raw"/>.</summary>
    public static byte[] Encode(SysDrawing.Bitmap raw, int maxWidth, int jpegQuality)
    {
        int srcW = raw.Width, srcH = raw.Height;
        int tw = Math.Min(srcW, maxWidth);
        int th = (int)Math.Round(tw * (double)srcH / srcW);

        SysDrawing.Bitmap target;
        if (tw == srcW && th == srcH)
        {
            target = raw;
        }
        else
        {
            target = new SysDrawing.Bitmap(tw, th);
            using var g = SysDrawing.Graphics.FromImage(target);
            g.InterpolationMode = SysDrawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.DrawImage(raw, 0, 0, tw, th);
        }

        try
        {
            var ep = new SysImaging.EncoderParameters(1);
            ep.Param[0] = new SysImaging.EncoderParameter(SysImaging.Encoder.Quality, (long)jpegQuality);
            using var ms = new MemoryStream();
            target.Save(ms, JpegCodec, ep);
            return ms.ToArray();
        }
        finally
        {
            raw.Dispose();
            if (!ReferenceEquals(target, raw)) target.Dispose();
        }
    }
}
#endif
