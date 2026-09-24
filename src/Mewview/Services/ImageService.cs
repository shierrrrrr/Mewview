using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mewview.Models;
using SkiaSharp;

namespace Mewview.Services;

/// <summary>
/// Image loading and folder browsing.
/// The supported extension list, the decode dispatch and the frame-selection rule
/// all come from <see cref="ImageFormatRegistry"/>, the single source of truth.
/// Very large images are downscaled at decode time to bound memory usage.
/// </summary>
public static class ImageService
{
    /// <summary>All extensions the viewer accepts, in dialog filter order.</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        ImageFormatRegistry.AllExtensions.ToList();

    /// <summary>
    /// Above this pixel count an image is decoded at a reduced resolution.
    /// Shared with the frame sources, which have to enforce the same ceiling after
    /// a single page has been decoded.
    /// This is a budget rather than a hard cap: the codecs round the scaled
    /// dimensions, so the result can land a fraction of a percent above it.
    /// </summary>
    internal const long MaxDecodePixels = 16_000_000; // ~64 MB BGRA upper bound

    /// <summary>
    /// Worst-case byte budget for keeping every frame of an animation resident.
    /// GIF frames are palette-based, so the real footprint is usually a quarter of
    /// this estimate; a file that still exceeds it is shown as a still instead.
    /// </summary>
    private const long MaxAnimationBytes = 512L * 1024 * 1024;

    public static bool IsSupported(string path) => ImageFormatRegistry.IsSupported(path);

    /// <summary>
    /// True when a file could not be opened because this machine has no decoder for
    /// its container, as opposed to the file itself being unreadable. The two cases
    /// need different messages: only one of them is fixable by installing a codec.
    /// <para>
    /// All three conditions have to hold. The extension must name a format whose
    /// codec comes from the system, so a stray non-image file never triggers it. The
    /// content must be an ISO base media container, which is what HEIC/AVIF are —
    /// a failed download saved as .heic is an HTML page and deserves the ordinary
    /// message. And WIC must report WINCODEC_ERR_COMPONENTNOTFOUND (0x88982F50),
    /// which is what "nothing claims this stream" looks like; a damaged file inside a
    /// recognised container reports UNKNOWNIMAGEFORMAT instead and is not caught here.
    /// </para>
    /// </summary>
    public static bool IsMissingCodecError(string path, Exception ex) =>
        ex is NotSupportedException &&
        ImageFormatRegistry.Find(path)?.CodecStoreSearch is not null &&
        HasIsoMediaHeader(path) &&
        (ex.InnerException is null ||
         (ex.InnerException is COMException com && (uint)com.HResult == 0x88982F50));

    /// <summary>
    /// ISO base media files (HEIC, HEIF, AVIF) begin with an "ftyp" box, whose four
    /// type bytes sit at offset 4. Only the header is read.
    /// </summary>
    private static bool HasIsoMediaHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[8];
            int read = 0;
            while (read < header.Length)
            {
                int chunk = stream.Read(header, read, header.Length - read);
                if (chunk <= 0) return false;
                read += chunk;
            }

            return header[4] == (byte)'f' && header[5] == (byte)'t'
                && header[6] == (byte)'y' && header[7] == (byte)'p';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a file for viewing, keeping whatever frame machinery it needs.
    /// The caller owns the result and must dispose it when the image is replaced.
    /// </summary>
    public static ImageDocument LoadDocument(string path)
    {
        var format = ImageFormatRegistry.Find(path);
        if (format is { FrameKind: FrameKind.Tiff or FrameKind.Gif }
            && TryLoadMultiFrame(path, format) is { } multiFrame)
        {
            return multiFrame;
        }
        return ImageDocument.SingleFrame(Load(path));
    }

