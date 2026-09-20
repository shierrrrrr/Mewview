using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Mewview.Services;

/// <summary>
/// Image loading and folder browsing.
/// JPG/PNG/BMP decode via built-in WIC; WEBP decodes via SkiaSharp.
/// Very large images are downscaled at decode time to bound memory usage.
/// </summary>
public static class ImageService
{
    public static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

    /// <summary>Above this pixel count the image is decoded at a reduced resolution.</summary>
    private const long MaxDecodePixels = 16_000_000; // ~64 MB BGRA upper bound

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return SupportedExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Loads an image into a frozen BitmapSource. Throws on failure;
    /// the caller is responsible for error reporting.
    /// </summary>
    public static BitmapSource Load(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            return LoadWithSkia(path);
        return LoadWithWic(path);
    }

    private static BitmapSource LoadWithWic(string path)
    {
        // Probe dimensions on a separate stream — reusing the same stream for both
        // probing and decoding corrupts the decode (partial/garbled image).
        int width, height;
        using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            width = decoder.Frames[0].PixelWidth;
            height = decoder.Frames[0].PixelHeight;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if ((long)width * height > MaxDecodePixels)
        {
            double ratio = Math.Sqrt((double)MaxDecodePixels / ((long)width * height));
            if (width >= height)
                bitmap.DecodePixelWidth = Math.Max(1, (int)(width * ratio));
            else
                bitmap.DecodePixelHeight = Math.Max(1, (int)(height * ratio));
        }
        bitmap.EndInit();
        bitmap.StreamSource.Dispose();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource LoadWithSkia(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec == null)
            throw new InvalidDataException("Unable to decode this image.");

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);

        // Downscale at decode time when the image is very large.
        if ((long)info.Width * info.Height > MaxDecodePixels)
        {
            double ratio = Math.Sqrt((double)MaxDecodePixels / ((long)info.Width * info.Height));
            info = new SKImageInfo(
                Math.Max(1, (int)(info.Width * ratio)),
                Math.Max(1, (int)(info.Height * ratio)),
                SKColorType.Bgra8888, SKAlphaType.Unpremul);
        }

        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            throw new InvalidDataException("Unable to decode this image.");

        var source = BitmapSource.Create(
            info.Width, info.Height, 96, 96,
            PixelFormats.Bgra32, null,
            bitmap.GetPixels(), info.Height * info.RowBytes, info.RowBytes);
        source.Freeze();
        return source;
    }

    /// <summary>
    /// Returns the sorted list of supported images in the same folder as the given file.
    /// </summary>
    public static IReadOnlyList<string> GetImagesInSameFolder(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return new[] { path };
        return Directory.EnumerateFiles(dir)
            .Where(IsSupported)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
