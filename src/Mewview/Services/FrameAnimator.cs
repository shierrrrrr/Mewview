using System;
using System.Windows.Threading;
using Mewview.Models;

namespace Mewview.Services;

/// <summary>
/// Drives animated playback. The first frame is assumed to be on screen already;
/// after that every frame owns the clock for as long as its own delay says.
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

    public FrameAnimator()
    {
        // Deliberately not DispatcherPriority.Render: a fast animation at render
        // priority would compete with the very work that draws it.
        _timer = new DispatcherTimer(DispatcherPriority.Normal);
        _timer.Tick += OnTick;
    }

    /// <summary>Index of the frame playback is currently parked on.</summary>
    public int CurrentIndex { get; private set; }

    public bool IsRunning => _source != null && !_suspended;

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

    public void Stop()
    {
        _timer.Stop();
        _source = null;
        _suspended = false;
        CurrentIndex = 0;
    }

    /// <summary>Pauses without losing the position (e.g. the window lost focus).</summary>
    public void Suspend()
    {
        if (_source == null || _suspended) return;
        _suspended = true;
        _timer.Stop();
    }

    public void Resume()
    {
        if (_source == null || !_suspended) return;
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
        CurrentIndex = (CurrentIndex + 1) % _source.FrameCount;
        FrameRequested?.Invoke(this, CurrentIndex);
        Schedule();
    }
}
