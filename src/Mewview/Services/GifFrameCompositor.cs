using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mewview.Services;

/// <summary>
/// Rebuilds the full logical screen for every frame of a GIF.
///
/// WPF's <see cref="GifBitmapDecoder"/> — like the WIC decoder underneath it —
/// hands back the *raw* frames, not the picture a viewer should show. A frame is
/// only the rectangle that changed (its position lives in <c>/imgdesc/Left</c> and
/// <c>/imgdesc/Top</c>), everything the frame does not cover is transparent, and
/// honouring the disposal method is the caller's job. Drawing such a frame as if
/// it were the whole image is what makes an optimised GIF flicker: the picture
/// shrinks to the size of the changed patch, the area around it falls through to
/// the (near-white) background, and transparent holes inside a patch show up as
/// white specks.
///
/// This class draws each frame onto a logical-screen-sized canvas, applying the
/// previous frame's disposal first, so every frame that leaves here is a complete
/// picture of the same size.
/// </summary>
internal static class GifFrameCompositor
{
    /// <summary>One entry of the global colour table.</summary>
    public readonly record struct Rgb(byte R, byte G, byte B);

    /// <summary>
    /// The logical screen: its size, plus the colour that "empty" areas take.
    /// Background is null when the file declares no usable one, in which case
    /// those areas stay transparent.
    /// </summary>
    public readonly record struct ScreenInfo(int Width, int Height, Rgb? Background);

    /// <summary>
    /// The image descriptor of one frame: where it sits, how its area is meant to
    /// be cleaned up afterwards, and whether it declares a transparent colour
    /// index. SourceX/SourceY are the offsets into the frame bitmap, which differ
    /// from X/Y only when a malformed file places a frame partly (or wholly)
    /// outside the canvas.
    /// </summary>
    private readonly record struct FrameLayout(
        int X, int Y, int Width, int Height, int Disposal, bool Transparency,
        int SourceX = 0, int SourceY = 0);

    /// <summary>
    /// Logical screen from the GIF header: 6 bytes of signature, the width and
    /// height as little-endian 16-bit values, then the packed field and the
    /// background colour index. Reading the header directly is deliberate — the
    /// decoder cannot be asked for the screen size, and the frames only describe
    /// their own rectangles.
    /// </summary>
    public static ScreenInfo? TryReadScreen(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[13];
            if (!ReadExactly(stream, header)) return null;

            if (header[0] != (byte)'G' || header[1] != (byte)'I' || header[2] != (byte)'F')
                return null;

            int width = header[6] | (header[7] << 8);
            int height = header[8] | (header[9] << 8);
            if (width <= 0 || height <= 0) return null;

            return new ScreenInfo(width, height, ReadBackground(stream, header[10], header[11]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The colour a "restore to background" disposal paints, and the colour the
    /// untouched part of the first frame sits on: the global colour table entry
    /// the logical screen descriptor points at.
    ///
    /// Null — meaning "leave it transparent" — when the file has no global colour
    /// table or the index falls outside it. The GIF89a specification says such an
    /// index should be zero and ignored, and painting a colour picked out of thin
    /// air would be worse than showing what is behind the image.
    /// </summary>
    private static Rgb? ReadBackground(Stream stream, int packed, int backgroundIndex)
    {
        if ((packed & 0x80) == 0) return null;      // no global colour table
        int entries = 2 << (packed & 0x07);         // 2, 4, 8 ... up to 256
        if (backgroundIndex >= entries) return null;

        // 6 bytes of signature plus 7 of logical screen descriptor, then three
        // bytes per colour table entry.
        if (stream.Seek(13 + (backgroundIndex * 3), SeekOrigin.Begin) < 0) return null;
        var entry = new byte[3];
        return ReadExactly(stream, entry) ? new Rgb(entry[0], entry[1], entry[2]) : null;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk <= 0) return false;
            read += chunk;
        }
        return true;
    }

    /// <summary>
    /// Largest rectangle any frame covers — the fallback canvas when the header
    /// cannot be read.
    /// </summary>
    public static (int Width, int Height) BoundsOf(IReadOnlyList<BitmapSource> frames)
    {
        int width = 0;
        int height = 0;
        foreach (var frame in frames)
        {
            var layout = Describe(frame, int.MaxValue, int.MaxValue);
            width = Math.Max(width, layout.X + layout.Width);
            height = Math.Max(height, layout.Y + layout.Height);
        }
        return (Math.Max(width, 1), Math.Max(height, 1));
    }

    /// <summary>
    /// Draws every frame onto the logical screen. The result is one frozen
    /// BGRA bitmap per frame, all of them <paramref name="width"/> x
    /// <paramref name="height"/>.
    /// </summary>
    public static BitmapSource[] Composite(
        IReadOnlyList<BitmapSource> frames, int width, int height, Rgb? background)
    {
        var layouts = new FrameLayout[frames.Count];
        for (int i = 0; i < frames.Count; i++)
            layouts[i] = Describe(frames[i], width, height);

        // Straight BGRA. A GIF's transparent pixels are meant to be see-through,
        // so nothing starts painted — unless the animation does not use
        // transparency at all. A first frame that declares no transparent colour
        // index is opaque, so whatever it does not cover shows the logical
        // screen's background colour. This is what ffmpeg and Pillow both paint;
        // it only ever shows in the margins of a file that never covers them.
        var canvas = new byte[width * height * 4];
        if (background is { } initial && frames.Count > 0 && !layouts[0].Transparency)
            FillRect(canvas, width, 0, 0, width, height, initial);

        var results = new BitmapSource[frames.Count];
        byte[]? restore = null; // canvas state before the last drawn frame (disposal 3)

        for (int i = 0; i < frames.Count; i++)
        {
            if (i > 0)
                ApplyDisposal(canvas, width, layouts[i - 1], restore, background);

            var layout = layouts[i];

            restore = layout.Disposal == 3
                ? CopyRegion(canvas, width, layout)
                : null;

            Draw(canvas, width, height, frames[i], layout);

            var snapshot = new byte[canvas.Length];
            Buffer.BlockCopy(canvas, 0, snapshot, 0, canvas.Length);
            var frame = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, snapshot, width * 4);
            frame.Freeze();
            results[i] = frame;
        }

        return results;
    }

