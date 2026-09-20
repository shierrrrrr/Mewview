using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Mewview.Models;

namespace Mewview.Controls;

public enum ViewMode { Free, Fit, Actual }

/// <summary>
/// A finished annotation drag, expressed in DISPLAYED-image pixels (the bitmap
/// currently shown — when a crop is active this is the cropped view; the host
/// adds the crop offset back to get original-image coordinates).
/// </summary>
public sealed class AnnotationDraftEventArgs : EventArgs
{
    public AnnotationDraftEventArgs(AnnotationTool tool, Point start, Point end)
    {
        Tool = tool;
        Start = start;
        End = end;
    }
    public AnnotationTool Tool { get; }
    public Point Start { get; }
    public Point End { get; }
}

/// <summary>A click with the Text tool, in displayed-image pixels.</summary>
public sealed class TextInputRequestedEventArgs : EventArgs
{
    public TextInputRequestedEventArgs(Point position) => Position = position;
    public Point Position { get; }
}

/// <summary>Committed text plus its position in displayed-image pixels.</summary>
public sealed class TextCommittedEventArgs : EventArgs
{
    public TextCommittedEventArgs(Point position, string text, double fontSize)
    {
        Position = position;
        Text = text;
        FontSize = fontSize;
    }
    public Point Position { get; }
    public string Text { get; }

    /// <summary>Final font size in image pixels (may have been wheel-adjusted).</summary>
    public double FontSize { get; }
}

/// <summary>
/// Image viewport: wheel zoom (anchored at cursor), left-drag pan,
/// double-click toggles 100%/Fit. An overlay canvas sharing the same transform
/// hosts committed annotations and the rubber band while drawing.
/// The displayed image is never modified.
/// </summary>
public class ImageCanvas : Grid
{
    private const double MinScale = 0.02;
    private const double MaxScale = 64.0;
    private const double WheelFactor = 1.25;
    private const double MinDragPixels = 3.0;
    private const double MinFontSize = 6.0;
    private const double MaxFontSize = 500.0;
    private const double FontSizeWheelFactor = 1.1;

    private readonly Image _image;
    private readonly Canvas _overlay;
    private readonly MatrixTransform _transform = new();

    private double _scale = 1.0;
    private double _tx;
    private double _ty;

    private bool _panning;
    private Point _panStart;
    private double _panStartTx;
    private double _panStartTy;

    // Annotation draft state
    private bool _drafting;
    private Point _draftStartView;
    private Shape? _rubberBand;

    // Text editing state
    private TextBox? _textEditor;
    private Point _textEditorPosPx;
    private double _textFontSizePx = 28.0; // remembered across editors

    /// <summary>True until the open-time zoom rule has been applied for the current image.</summary>
    private bool _pendingInitialView;

    public ViewMode Mode { get; private set; } = ViewMode.Fit;
    public double Zoom => _scale;
    public bool HasImage => _image.Source != null;

    /// <summary>Active annotation tool; None = pan/zoom viewing.</summary>
    public AnnotationTool ActiveTool
    {
        get => _activeTool;
        set
        {
            _activeTool = value;
            // Crop mode dims the whole image; the dragged selection stays bright.
            if (value == AnnotationTool.Crop) ShowCropDim();
            else HideCropDim();
        }
    }
    private AnnotationTool _activeTool = AnnotationTool.None;

    /// <summary>Style used for the rubber band preview (committed shapes are set by the host).</summary>
    public Color DraftColor { get; set; } = Color.FromRgb(0xE5, 0x39, 0x35);
    public double DraftStrokeWidth { get; set; } = 3.0; // image pixels at 100%

    // Crop rubber band: thin double-dash outline (white + phase-shifted black),
    // visible on any background; never follows the annotation pickers.
    private const double CropRubberWidth = 2.0; // image pixels at 100%
    private Path? _cropDim;           // darkens the image; a punched hole follows the selection
    private Rectangle? _cropOutlineW; // white dashes under the phase-shifted black ones

    /// <summary>Current text font size in image pixels; remembered across editors.</summary>
    public double TextFontSize
    {
        get => _textFontSizePx;
        set => _textFontSizePx = Math.Clamp(value, MinFontSize, MaxFontSize);
    }

    /// <summary>Raised whenever zoom/pan changes so the host can update its status display.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>Raised when an annotation drag finishes (coords in displayed-image pixels).</summary>
    public event EventHandler<AnnotationDraftEventArgs>? DraftCommitted;

