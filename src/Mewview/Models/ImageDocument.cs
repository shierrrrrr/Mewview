using System;
using System.Windows.Media.Imaging;

namespace Mewview.Models;

/// <summary>
/// The frames a loaded file holds, plus how they should be presented.
/// Implementations own whatever resources they need (a decoder, a file stream)
/// and release them from <see cref="Dispose"/>.
/// </summary>
public interface IFrameSource : IDisposable
{
    /// <summary>Total number of frames; always 1 for a single-image format.</summary>
    int FrameCount { get; }

    /// <summary>
    /// True when the user is meant to step through the frames one at a time
    /// (multi-page TIFF). Such a source carries no playback timing.
    /// </summary>
    bool IsPaged { get; }

    /// <summary>True when the frames should advance on a timer (animated GIF).</summary>
    bool IsAnimated { get; }

    /// <summary>The frame to display at the given index.</summary>
    BitmapSource GetFrame(int index);

    /// <summary>How long the frame at the given index stays on screen.</summary>
    TimeSpan GetDelay(int index);
}

/// <summary>
/// A file that has been opened for viewing: its frames plus the rules that
/// follow from them. Disposing releases the underlying resources — a multi-page
/// TIFF keeps its file stream open for as long as it is the current document.
/// </summary>
public sealed class ImageDocument : IDisposable
{
    private readonly IFrameSource _frames;

    private ImageDocument(IFrameSource frames) => _frames = frames;

    /// <summary>
    /// Wraps a bitmap that is already in memory — every single-image format, and
    /// clipboard pastes. Nothing to release.
    /// </summary>
    public static ImageDocument SingleFrame(BitmapSource frame) => new(new SingleFrameSource(frame));

    public static ImageDocument MultiFrame(IFrameSource frames) => new(frames);

    public IFrameSource Frames => _frames;

    public int FrameCount => _frames.FrameCount;

    /// <summary>Steppable pages (multi-page TIFF) — these get the frame navigation bar.</summary>
    public bool IsPaged => _frames.IsPaged;

    /// <summary>Frames that advance by themselves (animated GIF).</summary>
    public bool IsAnimated => _frames.IsAnimated;

    /// <summary>
    /// Annotations need one stable bitmap: a page turn or an animation tick would
    /// invalidate every shape already drawn. Single-frame files only.
    /// </summary>
    public bool SupportsAnnotation => _frames.FrameCount == 1;

    public BitmapSource GetFrame(int index) => _frames.GetFrame(index);

    public TimeSpan GetFrameDelay(int index) => _frames.GetDelay(index);

    public void Dispose() => _frames.Dispose();
}

/// <summary>Exactly one bitmap, already in memory — nothing to dispose.</summary>
internal sealed class SingleFrameSource : IFrameSource
{
    private readonly BitmapSource _frame;

    public SingleFrameSource(BitmapSource frame) => _frame = frame;

    public int FrameCount => 1;

    public bool IsPaged => false;

    public bool IsAnimated => false;

    public BitmapSource GetFrame(int index) => _frame;

    public TimeSpan GetDelay(int index) => TimeSpan.Zero;

    public void Dispose() { }
}