    private static void ApplyDisposal(
        byte[] canvas, int width, FrameLayout layout, byte[]? restore, Rgb? background)
    {
        switch (layout.Disposal)
        {
            case 2:
                // "Restore to background". A frame that declares a transparent
                // colour index has no background of its own — the area simply
                // stops covering what is behind it, which is what every viewer
                // does, and what the fixtures show for files whose frames carry
                // the flag. Without such a declaration the frame is opaque, so
                // the logical screen's background colour shows through instead;
                // ffmpeg and Pillow both paint it, and it is what the
                // specification describes.
                if (background is { } fill && !layout.Transparency)
                    FillRect(canvas, width, layout.X, layout.Y, layout.Width, layout.Height, fill);
                else
                    Clear(canvas, width, layout);
                break;

            case 3:
                // "Restore to previous": put back what was there before drawing.
                if (restore != null)
                    Paste(canvas, width, restore, layout);
                break;
        }
    }

    /// <summary>
    /// Frame position, size, disposal method and transparency from the GIF block
    /// metadata. A frame with no metadata is assumed to cover the whole canvas.
    /// </summary>
    private static FrameLayout Describe(BitmapSource frame, int canvasWidth, int canvasHeight)
    {
        int rawX = 0;
        int rawY = 0;
        int disposal = 0;
        bool transparency = false;

        if (frame.Metadata is BitmapMetadata meta)
        {
            rawX = Query(meta, "/imgdesc/Left", 0);
            rawY = Query(meta, "/imgdesc/Top", 0);
            disposal = Query(meta, "/grctlext/Disposal", 0);
            transparency = Query(meta, "/grctlext/TransparencyFlag", 0) != 0;
        }

        // Clip to the canvas: a malformed file may place a frame outside it. The
        // clipping has to be mirrored on the source side, otherwise the patch would
        // be read from the wrong offset.
        int x = Math.Max(rawX, 0);
        int y = Math.Max(rawY, 0);
        int sourceX = x - rawX;
        int sourceY = y - rawY;

        int width = Math.Max(0, frame.PixelWidth - sourceX);
        int height = Math.Max(0, frame.PixelHeight - sourceY);
        width = Math.Max(0, Math.Min(width, canvasWidth - x));
        height = Math.Max(0, Math.Min(height, canvasHeight - y));

        return new FrameLayout(x, y, width, height, disposal, transparency, sourceX, sourceY);
    }

