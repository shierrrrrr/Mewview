using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Mewview.Models;
using SkiaSharp;

namespace Mewview.Services;

/// <summary>
/// Rasterizes the edit state (annotations + crop) onto a copy of the original
/// image and encodes the result to disk. The original bitmap is never touched;
/// export is the only place where state becomes pixels.
/// </summary>
public static class AnnotationRenderer
{
    /// <summary>
    /// Renders original + annotations, applies the crop region (if any) and saves.
    /// Format is chosen by file extension: .png (default), .jpg/.jpeg, .webp.
    /// </summary>
    public static void SaveToFile(
        BitmapSource original,
        IReadOnlyList<Annotation> annotations,
        Rect? cropRegion,
        string path)
    {
        using var output = RenderToImage(original, annotations, cropRegion);

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var (format, quality) = ext switch
        {
            ".jpg" or ".jpeg" => (SKEncodedImageFormat.Jpeg, 92),
            ".webp" => (SKEncodedImageFormat.Webp, 90),
            _ => (SKEncodedImageFormat.Png, 100),
        };

        using var data = output.Encode(format, quality);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        data.SaveTo(stream);
    }

    /// <summary>
    /// Renders the final image (original + annotations + crop) to PNG bytes,
    /// e.g. for putting the composed result on the clipboard.
    /// </summary>
    public static byte[] RenderToPngBytes(
        BitmapSource original,
        IReadOnlyList<Annotation> annotations,
        Rect? cropRegion)
    {
        using var output = RenderToImage(original, annotations, cropRegion);
        using var data = output.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Composites original + annotations and applies the crop. Caller disposes.</summary>
    private static SKImage RenderToImage(
        BitmapSource original,
        IReadOnlyList<Annotation> annotations,
        Rect? cropRegion)
    {
        using var bitmap = BitmapConversion.ToSKBitmap(original);
        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height));
        var canvas = surface.Canvas;

        if (cropRegion is { } crop)
        {
            canvas.ClipRect(new SKRect(
                (float)crop.X, (float)crop.Y,
                (float)crop.Right, (float)crop.Bottom));
        }

        canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));

        foreach (var annotation in annotations)
            DrawAnnotation(canvas, annotation);

        // Snapshot the (possibly clipped) result.
        var snapshot = surface.Snapshot();
        if (cropRegion is not { } c)
            return snapshot;

        // Clamp to the bitmap; a degenerate region falls back to the full image.
        var rect = new SKRectI(
            Math.Clamp((int)c.X, 0, bitmap.Width - 1),
            Math.Clamp((int)c.Y, 0, bitmap.Height - 1),
            Math.Clamp((int)c.Right, 1, bitmap.Width),
            Math.Clamp((int)c.Bottom, 1, bitmap.Height));
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            return snapshot;

        var subset = snapshot.Subset(rect);
        snapshot.Dispose();
        return subset ?? throw new InvalidOperationException("Crop region is outside the image.");
    }

    private static void DrawAnnotation(SKCanvas canvas, Annotation annotation)
    {
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            Color = new SKColor(annotation.Color.R, annotation.Color.G, annotation.Color.B, annotation.Color.A),
            StrokeWidth = (float)annotation.StrokeWidth,
        };

        switch (annotation)
        {
            case LineAnnotation line:
                canvas.DrawLine(
                    (float)line.Start.X, (float)line.Start.Y,
                    (float)line.End.X, (float)line.End.Y, paint);
                break;

            case ArrowAnnotation arrow:
                canvas.DrawLine(
                    (float)arrow.Start.X, (float)arrow.Start.Y,
                    (float)arrow.End.X, (float)arrow.End.Y, paint);
                var (w1, w2) = ArrowGeometry.HeadPoints(arrow.Start, arrow.End, arrow.StrokeWidth);
                // Chunky filled triangle head (same geometry as the WPF overlay).
                using (var head = new SKPath())
                {
#pragma warning disable CS0618 // SKPath builder methods: fine for this tiny fixed triangle
                    head.MoveTo((float)arrow.End.X, (float)arrow.End.Y);
                    head.LineTo((float)w1.X, (float)w1.Y);
                    head.LineTo((float)w2.X, (float)w2.Y);
                    head.Close();
#pragma warning restore CS0618
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawPath(head, paint);
                    paint.Style = SKPaintStyle.Stroke;
                }
                break;

            case RectangleAnnotation rect:
                var skRect = new SKRect(
                    (float)rect.Region.X, (float)rect.Region.Y,
                    (float)rect.Region.Right, (float)rect.Region.Bottom);
                canvas.DrawRect(skRect, paint);
                break;

            case TextAnnotation text:
                DrawText(canvas, text);
                break;
        }
    }

    private static void DrawText(SKCanvas canvas, TextAnnotation text)
    {
        // Microsoft YaHei covers CJK on every Windows install; fall back to default.
        using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei") ?? SKTypeface.Default;
        using var font = new SKFont(typeface, (float)text.FontSize);
        using var fill = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(text.Color.R, text.Color.G, text.Color.B, text.Color.A),
        };
        // Position is the top-left of the text; DrawText wants the baseline.
        float baseline = (float)text.Position.Y - font.Metrics.Ascent;
        canvas.DrawText(text.Text, (float)text.Position.X, baseline, SKTextAlign.Left, font, fill);
    }
}