    /// <summary>Raised when the Text tool clicks the image (coords in displayed-image pixels).</summary>
    public event EventHandler<TextInputRequestedEventArgs>? TextInputRequested;

    /// <summary>Raised when the inline text editor commits non-empty text.</summary>
    public event EventHandler<TextCommittedEventArgs>? TextCommitted;

    public ImageCanvas()
    {
        _image = new Image
        {
            // The element size is forced to pixel*96/monitorDpi (see UpdatePixelMapping),
            // so Fill simply renders the bitmap into that exact box.
            Stretch = Stretch.Fill,
            RenderTransform = _transform
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        _overlay = new Canvas
        {
            RenderTransform = _transform,
            IsHitTestVisible = false, // mouse events belong to the viewport grid
            ClipToBounds = true,      // hide annotation parts outside the (cropped) image
        };

        // A WPF Canvas measures children with infinite space, so the Image keeps its
        // full natural size instead of being clipped to the viewport by the Grid.
        var host = new Canvas();
        host.Children.Add(_image);
        host.Children.Add(_overlay);
        Children.Add(host);
        ClipToBounds = true;

        MouseWheel += OnMouseWheel;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseMove += OnMouseMove;
        LostMouseCapture += (s, e) => { EndPan(); CancelDraft(); };
        SizeChanged += (s, e) =>
        {
            if (!HasImage) return;
            // The open-time rule has priority until it has been applied once
            // against a real viewport size.
            if (_pendingInitialView)
            {
                UpdatePixelMapping(); // DPI is reliable only once we are laid out
                ApplyInitialView();
                return;
            }
            if (Mode == ViewMode.Fit) ZoomToFit();
            else if (Mode == ViewMode.Actual) ZoomTo100();
            else ReanchorToCenter(e.PreviousSize); // Free: don't drift on resize
        };

        // Re-map pixels when the window moves to a monitor with a different DPI.
        Loaded += (s, e) =>
        {
            if (_dpiHooked) return;
            _dpiHooked = true;
            var w = Window.GetWindow(this);
            if (w != null) w.DpiChanged += OnHostDpiChanged;
        };
    }

    private bool _dpiHooked;

    private void OnHostDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        if (!HasImage) return;
        UpdatePixelMapping();
        if (Mode == ViewMode.Fit) ZoomToFit();
        else if (Mode == ViewMode.Actual) ZoomTo100();
        else Apply(); // Free: keep the user's scale
    }

