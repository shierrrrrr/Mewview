using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mewview.Models;

namespace Mewview.Services;

/// <summary>
/// Turns a lazily-decoded decoder frame into a self-contained frozen bitmap.
/// </summary>
internal static class FrameMaterializer
{
    /// <summary>
    /// Copies the frame's pixels into a standalone bitmap, scaling it down first
    /// when the frame exceeds <paramref name="maxPixels"/>.
    ///
    /// A single page of a multi-page TIFF cannot go through
    /// BitmapImage.DecodePixelWidth — that switch only works for a whole file — so
    /// the memory ceiling has to be enforced once the page is in memory. Copying
    /// also detaches the result from the decoder's stream, so closing the file can
    /// never invalidate a page that is currently on screen.
    /// </summary>
    public static BitmapSource Materialize(BitmapSource frame, long maxPixels)
    {
        BitmapSource source = frame;
        long pixels = (long)frame.PixelWidth * frame.PixelHeight;
        if (pixels > maxPixels)
        {
            double ratio = Math.Sqrt((double)maxPixels / pixels);
            source = new FormatConvertedBitmap(
                new TransformedBitmap(frame, new ScaleTransform(ratio, ratio)),
                PixelFormats.Bgra32, null, 0);
        }

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = (width * source.Format.BitsPerPixel + 7) / 8;
        var buffer = new byte[stride * height];
        source.CopyPixels(buffer, stride, 0);

        // DPI is written as 96 on purpose: the viewer ignores a file's DPI metadata
        // and maps one image pixel to one screen pixel (see ImageCanvas).
        var result = BitmapSource.Create(
            width, height, 96, 96, source.Format, source.Palette, buffer, stride);
        result.Freeze();
        return result;
    }
}

/// <summary>
/// The pages of a multi-page TIFF. Pages are decoded one at a time, on demand, so
/// a 100-page scan never has to fit in memory at once — which in turn means the
/// file stream has to stay open for as long as this source lives.
///
/// Only page 1 is decoded up-front, and it leaves the loading thread as a frozen
/// bitmap. Everything else waits, because a <see cref="BitmapDecoder"/> is a
/// DispatcherObject: one created on the loading thread throws the moment the UI
/// thread touches it. The decoder is therefore built on first use, which is always
/// the thread that asked for a page.
/// </summary>
internal sealed class PagedFrameSource : IFrameSource
{
    private readonly Stream _stream;
    private readonly int _frameCount;
    private readonly BitmapSource _firstPage;
    private BitmapDecoder? _decoder;

    private PagedFrameSource(Stream stream, int frameCount, BitmapSource firstPage)
    {
        _stream = stream;
        _frameCount = frameCount;
        _firstPage = firstPage;
    }

    /// <summary>
    /// Opens the file and decodes page 1. Returns null when there is only one page,
    /// so the caller falls back to the eager single-image path — the only one that
    /// gets BitmapImage.DecodePixelWidth, and therefore the only one that keeps a
    /// 400 dpi single-page scan inside the memory budget.
    /// </summary>
    public static PagedFrameSource? TryOpen(string path)
    {
        // Two streams on purpose. The probe is closed as soon as page 1 is in memory;
        // the second one is held for the later pages, so the file stays locked while
        // it is on screen exactly as before. Sharing one stream would make the
        // decoder's read position depend on how far the probe got.
        var keepOpen = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            int frameCount;
            BitmapSource firstPage;
            using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var decoder = BitmapDecoder.Create(
                    probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                frameCount = decoder.Frames.Count;
                if (frameCount <= 1)
                {
                    keepOpen.Dispose();
                    return null;
                }

                firstPage = FrameMaterializer.Materialize(
                    decoder.Frames[0], ImageService.MaxDecodePixels);
            }
            return new PagedFrameSource(keepOpen, frameCount, firstPage);
        }
        catch
        {
            keepOpen.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Deliberately not <see cref="BitmapCacheOption.OnLoad"/>: that would decode
    /// every page of a long scan into memory, which is what this class exists to
    /// avoid.
    /// </summary>
    private BitmapDecoder Decoder() =>
        _decoder ??= BitmapDecoder.Create(
            _stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

    public int FrameCount => _frameCount;

    public bool IsPaged => true;

    public bool IsAnimated => false;

    public BitmapSource GetFrame(int index) =>
        index == 0
            ? _firstPage
            : FrameMaterializer.Materialize(Decoder().Frames[index], ImageService.MaxDecodePixels);

    public TimeSpan GetDelay(int index) => TimeSpan.Zero;

    public void Dispose()
    {
        _decoder = null;
        _stream.Dispose();
    }
}

/// <summary>
/// The animation frames of a GIF, decoded up-front. Playback revisits every frame
/// on each loop, so keeping them resident is what makes it smooth. A file whose
/// frames would not fit the budget is shown as a still instead — see
/// <c>ImageService</c>.
/// </summary>
internal sealed class AnimatedFrameSource : IFrameSource
{
    private readonly BitmapSource[] _frames;
    private readonly TimeSpan[] _delays;

    public AnimatedFrameSource(BitmapSource[] frames, TimeSpan[] delays)
    {
        _frames = frames;
        _delays = delays;
    }

    public int FrameCount => _frames.Length;

    public bool IsPaged => false;

    public bool IsAnimated => _frames.Length > 1;

    public BitmapSource GetFrame(int index) => _frames[index];

    public TimeSpan GetDelay(int index) => _delays[index];

    public void Dispose() { }
}
