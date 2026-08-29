using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AlienForge.Core.Imaging;

/// <summary>
/// Decodes a DDS through the Windows Imaging Component.
/// </summary>
/// <remarks>
/// Used only for BC7 and BC6H. Both could be written out by hand, but BC7 alone
/// carries eight block modes plus sixty-four partition tables, and a single wrong
/// table entry corrupts a handful of blocks quietly rather than failing loudly.
/// Windows ships Microsoft's own decoder for these, so that is what gets used.
/// BC1 through BC5 are simple enough to decode directly and do not come here.
/// </remarks>
public static class WicDecoder
{
    /// <summary>Loads any image WIC understands, used for comparing against references.</summary>
    public static RgbaImage? TryDecodeFile(string path, out string? error)
    {
        error = null;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return TryDecode(bytes, out error);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    public static RgbaImage? TryDecode(byte[] ddsBytes, out string? error)
    {
        error = null;
        try
        {
            using var stream = new MemoryStream(ddsBytes, writable: false);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                error = "WIC не вернул ни одного кадра.";
                return null;
            }

            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != PixelFormats.Bgra32)
                frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0.0);

            int width = frame.PixelWidth;
            int height = frame.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                error = "WIC вернул кадр нулевого размера.";
                return null;
            }

            int stride = width * 4;
            var buffer = new byte[stride * height];
            frame.CopyPixels(buffer, stride, 0);

            var image = new RgbaImage(width, height);
            // WIC hands back BGRA; swap into RGBA.
            for (int i = 0; i < buffer.Length; i += 4)
            {
                image.Pixels[i + 0] = buffer[i + 2];
                image.Pixels[i + 1] = buffer[i + 1];
                image.Pixels[i + 2] = buffer[i + 0];
                image.Pixels[i + 3] = buffer[i + 3];
            }
            return image;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }
}