    private static int Query(BitmapMetadata meta, string path, int fallback)
    {
        try
        {
            if (!meta.ContainsQuery(path)) return fallback;
            object? value = meta.GetQuery(path);
            return value is IConvertible number
                ? number.ToInt32(CultureInfo.InvariantCulture)
                : fallback;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return fallback;
        }
    }

    /// <summary>Source-over draw of one frame onto the canvas, skipping transparency.</summary>
    private static void Draw(
        byte[] canvas, int canvasWidth, int canvasHeight, BitmapSource frame, FrameLayout layout)
    {
        int sourceStride = frame.PixelWidth * 4;
        var source = new byte[sourceStride * frame.PixelHeight];
        var readable = frame;
        if (frame.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            readable = converted;
        }
        readable.CopyPixels(source, sourceStride, 0);

        for (int row = 0; row < layout.Height; row++)
        {
            int sourceRow = ((layout.SourceY + row) * sourceStride) + (layout.SourceX * 4);
            int canvasRow = (((layout.Y + row) * canvasWidth) + layout.X) * 4;

            for (int col = 0; col < layout.Width; col++)
            {
                int s = sourceRow + (col * 4);
                int a = source[s + 3];
                if (a == 0) continue; // transparent pixels keep whatever is underneath

                int c = canvasRow + (col * 4);
                if (a == 255)
                {
                    canvas[c] = source[s];
                    canvas[c + 1] = source[s + 1];
                    canvas[c + 2] = source[s + 2];
                    canvas[c + 3] = 255;
                    continue;
                }

                int inverse = 255 - a;
                canvas[c] = (byte)(((source[s] * a) + (canvas[c] * inverse)) / 255);
                canvas[c + 1] = (byte)(((source[s + 1] * a) + (canvas[c + 1] * inverse)) / 255);
                canvas[c + 2] = (byte)(((source[s + 2] * a) + (canvas[c + 2] * inverse)) / 255);
                canvas[c + 3] = (byte)Math.Min(255, a + (canvas[c + 3] * inverse) / 255);
            }
        }
    }

    /// <summary>Paints a rectangle with an opaque colour.</summary>
    private static void FillRect(
        byte[] canvas, int canvasWidth, int x, int y, int width, int height, Rgb colour)
    {
        // The canvas is Bgra32, so each pixel is stored blue, green, red, alpha —
        // the colour table reads the other way round and must not be copied in
        // order.
        byte blue = colour.B;
        byte green = colour.G;
        byte red = colour.R;

        for (int row = 0; row < height; row++)
        {
            int start = (((y + row) * canvasWidth) + x) * 4;
            for (int col = 0; col < width; col++)
            {
                int p = start + (col * 4);
                canvas[p] = blue;
                canvas[p + 1] = green;
                canvas[p + 2] = red;
                canvas[p + 3] = 255;
            }
        }
    }

    private static void Clear(byte[] canvas, int canvasWidth, FrameLayout layout)
    {
        for (int row = 0; row < layout.Height; row++)
        {
            int start = (((layout.Y + row) * canvasWidth) + layout.X) * 4;
            Array.Clear(canvas, start, layout.Width * 4);
        }
    }

    private static void Paste(byte[] canvas, int canvasWidth, byte[] region, FrameLayout layout)
    {
        for (int row = 0; row < layout.Height; row++)
        {
            int start = (((layout.Y + row) * canvasWidth) + layout.X) * 4;
            Buffer.BlockCopy(region, row * layout.Width * 4, canvas, start, layout.Width * 4);
        }
    }

    private static byte[] CopyRegion(byte[] canvas, int canvasWidth, FrameLayout layout)
    {
        var region = new byte[layout.Width * layout.Height * 4];
        for (int row = 0; row < layout.Height; row++)
        {
            int start = (((layout.Y + row) * canvasWidth) + layout.X) * 4;
            Buffer.BlockCopy(canvas, start, region, row * layout.Width * 4, layout.Width * 4);
        }
        return region;
    }
}
