using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mewview.Models;

public enum AnnotationTool
{
    None,
    Line,
    Arrow,
    Rectangle,
    Crop,
    Text
}

public abstract record Annotation(Color Color, double StrokeWidth);

public sealed record LineAnnotation(Point Start, Point End, Color Color, double StrokeWidth)
    : Annotation(Color, StrokeWidth);

public sealed record ArrowAnnotation(Point Start, Point End, Color Color, double StrokeWidth)
    : Annotation(Color, StrokeWidth);

public sealed record RectangleAnnotation(Rect Region, Color Color, double StrokeWidth)
    : Annotation(Color, StrokeWidth);

public sealed record TextAnnotation(Point Position, string Text, Color Color, double FontSize)
    : Annotation(Color, FontSize);

public static class ArrowGeometry
{
    public static (Point Wing1, Point Wing2) HeadPoints(Point start, Point tip, double strokeWidth)
    {
        Vector dir = tip - start;
        double length = dir.Length;
        if (length < 1e-6)
        {
            return (Wing1: tip, Wing2: tip);
        }
        dir.Normalize();
        double headLen = Math.Min(Math.Max(strokeWidth * 6.0, 16.0), length * 0.7);
        double cos = Math.Cos(0.5759586531581288);
        double sin = Math.Sin(0.5759586531581288);
        Vector back = -dir;
        Point wing1 = new Point(
            tip.X + headLen * (back.X * cos - back.Y * sin),
            tip.Y + headLen * (back.X * sin + back.Y * cos));
        Point wing2 = new Point(
            tip.X + headLen * (back.X * cos + back.Y * sin),
            tip.Y + headLen * (-back.X * sin + back.Y * cos));
        return (Wing1: wing1, Wing2: wing2);
    }
}

public interface IEditCommand
{
    void Apply(AnnotationSession session);

    void Revert(AnnotationSession session);
}

public sealed class AddAnnotationCommand : IEditCommand
{
    private readonly Annotation _annotation;

    public AddAnnotationCommand(Annotation annotation)
    {
        _annotation = annotation;
    }

    public void Apply(AnnotationSession session)
    {
        session.AddCore(_annotation);
    }

    public void Revert(AnnotationSession session)
    {
        session.RemoveCore(_annotation);
    }
}

public sealed class SetCropCommand : IEditCommand
{
    private readonly Rect? _oldRegion;
    private readonly Rect? _newRegion;

    public SetCropCommand(Rect? oldRegion, Rect? newRegion)
    {
        _oldRegion = oldRegion;
        _newRegion = newRegion;
    }

    public void Apply(AnnotationSession session)
    {
        session.SetCropCore(_newRegion);
    }

    public void Revert(AnnotationSession session)
    {
        session.SetCropCore(_oldRegion);
    }
}

public sealed class AnnotationSession
{
    private readonly List<Annotation> _annotations = new();
    private readonly Stack<IEditCommand> _undoStack = new();
    private readonly Stack<IEditCommand> _redoStack = new();

    public BitmapSource Original { get; }

    public IReadOnlyList<Annotation> Annotations => _annotations;

    public Rect? CropRegion { get; private set; }

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    public event EventHandler? StateChanged;

    public AnnotationSession(BitmapSource original)
    {
        Original = original ?? throw new ArgumentNullException(nameof(original));
    }

    public void AddAnnotation(Annotation annotation)
    {
        Execute(new AddAnnotationCommand(annotation));
    }

    public void SetCrop(Rect region)
    {
        Execute(new SetCropCommand(CropRegion, region));
    }

    public void Undo()
    {
        if (_undoStack.Count != 0)
        {
            IEditCommand command = _undoStack.Pop();
            command.Revert(this);
            _redoStack.Push(command);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Redo()
    {
        if (_redoStack.Count != 0)
        {
            IEditCommand command = _redoStack.Pop();
            command.Apply(this);
            _undoStack.Push(command);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Execute(IEditCommand command)
    {
        command.Apply(this);
        _undoStack.Push(command);
        _redoStack.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void AddCore(Annotation annotation)
    {
        _annotations.Add(annotation);
    }

    internal void RemoveCore(Annotation annotation)
    {
        _annotations.Remove(annotation);
    }

    internal void SetCropCore(Rect? region)
    {
        CropRegion = region;
    }
}
