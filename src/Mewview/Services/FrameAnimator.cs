using System;
using System.Windows.Threading;
using Mewview.Models;

namespace Mewview.Services;

/// <summary>
/// Drives animated playback. The first frame is assumed to be on screen already;
/// after that every frame owns the clock for as long as its own delay says.
///
/// Playback runs through the frames ONCE and stops on the last one: an animation
/// that never ends is hard to look at when the point of the viewer is to study a
/// still picture, so replaying is left to the user (see <see cref="Replay"/>).
/// Suspend/Resume are meant to be wired to window activation so a GIF that is no
/// longer visible stops consuming CPU.
/// </summary>
public sealed class FrameAnimator
{
    /// <summary>Used for a frame whose delay is missing or too small to honour.</summary>
    private static readonly TimeSpan FallbackDelay = TimeSpan.FromMilliseconds(100);

    private readonly DispatcherTimer _timer;
    private IFrameSource? _source;
    private bool _suspended;

    /// <summary>Raised with the index of the frame that should now be displayed.</summary>
    public event EventHandler<int>? FrameRequested;

    /// <summary>Raised once playback has run through the last frame.</summary>
    public event EventHandler? Finished;

    public FrameAnimator()
    {
        // Deliberately not DispatcherPriority.Render: a fast animation at render
        // priority would compete with the very work that draws it.
        _timer = new DispatcherTimer(DispatcherPriority.Normal);
        _timer.Tick += OnTick;
    }

    /// <summary>Index of the frame playback is currently parked on.</summary>
    public int CurrentIndex { get; private set; }

    /// <summary>True while the clock is running, i.e. frames are still coming.</summary>
    public bool IsRunning => _timer.IsEnabled;

    /// <summary>True once a single-shot playback reached the last frame.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>
    /// Begins playback from frame 0. A source with a single frame, or one that is
    /// not animated at all, is ignored.
    /// </summary>
    public void Start(IFrameSource source)
    {
        Stop();
        if (!source.IsAnimated || source.FrameCount < 2) return;
        _source = source;
        CurrentIndex = 0;
        Schedule();
    }

    /// <summary>
    /// Plays the current source again from the start. Used by the "click the image
    /// to watch it once more" gesture, so the source is kept after finishing.
    /// </summary>
    public void Replay()
    {
        if (_source == null) return;
        _suspended = false;
        IsFinished = false;
        CurrentIndex = 0;
        FrameRequested?.Invoke(this, 0); // the last frame is on screen; go back to the first
        Schedule();
    }

    public void Stop()
    {
        _timer.Stop();
        _source = null;
        _suspended = false;
        IsFinished = false;
        CurrentIndex = 0;
    }

    /// <summary>Pauses without losing the position (e.g. the window lost focus).</summary>
    public void Suspend()
    {
        if (_source == null || _suspended || IsFinished) return;
        _suspended = true;
        _timer.Stop();
    }

    public void Resume()
    {
        // A finished animation stays finished: reactivating the window must not
        // restart something the user has already seen.
        if (_source == null || !_suspended || IsFinished) return;
        _suspended = false;
        Schedule();
    }

    private void Schedule()
    {
        if (_source == null) return;
        var delay = _source.GetDelay(CurrentIndex);
        // A frame with no usable delay would turn the timer into a busy loop.
        _timer.Interval = delay > TimeSpan.Zero ? delay : FallbackDelay;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_source == null)
        {
            _timer.Stop();
            return;
        }

        int next = CurrentIndex + 1;
        if (next >= _source.FrameCount)
        {
            // The last frame has had its turn: park on it and stop.
            _timer.Stop();
            IsFinished = true;
            Finished?.Invoke(this, EventArgs.Empty);
            return;
        }

        CurrentIndex = next;
        FrameRequested?.Invoke(this, CurrentIndex);
        Schedule();
    }
}