    /// <summary>
    /// Probes a TIFF/GIF for extra frames and, when there are some, builds the
    /// matching frame source. Returns null for a single-frame file so the caller
    /// falls back to the established eager path — which is the only one that gets
    /// BitmapImage.DecodePixelWidth, and therefore the only one that keeps a
    /// 400 dpi single-page scan inside the memory budget.
    /// </summary>
    private static ImageDocument? TryLoadMultiFrame(string path, ImageFormat format)
    {
        if (format.FrameKind == FrameKind.Gif)
        {
            // Pass one, header only: frame count and per-frame dimensions, no pixels.
            int frameCount;
            long estimatedBytes = 0;
            using (var header = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var probe = BitmapDecoder.Create(
                    header, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                frameCount = probe.Frames.Count;
                if (frameCount <= 1) return null;

                foreach (var frame in probe.Frames)
                {
                    estimatedBytes += (long)frame.PixelWidth * frame.PixelHeight * 4;
                }
            }

            if (estimatedBytes > MaxAnimationBytes)
                return null; // too big to animate — show the first frame as a still

            return ImageDocument.MultiFrame(LoadAnimatedGif(path, frameCount));
        }

        if (format.FrameKind != FrameKind.Tiff) return null;

        // Multi-page TIFF does its own single pass — it has to decode page 1 while
        // still on the loading thread, and it is the only place that knows how.
        return PagedFrameSource.TryOpen(path) is { } paged
            ? ImageDocument.MultiFrame(paged)
            : null;
    }

    /// <summary>
    /// Decodes every GIF frame and its delay up-front. OnLoad is used rather than
    /// the lazy mode because playback revisits each frame on every loop.
    /// </summary>
    private static AnimatedFrameSource LoadAnimatedGif(string path, int frameCount)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new GifBitmapDecoder(
            stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        // The header pass already counted the frames; trust it only as an upper
        // bound in case the file is malformed.
        int count = Math.Min(frameCount, decoder.Frames.Count);
        var frames = new BitmapSource[count];
        var delays = new TimeSpan[count];
        for (int i = 0; i < count; i++)
        {
            var decoded = decoder.Frames[i];

            // Read the timing off the decoded frame: the metadata does not survive
            // the copy below.
            delays[i] = ReadGifDelay(decoded);

            // Every frame has to be frozen before it leaves this method. The load may
            // well have run on a background thread, and an unfrozen frame cannot be
            // touched by the UI thread at all. Materializing is the fallback for a
            // frame that refuses to freeze.
            BitmapSource frame = decoded;
            if (decoded.CanFreeze)
                decoded.Freeze();
            else
                frame = FrameMaterializer.Materialize(decoded, MaxDecodePixels);
            frames[i] = frame;
        }
        return new AnimatedFrameSource(frames, delays);
    }

    /// <summary>
    /// Frame delay from the GIF Graphic Control Extension, expressed in hundredths
    /// of a second. The CLR type this comes back as is not part of any contract —
    /// encoders differ — so the value is converted rather than pattern-matched.
    /// A delay of 0 or 1 means "as fast as possible"; browsers clamp those to
    /// 100 ms, and so does this, so such a file animates instead of
    /// spinning the timer.
    /// </summary>
    private static TimeSpan ReadGifDelay(BitmapSource frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.ContainsQuery("/grctlext/Delay"))
            {
                object? raw = meta.GetQuery("/grctlext/Delay");
                int hundredths = raw is IConvertible number
                    ? number.ToInt32(System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                if (hundredths >= 2)
                    return TimeSpan.FromMilliseconds(hundredths * 10);
            }
        }
        catch (NotSupportedException) { }
        catch (ArgumentException) { }
        return TimeSpan.FromMilliseconds(100);
    }

    /// <summary>
    /// Loads the frame that should be displayed, as a frozen BitmapSource. Throws on
    /// failure; the caller is responsible for error reporting.
    /// Convenience for single-frame callers — for anything that may hold several
    /// frames use <see cref="LoadDocument"/>, which also hands out the lifetime.
    /// </summary>
    public static BitmapSource Load(string path)
    {
        var format = ImageFormatRegistry.Find(path);
        if (format is { Engine: DecodeEngine.Skia })
            return LoadWithSkia(path);
        return LoadWithWic(path, format?.FrameKind ?? FrameKind.Single);
    }

    private static BitmapSource LoadWithWic(string path, FrameKind frameKind)
    {
        // The icon decoder lists sizes smallest-first, so its Frames[0] is the 16x16
        // variant; the largest frame is the only one worth showing.
        if (frameKind == FrameKind.Icon)
            return LoadLargestIconFrame(path);

        // Tiff and Gif fall through to here and surface their first page/frame.
        // BitmapImage is used instead of an explicit decoder because it forwards
        // DecodePixelWidth to the codec, which keeps a 400 dpi scan inside the budget
        // set by MaxDecodePixels; an explicit decoder materialises the full-resolution
        // page in memory before anything can be scaled.
        return LoadFirstFrameWithBitmapImage(path);
    }

    private static BitmapSource LoadFirstFrameWithBitmapImage(string path)
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

    /// <summary>
    /// ICO files bundle several sizes of a single icon. The largest frame is the only
    /// one that survives being scaled to a window, so that is what gets displayed.
    /// </summary>
    private static BitmapSource LoadLargestIconFrame(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new IconBitmapDecoder(
            stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        // OnLoad means the pixels are already cached, so the frame stays valid after
        // the stream is closed as this method returns.
        var frame = decoder.Frames
            .OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight)
            .First();
        if (frame.CanFreeze)
            frame.Freeze();
        return frame;
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

        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(IsSupported)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed — no permission, drive removed while
            // browsing — must not stop the image itself from opening. Browsing just
            // stays put on this one file.
            return new[] { path };
        }
    }
}
