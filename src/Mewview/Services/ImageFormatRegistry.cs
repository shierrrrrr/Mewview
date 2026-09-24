using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mewview.Services;

/// <summary>Which decoder handles a format.</summary>
public enum DecodeEngine
{
    /// <summary>WPF's built-in WIC decoders (BitmapImage, TiffBitmapDecoder, GifBitmapDecoder, ...).</summary>
    Wic,

    /// <summary>SkiaSharp SKCodec — for containers WPF's decoders reject, e.g. WebP.</summary>
    Skia,
}

/// <summary>
/// How many images a file holds — this decides which decoder produces the
/// displayed frame.
/// </summary>
public enum FrameKind
{
    /// <summary>Exactly one image.</summary>
    Single,

    /// <summary>Multi-page TIFF. Pages are meant to be navigated manually.</summary>
    Tiff,

    /// <summary>GIF; may carry several animation frames.</summary>
    Gif,

    /// <summary>ICO: several sizes of one icon, smallest first — show the largest.</summary>
    Icon,
}

/// <summary>
/// One supported image format.
/// </summary>
public sealed record ImageFormat
{
    /// <summary>Extensions including the leading dot, e.g. [".jpg", ".jpeg"].</summary>
    public required string[] Extensions { get; init; }

    /// <summary>Label used in the "save as" dialog, e.g. "JPEG".</summary>
    public required string DisplayName { get; init; }

    public required DecodeEngine Engine { get; init; }

    public required FrameKind FrameKind { get; init; }

    /// <summary>True when Ctrl+S may overwrite the original file, i.e. an encoder exists for it.</summary>
    public bool CanOverwrite { get; init; }

    /// <summary>True when the format is offered in the "save as" dialog.</summary>
    public bool CanSaveAs { get; init; }

    /// <summary>
    /// Store search term for a codec this format needs but the machine may not have.
    /// Null for formats the viewer decodes by itself. HEIC/AVIF rely on Windows'
    /// optional imaging extensions, so the app has to be able to say what to install;
    /// it never installs anything on its own, it only offers to open the Store page.
    /// </summary>
    public string? CodecStoreSearch { get; init; }

    public bool Matches(string? extension) =>
        extension is not null &&
        Extensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The single source of truth for supported formats: extension lists, dialog
/// filters, decode dispatch and save capability all derive from this table.
/// No other file should hard-code an extension.
/// </summary>
public static class ImageFormatRegistry
{
    /// <summary>Supported formats, in the order their extensions appear in the open dialog.</summary>
    public static readonly IReadOnlyList<ImageFormat> All = new ImageFormat[]
    {
        new()
        {
            Extensions = [".jpg", ".jpeg"], DisplayName = "JPEG",
            Engine = DecodeEngine.Wic, FrameKind = FrameKind.Single,
            CanOverwrite = true, CanSaveAs = true,
        },
        new()
        {
            Extensions = [".png"], DisplayName = "PNG",
            Engine = DecodeEngine.Wic, FrameKind = FrameKind.Single,
            CanOverwrite = true, CanSaveAs = true,
        },
        new()
        {
            Extensions = [".webp"], DisplayName = "WebP",
            Engine = DecodeEngine.Skia, FrameKind = FrameKind.Single,
            CanOverwrite = true, CanSaveAs = true,
        },
        new()
        {
            Extensions = [".bmp"], DisplayName = "BMP",
            Engine = DecodeEngine.Wic, FrameKind = FrameKind.Single,
            CanOverwrite = false, CanSaveAs = false,
        },

        // Appended after BMP so the first four entries of the open-dialog filter keep
        // their old order. None of these has an encoder here, so saving one behaves
        // exactly like BMP already does: Ctrl+S falls through to "save as".
        new()
        {
            Extensions = [".tif", ".tiff"], DisplayName = "TIFF",
            Engine = DecodeEngine.Wic, FrameKind = FrameKind.Tiff,
            CanOverwrite = false, CanSaveAs = false,
        },
        new()
        {
            Extensions = [".gif"], DisplayName = "GIF",
            Engine = DecodeEngine.Wic, FrameKind = FrameKind.Gif,
            CanOverwrite = false, CanSaveAs = false,
        },
        new()
        {
            Extensions = [".ico"], DisplayName = "ICO",
            Engine = DecodeEngine.Wic, FrameKind = FrameKind.Icon,
            CanOverwrite = false, CanSaveAs = false,
        },
        new()
        {
            // Same codec; .wdp is the original Windows Media Photo spelling.
            Extensions = [".jxr", ".wdp"], DisplayName = "JPEG XR",
            Engine = DecodeEngine.Wic, FrameKind = FrameKind.Single,
            CanOverwrite = false, CanSaveAs = false,
        },
        new()
        {
            // Decoded by Windows' optional imaging extensions rather than by WPF
            // itself, so these open only on machines that have them installed.
            // The decoder hands back Bgr32: an HEIC alpha auxiliary image is dropped.
            Extensions = [".heic", ".heif"], DisplayName = "HEIC",
            Engine = DecodeEngine.Wic, FrameKind = FrameKind.Single,
            CanOverwrite = false, CanSaveAs = false,
            CodecStoreSearch = "HEIF 图像扩展",
        },
        new()
        {
            // AVIF is a HEIF container holding AV1, so it needs the AV1 decoder too.
            Extensions = [".avif"], DisplayName = "AVIF",
            Engine = DecodeEngine.Wic, FrameKind = FrameKind.Single,
            CanOverwrite = false, CanSaveAs = false,
            CodecStoreSearch = "AV1 视频扩展",
        },
    };

    /// <summary>
    /// Display order of the "save as" filter. PNG leads because it is the dialog's
    /// DefaultExt, so the dialog must open with it selected.
    /// </summary>
    private static readonly string[] SaveFilterOrder = { ".png", ".jpg", ".webp" };

    public static IEnumerable<string> AllExtensions => All.SelectMany(f => f.Extensions);

    public static ImageFormat? Find(string path) => FindByExtension(Path.GetExtension(path));

    public static ImageFormat? FindByExtension(string? extension) =>
        string.IsNullOrEmpty(extension) ? null : All.FirstOrDefault(f => f.Matches(extension));

    public static bool IsSupported(string path) => Find(path) is not null;

    /// <summary>"Images (*.jpg;*.jpeg;...)|*.jpg;*.jpeg;...|All files (*.*)|*.*"</summary>
    public static string BuildOpenFilter()
    {
        var joined = string.Join(";", AllExtensions.Select(e => "*" + e));
        return $"Images ({joined})|{joined}|All files (*.*)|*.*";
    }

    /// <summary>
    /// "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|...". Only formats with an
    /// encoder appear; each pattern follows that format's first extension, which is
    /// the one its encoder writes.
    /// </summary>
    public static string BuildSaveFilter() =>
        string.Join("|", SaveFilterOrder
            .Select(FindByExtension)
            .Where(f => f is { CanSaveAs: true })
            .Select(f =>
            {
                var pattern = "*" + f!.Extensions[0];
                return $"{f.DisplayName} image ({pattern})|{pattern}";
            }));
}
