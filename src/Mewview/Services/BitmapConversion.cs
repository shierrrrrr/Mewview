using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Mewview.Services;

/// <summary>WPF BitmapSource ↔ SkiaSharp conversions, shared by OCR and export.</summary>
public static class BitmapConversion
{
    /// <summary>Copies a BitmapSource into a new SKBitmap (BGRA). Caller owns the result.</summary>
    public static SKBitmap ToSKBitmap(BitmapSource source)
    {
        // SkiaSharp wants straight (un-premultiplied) alpha. Pbgra32 therefore has to
        // be converted too: passing premultiplied bytes to an Unpremul surface darkens
        // every translucent pixel. JPEG-XR and clipboard images arrive as Pbgra32.
        BitmapSource bgra = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            bgra = converted;
        }

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }
}