    /// <summary>96 / monitor DPI: converts one image pixel into DIPs.</summary>
    private double PixelToDipFactor()
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX;
        return 96.0 / (dpi > 0 ? dpi : 96.0);
    }

    /// <summary>
    /// Forces the render size so that at 100% zoom ONE image pixel maps to ONE
    /// physical screen pixel, ignoring the file's DPI metadata (same behaviour as
    /// the Windows Photos app / WeChat viewer). All view math then works in DIPs
    /// on top of this mapping.
    /// </summary>
    private void UpdatePixelMapping()
    {
        if (!HasImage) return;
        var s = (BitmapSource)_image.Source;
        double k = PixelToDipFactor();
        _image.Width = s.PixelWidth * k;
        _image.Height = s.PixelHeight * k;
        _overlay.Width = _image.Width;
        _overlay.Height = _image.Height;
    }

    public void SetImage(BitmapSource source)
    {
        _image.Source = source;
        _overlay.Children.Clear();
        CancelDraft();
        UpdatePixelMapping();
        _pendingInitialView = true;
        ApplyInitialView();
    }

    public void Clear()
    {
        _image.Source = null;
        _image.Width = double.NaN;
        _image.Height = double.NaN;
        _overlay.Children.Clear();
        CancelDraft();
        _pendingInitialView = false;
        _scale = 1.0;
        _tx = _ty = 0;
        Apply();
        Mode = ViewMode.Fit;
    }

    /// <summary>
    /// Open-time zoom rule (applies to every newly opened image):
    ///   - image smaller than the viewport in BOTH dimensions  -> 100% original size, centered
    ///   - otherwise (either dimension exceeds the viewport)   -> fit to window, centered
    /// If the control has not been laid out yet (e.g. image set before the window is
    /// shown), the decision stays pending and is applied on the first SizeChanged.
    /// </summary>
    private void ApplyInitialView()
    {
        if (!_pendingInitialView || !HasImage) return;
        if (ActualWidth < 1 || ActualHeight < 1) return; // wait for layout

        _pendingInitialView = false;
        var size = ImageSize();
        if (size.Width <= ActualWidth && size.Height <= ActualHeight)
            ZoomTo100();
        else
            ZoomToFit();
    }

    public void ZoomToFit()
    {
        if (!HasImage || ActualWidth < 1 || ActualHeight < 1) return;
        var size = ImageSize();
        _scale = Math.Min(ActualWidth / size.Width, ActualHeight / size.Height);
        _scale = ClampScale(_scale);
        Center(size);
        Mode = ViewMode.Fit;
        Apply();
    }

    public void ZoomTo100()
    {
        if (!HasImage || ActualWidth < 1 || ActualHeight < 1) return;
        var size = ImageSize();
        _scale = 1.0;
        Center(size);
        Mode = ViewMode.Actual;
        Apply();
    }

    private void Center(Size imageSize)
    {
        _tx = (ActualWidth - imageSize.Width * _scale) / 2;
        _ty = (ActualHeight - imageSize.Height * _scale) / 2;
    }

    /// <summary>
    /// Free-mode resize: keep the image point that sat at the OLD viewport center
    /// anchored to the NEW viewport center. Without this the stale translation
    /// (computed for the old viewport) pins the image to the top-left and it
    /// visibly drifts off-center while the window is resized.
    /// </summary>
    private void ReanchorToCenter(Size previous)
    {
        if (previous.Width < 1 || previous.Height < 1) return;
        double ix = (previous.Width / 2 - _tx) / _scale;
        double iy = (previous.Height / 2 - _ty) / _scale;
        _tx = ActualWidth / 2 - ix * _scale;
        _ty = ActualHeight / 2 - iy * _scale;
        Apply();
    }

    /// <summary>
    /// The size the image is rendered at, in DIPs. This is the forced element size
    /// (pixel * 96/monitorDpi), so 100% zoom means 1 image pixel = 1 physical pixel.
    /// </summary>
    private Size ImageSize() => new(_image.Width, _image.Height);

    /// <summary>Converts a point in viewport (control) space to displayed-image pixels.</summary>
    public Point ViewToImagePixel(Point viewPoint)
    {
        double k = PixelToDipFactor();
        return new Point(
            (viewPoint.X - _tx) / _scale / k,
            (viewPoint.Y - _ty) / _scale / k);
    }

    // ---------- Committed annotations ----------

    /// <summary>
    /// Rebuilds the overlay from committed annotations. Coordinates are in
    /// displayed-image pixels (the host subtracts the crop offset beforehand).
    /// </summary>
    public void SetAnnotations(IReadOnlyList<Annotation> annotations)
    {
        _overlay.Children.Clear();
        _textEditor = null; // overlay rebuild drops any open editor
        foreach (var a in annotations)
        {
            if (a is TextAnnotation t)
            {
                _overlay.Children.Add(CreateTextBlock(t));
                continue;
            }
            var (tool, start, end) = a switch
            {
                LineAnnotation l => (AnnotationTool.Line, l.Start, l.End),
                ArrowAnnotation ar => (AnnotationTool.Arrow, ar.Start, ar.End),
                RectangleAnnotation r => (AnnotationTool.Rectangle, r.Region.TopLeft, r.Region.BottomRight),
                _ => (AnnotationTool.None, new Point(), new Point()),
            };
            var shape = CreateShape(tool, start, end, a.Color, a.StrokeWidth, dashed: false);
            if (shape != null)
                _overlay.Children.Add(shape);
        }
    }

    private TextBlock CreateTextBlock(TextAnnotation t)
    {
        double k = PixelToDipFactor();
        var brush = new SolidColorBrush(t.Color);
        brush.Freeze();
        var block = new TextBlock
        {
            Text = t.Text,
            FontFamily = new FontFamily("Microsoft YaHei"),
            FontSize = t.FontSize * k,
            Foreground = brush,
        };
        Canvas.SetLeft(block, t.Position.X * k);
        Canvas.SetTop(block, t.Position.Y * k);
        return block;
    }

    // ---------- Mouse ----------

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!HasImage) return;

        // While the inline text editor is open, the wheel resizes the text
        // (the editor grows with it) instead of zooming the view.
        if (IsEditingText)
        {
            AdjustTextFontSize(e.Delta > 0 ? FontSizeWheelFactor : 1.0 / FontSizeWheelFactor);
            e.Handled = true;
            return;
        }

        double factor = e.Delta > 0 ? WheelFactor : 1.0 / WheelFactor;
        ZoomAt(e.GetPosition(this), _scale * factor);
        e.Handled = true;
    }

    /// <summary>Scales the text font size and applies it to the open editor immediately.</summary>
    private void AdjustTextFontSize(double factor)
    {
        _textFontSizePx = Math.Clamp(_textFontSizePx * factor, MinFontSize, MaxFontSize);
        if (_textEditor != null)
            _textEditor.FontSize = _textFontSizePx * PixelToDipFactor();
    }

    private void ZoomAt(Point anchor, double newScale)
    {
        newScale = ClampScale(newScale);
        if (Math.Abs(newScale - _scale) < 1e-9) return;

        // Keep the point under the cursor fixed while scaling.
        double ratio = newScale / _scale;
        _tx = anchor.X - (anchor.X - _tx) * ratio;
        _ty = anchor.Y - (anchor.Y - _ty) * ratio;
        _scale = newScale;
        Mode = ViewMode.Free;
        Apply();
    }

    private static double ClampScale(double s) => Math.Min(MaxScale, Math.Max(MinScale, s));

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasImage) return;

        // Text tool: a click places an inline editor instead of starting a drag.
        if (ActiveTool == AnnotationTool.Text)
        {
            if (e.ClickCount > 1) { e.Handled = true; return; }
            var img = (BitmapSource?)_image.Source;
            if (img == null) return;
            var px = ClampToImage(ViewToImagePixel(e.GetPosition(this)), img);
            CommitTextEditor(); // a click elsewhere seals the previous editor
            TextInputRequested?.Invoke(this, new TextInputRequestedEventArgs(px));
            e.Handled = true;
            return;
        }

        // Annotation mode: start a draft instead of panning / zoom-toggling.
        if (ActiveTool != AnnotationTool.None)
        {
            if (e.ClickCount > 1) { e.Handled = true; return; }
            _drafting = true;
            _draftStartView = e.GetPosition(this);
            CaptureMouse();
            Cursor = Cursors.Cross;
            e.Handled = true;
            return;
        }

        // Grid has no MouseDoubleClick event; detect it via ClickCount.
        if (e.ClickCount == 2)
        {
            if (Mode == ViewMode.Fit) ZoomTo100();
            else ZoomToFit();
            e.Handled = true;
            return;
        }

        _panning = true;
        _panStart = e.GetPosition(this);
        _panStartTx = _tx;
        _panStartTy = _ty;
        CaptureMouse();
        Cursor = Cursors.Hand;
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_drafting)
        {
            UpdateRubberBand(e.GetPosition(this));
            return;
        }
        if (!_panning) return;
        var p = e.GetPosition(this);
        _tx = _panStartTx + (p.X - _panStart.X);
        _ty = _panStartTy + (p.Y - _panStart.Y);
        // Panning leaves the Fit/Actual layout — the view is now user-controlled.
        Mode = ViewMode.Free;
        Apply();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drafting)
        {
            FinishDraft(e.GetPosition(this));
            ReleaseMouseCapture();
            return;
        }
        if (_panning)
            ReleaseMouseCapture();
        EndPan();
    }

    private void EndPan()
    {
        _panning = false;
        if (ActiveTool == AnnotationTool.None)
            Cursor = Cursors.Arrow;
    }

    // ---------- Annotation drafting ----------

    private void UpdateRubberBand(Point currentView)
    {
        var startPx = ViewToImagePixel(_draftStartView);
        var endPx = ViewToImagePixel(currentView);

        if (ActiveTool == AnnotationTool.Crop)
        {
            UpdateCropVisuals(startPx, endPx);
            return;
        }

        if (_rubberBand == null)
        {
            _rubberBand = CreateShape(ActiveTool, startPx, endPx,
                DraftColor, DraftStrokeWidth, dashed: false);
            if (_rubberBand != null)
                _overlay.Children.Add(_rubberBand);
        }
        else
        {
            UpdateShape(_rubberBand, ActiveTool, startPx, endPx);
        }
    }

    // ---------- Crop visuals (dim + double-dash outline) ----------

    /// <summary>Darkens the whole image (crop mode entered); the hole follows the selection.</summary>
    private void ShowCropDim()
    {
        if (!HasImage || _cropDim != null) return;
        var fill = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)); // ~40% black
        fill.Freeze();
        _cropDim = new Path
        {
            Fill = fill,
            Data = new RectangleGeometry(new Rect(0, 0, _image.Width, _image.Height)),
        };
        _overlay.Children.Insert(0, _cropDim); // below annotations and outlines
    }

    private void HideCropDim()
    {
        if (_cropDim == null) return;
        _overlay.Children.Remove(_cropDim); // no-op if the overlay was cleared meanwhile
        _cropDim = null;
    }

    /// <summary>Moves the dim hole and the double-dash outline to the dragged rect.</summary>
    private void UpdateCropVisuals(Point startPx, Point endPx)
    {
        double k = PixelToDipFactor();
        double x = Math.Min(startPx.X, endPx.X) * k;
        double y = Math.Min(startPx.Y, endPx.Y) * k;
        double w = Math.Abs(endPx.X - startPx.X) * k;
        double h = Math.Abs(endPx.Y - startPx.Y) * k;
        double thickness = Math.Max(0.5, CropRubberWidth * k);

        if (_cropOutlineW == null)
        {
            // White dashes under, black dashes on top shifted by one dash
            // length — at every point along the edge one of the two shows.
            var white = System.Windows.Media.Brushes.White.Clone();
            white.Freeze();
            var black = System.Windows.Media.Brushes.Black.Clone();
            black.Freeze();
            _cropOutlineW = new Rectangle
            {
                Stroke = white,
                StrokeThickness = thickness,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
            _rubberBand = new Rectangle
            {
                Stroke = black,
                StrokeThickness = thickness,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                StrokeDashOffset = 4,
            };
            _overlay.Children.Add(_cropOutlineW);
            _overlay.Children.Add(_rubberBand);
        }

        Canvas.SetLeft(_cropOutlineW, x);
        Canvas.SetTop(_cropOutlineW, y);
        _cropOutlineW.Width = w;
        _cropOutlineW.Height = h;
        // _rubberBand is the black dash layer here (created above if null).
        var outlineB = (Rectangle)_rubberBand!;
        Canvas.SetLeft(outlineB, x);
        Canvas.SetTop(outlineB, y);
        outlineB.Width = w;
        outlineB.Height = h;

        if (_cropDim != null)
        {
            _cropDim.Data = new CombinedGeometry(
                new RectangleGeometry(new Rect(0, 0, _image.Width, _image.Height)),
                new RectangleGeometry(new Rect(x, y, w, h)))
            {
                GeometryCombineMode = GeometryCombineMode.Exclude,
            };
        }
    }

    private void RemoveCropOutlines()
    {
        if (_cropOutlineW != null)
        {
            _overlay.Children.Remove(_cropOutlineW);
            _cropOutlineW = null;
        }
        // _rubberBand is removed by the shared draft-cleanup paths.
    }

    private void FinishDraft(Point currentView)
    {
        _drafting = false;
        Cursor = Cursors.Cross;

        var startPx = ViewToImagePixel(_draftStartView);
        var endPx = ViewToImagePixel(currentView);

        _overlay.Children.Remove(_rubberBand);
        _rubberBand = null;
        RemoveCropOutlines();

        var img = (BitmapSource?)_image.Source;
        if (img == null) return;

        // Clamp to the displayed bitmap bounds.
        startPx = ClampToImage(startPx, img);
        endPx = ClampToImage(endPx, img);

        double drag = (endPx - startPx).Length;
        if (drag < MinDragPixels) return; // treat as accidental click

        DraftCommitted?.Invoke(this, new AnnotationDraftEventArgs(ActiveTool, startPx, endPx));
    }

    /// <summary>Discards any in-progress draft (e.g. Esc or tool switch).</summary>
    public void CancelDraft()
    {
        _drafting = false;
        if (_rubberBand != null)
        {
            _overlay.Children.Remove(_rubberBand);
            _rubberBand = null;
        }
        RemoveCropOutlines();
        CancelTextEditor();
    }

    // ---------- Inline text editing ----------

    /// <summary>Opens an inline editor at the given displayed-image pixel position.</summary>
    public void BeginTextEdit(Point posPx)
    {
        CancelTextEditor();
        double k = PixelToDipFactor();
        _textEditorPosPx = posPx;

        var brush = new SolidColorBrush(DraftColor);
        brush.Freeze();
        _textEditor = new TextBox
        {
            FontFamily = new FontFamily("Microsoft YaHei"),
            FontSize = _textFontSizePx * k,
            Foreground = brush,
            CaretBrush = System.Windows.Media.Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            BorderThickness = new Thickness(1),
            MinWidth = 40,
            AcceptsReturn = false,
            IsHitTestVisible = true, // the overlay itself is click-through; the editor is not
        };
        Canvas.SetLeft(_textEditor, posPx.X * k);
        Canvas.SetTop(_textEditor, posPx.Y * k);

        _textEditor.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitTextEditor();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelTextEditor();
                e.Handled = true;
            }
        };
        _textEditor.LostKeyboardFocus += (s, e) => CommitTextEditor();

        _overlay.Children.Add(_textEditor);
        _textEditor.Focus();
    }

    /// <summary>Commits the editor if it contains non-empty text.</summary>
    public void CommitTextEditor()
    {
        if (_textEditor == null) return;
        var text = _textEditor.Text.Trim();
        var pos = _textEditorPosPx;
        RemoveTextEditor();
        if (text.Length > 0)
            TextCommitted?.Invoke(this, new TextCommittedEventArgs(pos, text, _textFontSizePx));
    }

    /// <summary>Discards the editor without committing.</summary>
    public void CancelTextEditor() => RemoveTextEditor();

    private void RemoveTextEditor()
    {
        if (_textEditor != null)
        {
            _overlay.Children.Remove(_textEditor);
            _textEditor = null;
        }
    }

    /// <summary>True while the inline text editor is open.</summary>
    public bool IsEditingText => _textEditor != null;

    private static Point ClampToImage(Point px, BitmapSource img) =>
        new(Math.Clamp(px.X, 0, img.PixelWidth), Math.Clamp(px.Y, 0, img.PixelHeight));

    /// <summary>Creates an overlay shape in DIP space from image-pixel coordinates.</summary>
    private Shape? CreateShape(AnnotationTool tool, Point startPx, Point endPx,
        Color color, double strokeWidthPx, bool dashed)
    {
        double k = PixelToDipFactor();
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        double thickness = Math.Max(0.5, strokeWidthPx * k);

        Shape? shape = tool switch
        {
            AnnotationTool.Line => new Line(),
            AnnotationTool.Arrow => new System.Windows.Shapes.Path(),
            AnnotationTool.Rectangle or AnnotationTool.Crop => new Rectangle(),
            _ => null,
        };
        if (shape == null) return null;

        shape.Stroke = brush;
        shape.StrokeThickness = thickness;
        if (tool is AnnotationTool.Line or AnnotationTool.Arrow)
        {
            shape.StrokeStartLineCap = PenLineCap.Round;
            shape.StrokeEndLineCap = PenLineCap.Round;
        }
        if (tool == AnnotationTool.Arrow)
            shape.Fill = brush; // the arrowhead triangle is filled
        if (dashed)
            shape.StrokeDashArray = new DoubleCollection { 4, 3 };
        UpdateShape(shape, tool, startPx, endPx);
        return shape;
    }

    private void UpdateShape(Shape shape, AnnotationTool tool, Point startPx, Point endPx)
    {
        double k = PixelToDipFactor();
        switch (shape)
        {
            case Line line:
                line.X1 = startPx.X * k; line.Y1 = startPx.Y * k;
                line.X2 = endPx.X * k; line.Y2 = endPx.Y * k;
                break;

            case System.Windows.Shapes.Path path:
                path.Data = BuildArrowGeometry(startPx, endPx, shape.StrokeThickness / k, k);
                break;

            case Rectangle rect:
                double x = Math.Min(startPx.X, endPx.X) * k;
                double y = Math.Min(startPx.Y, endPx.Y) * k;
                rect.Width = Math.Abs(endPx.X - startPx.X) * k;
                rect.Height = Math.Abs(endPx.Y - startPx.Y) * k;
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                break;
        }
    }

    private static Geometry BuildArrowGeometry(Point startPx, Point endPx, double strokeWidthPx, double k)
    {
        var (w1, w2) = ArrowGeometry.HeadPoints(startPx, endPx, strokeWidthPx);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            // Shaft.
            ctx.BeginFigure(new Point(startPx.X * k, startPx.Y * k), false, false);
            ctx.LineTo(new Point(endPx.X * k, endPx.Y * k), true, false);
            // Filled triangle head (tip + two base corners).
            ctx.BeginFigure(new Point(endPx.X * k, endPx.Y * k), true, true);
            ctx.LineTo(new Point(w1.X * k, w1.Y * k), true, false);
            ctx.LineTo(new Point(w2.X * k, w2.Y * k), true, false);
        }
        geo.Freeze();
        return geo;
    }

    private void Apply()
    {
        _transform.Matrix = new Matrix(_scale, 0, 0, _scale, _tx, _ty);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }
}
