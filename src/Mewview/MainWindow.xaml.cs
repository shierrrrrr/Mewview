using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mewview.Models;
using Mewview.Services;
using Microsoft.Win32;

namespace Mewview;

public partial class MainWindow : Window
{
    private Color _annotColor = Color.FromRgb(0xE5, 0x39, 0x35);
    private double _annotStrokeWidth = 8.0; // 细 4 / 中 8 / 粗 16, default 中

    private IReadOnlyList<string> _folderImages = Array.Empty<string>();
    private int _currentIndex = -1;
    private BitmapSource? _currentImage; // the frame currently on screen
    private string? _currentPath; // null when the image came from the clipboard

    private ImageDocument? _document; // owns the frames; disposed when replaced
    private int _frameIndex;
    private bool _canAnnotate = true;
    private readonly FrameAnimator _animator = new();

    private readonly IOcrService _ocrService = new OcrService();
    private CancellationTokenSource? _ocrCts;
    private string _lastOcrText = string.Empty;

    private AnnotationSession? _session;
    private AnnotationTool _activeTool = AnnotationTool.None;
    private Rect? _displayedCrop;

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

    // Decoding runs off the UI thread, so more than one open can be in flight at
    // once. The generation counter marks the newest request as the one whose result
    // may be shown; the busy counter drives the wait cursor and is a count rather
    // than a flag so that an older request finishing late cannot clear the cursor
    // while a newer one is still working.
    private int _openGeneration;
    private int _busyCount;

    /// <summary>How an open attempt ended, for callers that need to react to it.</summary>
    private enum OpenOutcome
    {
        /// <summary>The image is now the current one.</summary>
        Opened,

        /// <summary>It could not be decoded; whatever was on screen still is.</summary>
        Failed,

        /// <summary>No decoder for this format exists on this machine.</summary>
        MissingCodec,

        /// <summary>A newer request took over before this one finished.</summary>
        Superseded,
    }

    /// <summary>Optional image path passed on the command line; loaded after the window is shown.</summary>
    public string? InitialImagePath { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Canvas.DraftStrokeWidth = _annotStrokeWidth;
        Canvas.DraftCommitted += OnDraftCommitted;
        Canvas.TextInputRequested += (s, e) => Canvas.BeginTextEdit(e.Position);
        Canvas.TextCommitted += OnTextCommitted;
        _animator.FrameRequested += OnAnimationFrame;
        _toastTimer.Tick += (s, e) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(InitialImagePath) && File.Exists(InitialImagePath))
            _ = OpenImageAsync(InitialImagePath);
    }

    // ---------- Opening ----------

    private void OnOpenClick(object sender, RoutedEventArgs e) => ShowOpenDialog();

    private void ShowOpenDialog()
    {
        var dlg = new OpenFileDialog
        {
            Filter = ImageFormatRegistry.BuildOpenFilter(),
            Title = "Open Image"
        };
        if (dlg.ShowDialog(this) == true)
            _ = OpenImageAsync(dlg.FileName);
    }

    /// <summary>
    /// Opens an image, doing the decode and the folder listing on a background thread
    /// so that a 48 MP HEIC or a multi-page TIFF cannot freeze the window. Returns why
    /// it ended, and shows the window's wait cursor while it runs.
    /// </summary>
    /// <param name="reportFailure">
    /// False when the caller is walking through a folder and will summarise the
    /// failures itself — a modal dialog per unopenable file would make ←/→ unusable.
    /// </param>
    private async Task<OpenOutcome> OpenImageAsync(string path, bool reportFailure = true)
    {
        // Last request wins: a second open started while this one is still decoding
        // takes over, and the older one drops its result instead of showing it.
        int generation = ++_openGeneration;
        BeginBusy(path);

        try
        {
            // Normalize separators/relative segments so folder-list matching works
            // even when the path arrives with forward slashes (e.g. from CLI args).
            path = Path.GetFullPath(path);

            if (!ImageService.IsSupported(path))
            {
                if (reportFailure)
                {
                    MessageBox.Show(this, "Unsupported image format.", AppDisplayName.Current,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return OpenOutcome.Failed;
            }

            ImageDocument? loaded = null;
            try
            {
                loaded = await Task.Run(() => ImageService.LoadDocument(path));
                var folder = await Task.Run(() => ImageService.GetImagesInSameFolder(path));

                if (generation != _openGeneration)
                    return OpenOutcome.Superseded;

                ShowDocument(loaded, path, folder);
                loaded = null; // the window owns it from here on
                return OpenOutcome.Opened;
            }
            finally
            {
                // Anything decoded but not adopted has to be released here: a multi-page
                // TIFF keeps its file stream open until it is disposed.
                loaded?.Dispose();
            }
        }
        catch (Exception ex)
        {
            if (generation != _openGeneration)
                return OpenOutcome.Superseded; // the failure is no longer the user's problem

            if (ImageService.IsMissingCodecError(path, ex))
            {
                if (reportFailure)
                    ShowMissingCodecMessage(path);
                return OpenOutcome.MissingCodec;
            }

            if (reportFailure)
            {
                MessageBox.Show(this, "Unable to open this image.", AppDisplayName.Current,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return OpenOutcome.Failed;
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>
    /// A load takes a visible moment for a large file and there is no progress to
    /// report, so the lightest possible signal goes up: the wait cursor, plus the
    /// status bar saying which file is being opened.
    /// </summary>
    private void BeginBusy(string path)
    {
        if (++_busyCount == 1)
            Cursor = Cursors.Wait;

        StatusLeft.Text = $"正在打开 {Path.GetFileName(path)} …";
    }

    private void EndBusy()
    {
        if (_busyCount == 0 || --_busyCount > 0) return;

        Cursor = null; // back to the window default

        // The loading text is only true while a load is running. If nothing took its
        // place there is no image to describe, so clear it.
        if (_currentImage == null)
            StatusLeft.Text = string.Empty;
        else
            UpdateStatus();
    }

    /// <summary>
    /// HEIC/HEIF and AVIF are decoded by Windows' optional imaging extensions, which
    /// not every machine has. The app never installs anything itself and never fetches
    /// anything: it names what to install and, only if the user says yes, asks the shell
    /// to open the Store search page.
    /// </summary>
    private void ShowMissingCodecMessage(string path)
    {
        var format = ImageFormatRegistry.Find(path);
        var name = format?.DisplayName ?? "此格式";

        var text =
            $"系统缺少打开 {name} 图片所需的解码组件。\n\n" +
            "这是 Windows 的可选功能，需要从 Microsoft Store 免费安装：\n" +
            "· HEIC（.heic / .heif）：HEIF 图像扩展，部分系统还需 HEVC 视频扩展\n" +
            "· AVIF（.avif）：AV1 视频扩展";

        if (format?.CodecStoreSearch is not { } search)
        {
            MessageBox.Show(this, text, AppDisplayName.Current,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        text += "\n\n是否现在打开 Microsoft Store 搜索？";
        var answer = MessageBox.Show(this, text, AppDisplayName.Current,
            MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            Process.Start(new ProcessStartInfo(
                "ms-windows-store://search/?query=" + Uri.EscapeDataString(search))
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // No Store app, or the protocol is unregistered. Nothing is lost: the
            // message above already names what has to be installed.
        }
    }

    /// <summary>
    /// Makes a single bitmap current (clipboard paste). Path is null for images
    /// that did not come from disk.
    /// </summary>
    private void ShowImage(BitmapSource image, string? path) =>
        ShowDocument(ImageDocument.SingleFrame(image), path, Array.Empty<string>());

    /// <summary>
    /// Makes a document current and resets all per-image state.
    /// </summary>
    /// <param name="folder">
    /// The sibling images to browse through. Listed by the caller on a background
    /// thread — enumerating a folder is I/O and does not belong on the UI thread.
    /// </param>
    private void ShowDocument(ImageDocument document, string? path, IReadOnlyList<string> folder)
    {
        // Release the previous document before adopting the new one: a paged TIFF
        // holds its file stream open until then. Nothing on screen refers to it any
        // more, because every page handed out is already a standalone bitmap.
        _animator.Stop();
        _document?.Dispose();

        _document = document;
        _currentPath = path;
        _frameIndex = 0;
        _currentImage = document.GetFrame(0);

        Canvas.SetImage(_currentImage);
        _folderImages = path != null ? folder : Array.Empty<string>();
        _currentIndex = path != null ? IndexOfPath(_folderImages, path) : -1;

        // Fresh edit state per image; the original bitmap stays untouched.
        // A multi-frame file gets no session at all: see SetAnnotationEnabled.
        _canAnnotate = document.SupportsAnnotation;
        _session = _canAnnotate ? new AnnotationSession(_currentImage) : null;
        if (_session != null)
            _session.StateChanged += (s, e) => RefreshFromSession();
        _displayedCrop = null;
        SetTool(AnnotationTool.None);
        RefreshFromSession();

        EmptyState.Visibility = Visibility.Collapsed;
        SetImageUiEnabled(true);
        // Must follow SetImageUiEnabled, which re-enables the whole tool panel.
        SetAnnotationEnabled(_canAnnotate);
        UpdateFrameNav();
        ResetOcrPanel();
        UpdateStatus();

        if (document.IsAnimated)
            _animator.Start(document.Frames);
    }

    /// <summary>
    /// Annotation and crop are unavailable for multi-page TIFFs and animated GIFs:
    /// the bitmap under the shapes changes on every page turn or animation tick, so
    /// any annotation would immediately point at the wrong pixels. OCR is unaffected.
    /// </summary>
    private void SetAnnotationEnabled(bool enabled)
    {
        ToolPanel.IsEnabled = enabled;
        Canvas.AnnotationsEnabled = enabled;
        if (enabled) return;

        SetTool(AnnotationTool.None);
        UndoButton.IsEnabled = false;
        RedoButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
    }

    /// <summary>Steps one page of a multi-page TIFF.</summary>
    private void NavigateFrame(int delta)
    {
        if (_document is not { IsPaged: true } document) return;
        int next = _frameIndex + delta;
        if (next < 0 || next >= document.FrameCount) return;

        _frameIndex = next;
        _currentImage = document.GetFrame(next);

        // Deliberately not Canvas.SetImage: that would reset zoom and pan, making
        // two pages impossible to compare side by side.
        Canvas.UpdateImageSource(_currentImage);

        UpdateFrameNav();
        ResetOcrPanel(); // the previous page's recognition result no longer applies
        UpdateStatus();
    }

    private void UpdateFrameNav()
    {
        bool show = _document is { IsPaged: true };
        FrameNav.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || _document == null) return;

        FrameNavText.Text = $"{_frameIndex + 1} / {_document.FrameCount}";
        PrevFrameButton.IsEnabled = _frameIndex > 0;
        NextFrameButton.IsEnabled = _frameIndex < _document.FrameCount - 1;
    }

    /// <summary>Animation tick: swap the bitmap only, never the view transform.</summary>
    private void OnAnimationFrame(object? sender, int index)
    {
        if (_document is not { IsAnimated: true } document) return;
        if (index < 0 || index >= document.FrameCount) return;

        _frameIndex = index;
        _currentImage = document.GetFrame(index);
        Canvas.UpdateImageSource(_currentImage);
    }

    private static int IndexOfPath(IReadOnlyList<string> list, string path)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], path, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private void CloseCurrentImage()
    {
        CancelOcr();
        _animator.Stop();
        Canvas.Clear(); // drop the frame before the document that owns it goes away
        _document?.Dispose();
        _document = null;
        _session = null;
        _displayedCrop = null;
        _canAnnotate = true;
        SetTool(AnnotationTool.None);
        Canvas.AnnotationsEnabled = true;
        _currentImage = null;
        _currentPath = null;
        _frameIndex = 0;
        _folderImages = Array.Empty<string>();
        _currentIndex = -1;
        FrameNav.Visibility = Visibility.Collapsed;
        UpdateTitle();
        OcrPanel.Visibility = Visibility.Collapsed;
        SetImageUiEnabled(false);
        ResetOcrPanel();
        StatusLeft.Text = string.Empty;
        StatusRight.Text = string.Empty;
        EmptyState.Visibility = Visibility.Visible;
    }

    /// <summary>Enables/disables every control that needs an open image (Spec §36).</summary>
    private void SetImageUiEnabled(bool enabled)
    {
        ToolPanel.IsEnabled = enabled;
        PrevButton.IsEnabled = enabled;
        NextButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
        Zoom100Button.IsEnabled = enabled;
        FitButton.IsEnabled = enabled;
        OcrTopButton.IsEnabled = enabled;
        if (!enabled)
        {
            UndoButton.IsEnabled = false;
            RedoButton.IsEnabled = false;
        }
    }

    // ---------- Folder browsing ----------

    private void OnPrevClick(object sender, RoutedEventArgs e) => Navigate(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => Navigate(+1);

    private void OnPrevFrameClick(object sender, RoutedEventArgs e) => NavigateFrame(-1);

    private void OnNextFrameClick(object sender, RoutedEventArgs e) => NavigateFrame(+1);

    private async void Navigate(int delta)
    {
        if (_currentImage == null || _folderImages.Count == 0 || _currentIndex < 0) return;

        // Snapshot: a successful open re-lists the folder and replaces this array,
        // while the loop below has to keep walking the list it started on.
        var files = _folderImages;
        int index = _currentIndex;
        int skipped = 0;
        bool missingCodec = false;

        // Step past anything that will not open rather than stopping on it. A folder
        // can hold files this machine has no decoder for (a .heic without the Store
        // extension) or damaged ones, and a modal dialog for each would make ←/→
        // unusable.
        for (int attempt = 0; attempt < files.Count; attempt++)
        {
            index += delta;
            if (index < 0 || index >= files.Count)
            {
                if (skipped > 0)
                    ShowToast(SummariseSkipped(skipped, missingCodec, atEnd: true));
                return;
            }

            var outcome = await OpenImageAsync(files[index], reportFailure: false);
            if (outcome == OpenOutcome.Opened)
            {
                if (skipped > 0)
                    ShowToast(SummariseSkipped(skipped, missingCodec, atEnd: false));
                return;
            }
            if (outcome == OpenOutcome.Superseded)
                return; // a newer request owns the screen now

            if (outcome == OpenOutcome.MissingCodec)
                missingCodec = true;
            skipped++;
        }

        if (skipped > 0)
            ShowToast(SummariseSkipped(skipped, missingCodec, atEnd: false));
    }

    /// <summary>
    /// One line for the toast after browsing skipped some files. The missing-codec
    /// case is named because it is actionable, and this path deliberately stays
    /// quiet about failures while it walks the folder.
    /// </summary>
    private static string SummariseSkipped(int skipped, bool missingCodec, bool atEnd)
    {
        string where = atEnd ? "已到文件夹末尾，" : string.Empty;
        return missingCodec
            ? $"{where}跳过 {skipped} 个无法打开的文件（含缺少 HEIC/AVIF 解码扩展的）。"
            : $"{where}跳过 {skipped} 个无法打开的文件。";
    }

    // ---------- Drag & drop ----------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedImage(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedImage(e.Data);
        if (path != null)
            _ = OpenImageAsync(path);
        e.Handled = true;
    }

    private static string? GetDroppedImage(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
        var files = data.GetData(DataFormats.FileDrop) as string[];
        return files?.FirstOrDefault(f => File.Exists(f) && ImageService.IsSupported(f));
    }

    // ---------- Keyboard ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // While any TextBox has focus (inline text editor, OCR box), all keys
        // belong to it — no shortcuts, no image navigation.
        if (e.OriginalSource is TextBox) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && shift && e.Key == Key.Space)
        {
            SetTopmost(!Topmost);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.O)
        {
            ShowOpenDialog();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.D0)
        {
            Canvas.ZoomToFit();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.D1)
        {
            Canvas.ZoomTo100();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.E)
        {
            ToggleOcrPanel(runOnOpen: true);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Z && _session?.CanUndo == true)
        {
            _session.Undo();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Y && _session?.CanRedo == true)
        {
            _session.Redo();
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Key.S)
        {
            SaveAnnotated(); // Save As
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S)
        {
            SaveOverwrite();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.C)
        {
            CopyImageToClipboard();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.V)
        {
            PasteImageFromClipboard();
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            Navigate(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            Navigate(+1);
            e.Handled = true;
        }
        // Page up/down step through the PAGES of a multi-page file — a different
        // axis from the ←/→ folder browsing above, so the two never fight.
        else if (e.Key == Key.PageUp)
        {
            NavigateFrame(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.PageDown)
        {
            NavigateFrame(+1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Esc first exits the active tool / draft, then closes the image.
            if (_activeTool != AnnotationTool.None)
                SetTool(AnnotationTool.None);
            else
                CloseCurrentImage();
            e.Handled = true;
        }
    }

    // ---------- Window controls ----------

    private void OnPinClick(object sender, RoutedEventArgs e) => SetTopmost(!Topmost);

    private void SetTopmost(bool on)
    {
        Topmost = on;
        // Pin feedback: accent blue when active, quiet gray otherwise (Spec §33).
        PinButton.Foreground = on
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("TextSecondaryBrush");
        PinButton.ToolTip = on ? "取消置顶 (Ctrl+Shift+Space)" : "窗口置顶 (Ctrl+Shift+Space)";
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        string title;
        if (_currentPath != null)
            title = $"{Path.GetFileName(_currentPath)} - {AppDisplayName.Current}";
        else if (_currentImage != null)
            title = $"(剪贴板图片) - {AppDisplayName.Current}";
        else
            title = AppDisplayName.Current;
        if (Topmost) title += "  [置顶]";
        Title = title;
    }

    // ---------- Annotations ----------

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } &&
            Enum.TryParse<AnnotationTool>(tag, out var tool))
        {
            SetTool(tool);
        }
    }

    private void OnColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked || clicked.Tag is not string name) return;
        _annotColor = name switch
        {
            "Black" => Color.FromRgb(0x11, 0x11, 0x11),
            "White" => Colors.White,
            "Red" => Color.FromRgb(0xE5, 0x39, 0x35),
            "Blue" => Color.FromRgb(0x1E, 0x88, 0xE5),
            "Green" => Color.FromRgb(0x43, 0xA0, 0x47),
            _ => _annotColor,
        };
        Canvas.DraftColor = _annotColor; // rubber band + future drafts use the new color

        // Accent ring on the selected swatch.
        var accent = (Brush)FindResource("AccentBrush");
        var normal = (Brush)FindResource("BorderBrush");
        foreach (var child in ParamColorSection.Children)
        {
            if (child is Button b)
            {
                bool active = ReferenceEquals(b, clicked);
                b.BorderBrush = active ? accent : normal;
                b.BorderThickness = active ? new Thickness(2) : new Thickness(1);
            }
        }
    }

    private void OnThicknessClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string t } clicked ||
            !double.TryParse(t, out double width)) return;

        _annotStrokeWidth = width;
        Canvas.DraftStrokeWidth = width; // rubber band preview follows immediately
        HighlightSegment(ParamThicknessSection, clicked);
    }

    /// <summary>Soft accent-light highlight for the active segmented option.</summary>
    private void HighlightSegment(Panel panel, Button clicked)
    {
        var active = (Brush)FindResource("AccentLightBrush");
        foreach (var child in panel.Children)
        {
            if (child is Button b)
                b.Background = ReferenceEquals(b, clicked) ? active : System.Windows.Media.Brushes.Transparent;
        }
    }

    private void SetTool(AnnotationTool tool)
    {
        if (tool == _activeTool) return;

        Canvas.CommitTextEditor(); // switching tools seals any open editor
        _activeTool = tool;
        Canvas.ActiveTool = tool;
        Canvas.CancelDraft();
        Cursor = tool == AnnotationTool.None ? Cursors.Arrow : Cursors.Cross;

        // Sync the left toolbar radio buttons (Esc / code paths land here too).
        ToolSelect.IsChecked = tool == AnnotationTool.None;
        ToolLine.IsChecked = tool == AnnotationTool.Line;
        ToolArrow.IsChecked = tool == AnnotationTool.Arrow;
        ToolRect.IsChecked = tool == AnnotationTool.Rectangle;
        ToolText.IsChecked = tool == AnnotationTool.Text;
        ToolCrop.IsChecked = tool == AnnotationTool.Crop;

        UpdateParamPanel();
    }

    /// <summary>
    /// The floating parameter panel only appears for tools that have
    /// parameters, and shows exactly the sections that tool needs (Spec §13).
    /// </summary>
    private void UpdateParamPanel()
    {
        bool hasParams = _activeTool is AnnotationTool.Line or AnnotationTool.Arrow
            or AnnotationTool.Rectangle or AnnotationTool.Text;
        ParamPanel.Visibility = hasParams ? Visibility.Visible : Visibility.Collapsed;
        if (!hasParams) return;

        ParamColorSection.Visibility = Visibility.Visible; // all four tools have a color
        ParamThicknessSection.Visibility = _activeTool is AnnotationTool.Line
            or AnnotationTool.Arrow or AnnotationTool.Rectangle
            ? Visibility.Visible : Visibility.Collapsed;
        ParamFontSizeSection.Visibility = _activeTool == AnnotationTool.Text
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDraftCommitted(object? sender, Controls.AnnotationDraftEventArgs e)
    {
        if (_session == null) return;

        // Draft coords are in displayed-image pixels; shift back to original coords.
        var offset = _session.CropRegion?.Location ?? new Point(0, 0);
        var start = e.Start + (Vector)offset;
        var end = e.End + (Vector)offset;

        switch (e.Tool)
        {
            case AnnotationTool.Line:
                _session.AddAnnotation(new LineAnnotation(start, end, _annotColor, _annotStrokeWidth));
                break;
            case AnnotationTool.Arrow:
                _session.AddAnnotation(new ArrowAnnotation(start, end, _annotColor, _annotStrokeWidth));
                break;
            case AnnotationTool.Rectangle:
                _session.AddAnnotation(new RectangleAnnotation(
                    new Rect(start, end), _annotColor, _annotStrokeWidth));
                break;
            case AnnotationTool.Crop:
                _session.SetCrop(new Rect(start, end));
                SetTool(AnnotationTool.None); // crop is a one-shot action
                break;
        }
    }

    private void OnTextCommitted(object? sender, Controls.TextCommittedEventArgs e)
    {
        if (_session == null) return;
        var offset = _session.CropRegion?.Location ?? new Point(0, 0);
        var pos = e.Position + (Vector)offset;
        _session.AddAnnotation(new TextAnnotation(pos, e.Text, _annotColor, e.FontSize));
    }

    /// <summary>Re-renders display bitmap (with crop) and overlay from session state.</summary>
    private void RefreshFromSession()
    {
        if (_session == null) return;

        var crop = _session.CropRegion;

        // Swap the displayed bitmap only when the crop actually changed.
        if (crop != _displayedCrop)
        {
            BitmapSource display = _session.Original;
            if (crop is { } c)
            {
                display = new CroppedBitmap(_session.Original, ToInt32Rect(c, _session.Original));
                display.Freeze();
            }
            Canvas.SetImage(display);
            _displayedCrop = crop;
        }

        // Overlay works in displayed-image pixels: subtract the crop offset.
        var offset = crop?.Location ?? new Point(0, 0);
        var shift = (Vector)new Point(-offset.X, -offset.Y);
        Canvas.SetAnnotations(_session.Annotations
            .Select(a => TranslateAnnotation(a, shift))
            .ToList());

        UndoButton.IsEnabled = _session.CanUndo;
        RedoButton.IsEnabled = _session.CanRedo;
    }

    private static Int32Rect ToInt32Rect(Rect r, BitmapSource bounds)
    {
        int x = Math.Clamp((int)Math.Floor(r.X), 0, bounds.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Floor(r.Y), 0, bounds.PixelHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(r.Right), x + 1, bounds.PixelWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(r.Bottom), y + 1, bounds.PixelHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private static Annotation TranslateAnnotation(Annotation a, Vector v) => a switch
    {
        LineAnnotation l => l with { Start = l.Start + v, End = l.End + v },
        ArrowAnnotation ar => ar with { Start = ar.Start + v, End = ar.End + v },
        RectangleAnnotation r => r with { Region = new Rect(r.Region.TopLeft + v, r.Region.Size) },
        TextAnnotation t => t with { Position = t.Position + v },
        _ => a,
    };

    private void OnUndoClick(object sender, RoutedEventArgs e) => _session?.Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => _session?.Redo();

    /// <summary>
    /// Saving exists to write annotations out, so it is unavailable exactly where
    /// annotating is unavailable. The toolbar button is already disabled for such
    /// files; this covers the Ctrl+S / Ctrl+Shift+S shortcuts.
    /// </summary>
    private bool RejectSaveWhenNotAnnotatable()
    {
        if (_currentImage == null || _canAnnotate) return false;
        ShowToast("多帧图片（多页 TIFF / 动图）不支持标注与保存。");
        return true;
    }

    /// <summary>
    /// True when Ctrl+S may overwrite the original file in place: the image came
    /// from disk and its format has an encoder (see ImageFormatRegistry).
    /// Everything else falls back to "save as", which only writes PNG/JPEG/WebP —
    /// so an output file never ends up with an extension its content doesn't match.
    /// </summary>
    private bool CanOverwriteCurrent()
    {
        if (_currentPath == null) return false;
        return ImageFormatRegistry.Find(_currentPath) is { CanOverwrite: true };
    }

    /// <summary>
    /// Toolbar Save button: asks before overwriting (确定 / 另存为 / 取消).
    /// The Ctrl+S shortcut skips this confirmation on purpose.
    /// </summary>
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (RejectSaveWhenNotAnnotatable()) return;
        if (_session == null || _currentImage == null) return;

        bool hasEdits = _session.Annotations.Count > 0 || _session.CropRegion != null;
        if (!hasEdits)
        {
            ShowToast("没有改动，无需保存。");
            return;
        }

        // Clipboard images have no path; formats without an encoder → straight to Save As.
        if (!CanOverwriteCurrent())
        {
            SaveAnnotated();
            return;
        }

        var dlg = new ConfirmSaveDialog { Owner = this };
        if (dlg.ShowDialog() != true) return; // 取消
        if (dlg.Choice == SaveConfirmChoice.Overwrite)
            SaveOverwrite();
        else
            SaveAnnotated();
    }

    /// <summary>Ctrl+S: overwrite the original file with the rendered result.</summary>
    private void SaveOverwrite()
    {
        if (RejectSaveWhenNotAnnotatable()) return;
        if (_session == null || _currentImage == null) return;

        bool hasEdits = _session.Annotations.Count > 0 || _session.CropRegion != null;
        if (!hasEdits)
        {
            ShowToast("没有改动，无需保存。");
            return;
        }

        // Clipboard images have no path; formats without an encoder both go to Save As.
        if (_currentPath is not { } path || !CanOverwriteCurrent())
        {
            SaveAnnotated();
            return;
        }

        try
        {
            AnnotationRenderer.SaveToFile(
                _session.Original, _session.Annotations, _session.CropRegion, path);
            ShowToast($"已覆盖保存：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, AppDisplayName.Current,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Ctrl+C: copy what is on screen. With annotations or a crop present it copies
    /// the rendered result; otherwise — which includes every multi-frame file, since
    /// those cannot be annotated — it copies the current frame unchanged.
    /// </summary>
    private void CopyImageToClipboard()
    {
        if (_currentImage == null) return;

        BitmapSource image = _currentImage;
        if (_session is { } session
            && (session.Annotations.Count > 0 || session.CropRegion != null))
        {
            var png = AnnotationRenderer.RenderToPngBytes(
                session.Original, session.Annotations, session.CropRegion);
            image = PngBytesToBitmapSource(png);
        }

        bool ok = RunWithClipboardRetry(() => Clipboard.SetImage(image));
        ShowToast(ok ? "已复制图片到剪贴板。" : "复制失败：剪贴板被占用。");
    }

    /// <summary>Ctrl+V: open the image currently on the clipboard.</summary>
    private void PasteImageFromClipboard()
    {
        BitmapSource? image = null;
        bool ok = RunWithClipboardRetry(() =>
        {
            if (Clipboard.ContainsImage())
                image = Clipboard.GetImage();
        });

        if (!ok)
        {
            ShowToast("读取剪贴板失败。");
            return;
        }
        if (image == null)
        {
            ShowToast("剪贴板中没有图片。");
            return;
        }
        if (image.CanFreeze) image.Freeze();
        ShowImage(image, null);
    }

    private static BitmapSource PngBytesToBitmapSource(byte[] png)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = new MemoryStream(png);
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>The clipboard can be held briefly by other apps; retry a few times.</summary>
    private static bool RunWithClipboardRetry(Action action, int attempts = 5)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                action();
                return true;
            }
            catch (System.Runtime.InteropServices.COMException) when (i < attempts - 1)
            {
                Thread.Sleep(50);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private void SaveAnnotated()
    {
        if (RejectSaveWhenNotAnnotatable()) return;
        if (_session == null || _currentImage == null) return;

        string baseName = _currentIndex >= 0
            ? Path.GetFileNameWithoutExtension(_folderImages[_currentIndex])
            : "image";
        var dlg = new SaveFileDialog
        {
            Title = "Save annotated image",
            FileName = baseName + "_annotated",
            DefaultExt = ".png",
            AddExtension = true,
            Filter = ImageFormatRegistry.BuildSaveFilter(),
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            AnnotationRenderer.SaveToFile(
                _session.Original, _session.Annotations, _session.CropRegion, dlg.FileName);
            ShowToast($"已保存：{Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, AppDisplayName.Current,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- OCR ----------

    private void OnOcrToggleClick(object sender, RoutedEventArgs e) => ToggleOcrPanel(runOnOpen: true);

    private void OnOcrRunClick(object sender, RoutedEventArgs e) => _ = RunOcrAsync();

    /// <summary>
    /// The OCR panel stays collapsed until the user asks for it: top OCR icon
    /// (or Ctrl+E) expands it and starts recognition; clicking again collapses
    /// it and cancels any running job.
    /// </summary>
    private void ToggleOcrPanel(bool runOnOpen)
    {
        if (OcrPanel.Visibility == Visibility.Visible)
        {
            CancelOcr();
            OcrPanel.Visibility = Visibility.Collapsed;
            return;
        }
        if (_currentImage == null) return;
        OcrPanel.Visibility = Visibility.Visible;
        if (runOnOpen)
            _ = RunOcrAsync();
    }

    /// <summary>Back to the spec's idle state (§19/§36): Ready, empty result.</summary>
    private void ResetOcrPanel()
    {
        CancelOcr();
        _lastOcrText = string.Empty;
        OcrResultBox.Text = string.Empty;
        OcrStatusText.Text = "Ready";
        OcrCopyButton.IsEnabled = false;
    }

    private OcrLanguage SelectedOcrLanguage()
    {
        var tag = (OcrLanguageBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag == "Korean" ? OcrLanguage.Korean : OcrLanguage.ChineseEnglish;
    }

    private void CancelOcr()
    {
        _ocrCts?.Cancel();
        _ocrCts?.Dispose();
        _ocrCts = null;
    }

    private async Task RunOcrAsync()
    {
        var image = _currentImage;
        if (image == null)
        {
            OcrStatusText.Text = "没有可识别的图片。";
            return;
        }

        var language = SelectedOcrLanguage();

        // Model check before touching the engine — offer a one-time download.
        if (!_ocrService.IsLanguageReady(language, out _))
        {
            var choice = MessageBox.Show(
                this,
                "OCR 模型尚未安装（约 36 MB，仅首次需要联网下载）。\n是否现在下载？下载完成后将自动开始识别。",
                AppDisplayName.Current,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes)
            {
                OcrStatusText.Text = "已取消模型下载。";
                return;
            }

            OcrRunButton.IsEnabled = false;
            OcrHeaderRunButton.IsEnabled = false;
            var progress = new Progress<double>(p =>
                OcrStatusText.Text = $"正在下载 OCR 模型… {p:P0}");
            try
            {
                await _ocrService.EnsureModelsAsync(language, progress);
            }
            catch (OperationCanceledException)
            {
                OcrStatusText.Text = "已取消下载。";
                OcrRunButton.IsEnabled = true;
                OcrHeaderRunButton.IsEnabled = true;
                return;
            }
            catch (OcrException ex)
            {
                OcrStatusText.Text = ex.Message;
                OcrRunButton.IsEnabled = true;
                OcrHeaderRunButton.IsEnabled = true;
                return;
            }
            catch (Exception ex)
            {
                OcrStatusText.Text = "模型下载失败：" + ex.Message;
                OcrRunButton.IsEnabled = true;
                OcrHeaderRunButton.IsEnabled = true;
                return;
            }
        }

        CancelOcr();
        _ocrCts = new CancellationTokenSource();
        var ct = _ocrCts.Token;

        OcrRunButton.IsEnabled = false;
        OcrHeaderRunButton.IsEnabled = false;
        OcrCopyButton.IsEnabled = false;
        OcrResultBox.Text = string.Empty;
        OcrStatusText.Text = "识别中…（首次使用需加载模型）";

        try
        {
            var output = await _ocrService.RecognizeAsync(image, language, ct);
            _lastOcrText = output.FullText;
            OcrResultBox.Text = output.FullText;

            double avgScore = output.Lines.Count > 0 ? output.Lines.Average(l => l.Score) : 0;
            OcrStatusText.Text = output.Lines.Count > 0
                ? $"{output.Lines.Count} 行 · {output.Elapsed.TotalSeconds:F1} 秒 · 平均置信度 {avgScore:P0}"
                : "未识别到文字。";
            OcrCopyButton.IsEnabled = output.Lines.Count > 0;
        }
        catch (OperationCanceledException)
        {
            OcrStatusText.Text = "已取消。";
        }
        catch (OcrException ex)
        {
            OcrStatusText.Text = ex.Message;
            OcrResultBox.Text = string.Empty;
        }
        catch (Exception ex)
        {
            // Last-resort guard: OCR must never crash the viewer.
            OcrStatusText.Text = "OCR 出现未知错误：" + ex.Message;
            OcrResultBox.Text = string.Empty;
        }
        finally
        {
            OcrRunButton.IsEnabled = true;
            OcrHeaderRunButton.IsEnabled = true;
        }
    }

    private void OnOcrCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastOcrText)) return;

        // Clipboard can be held briefly by other apps; retry a few times.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetText(_lastOcrText);
                OcrStatusText.Text = "已复制全部文字。";
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                OcrStatusText.Text = "复制失败：" + ex.Message;
                return;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelOcr();
        _animator.Stop();
        _document?.Dispose();
        _document = null;
        _ocrService.Dispose();
        base.OnClosed(e);
    }

    // An animation nobody can see should not keep ticking; resume on the way back.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _animator.Resume();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        _animator.Suspend();
    }

    // ---------- Status ----------

    private void OnViewChanged(object? sender, EventArgs e) => UpdateStatus();

    private void OnZoom100Click(object sender, RoutedEventArgs e) => Canvas.ZoomTo100();

    private void OnFitClick(object sender, RoutedEventArgs e) => Canvas.ZoomToFit();

    /// <summary>Info-only status bar (Spec §24): size · format · position | zoom.</summary>
    private void UpdateStatus()
    {
        if (_currentImage == null) return;

        string fileInfo = string.Empty;
        if (_currentPath != null && File.Exists(_currentPath))
        {
            var fi = new FileInfo(_currentPath);
            fileInfo = $"   {fi.Extension.TrimStart('.').ToUpperInvariant()} · {FormatBytes(fi.Length)}";
        }
        else if (_currentPath == null)
        {
            fileInfo = "   剪贴板图片";
        }

        string position = _currentIndex >= 0 ? $"   {_currentIndex + 1}/{_folderImages.Count}" : string.Empty;

        // A multi-frame file says so here too. The floating chip is navigation, this
        // is the image's own description — and for an animation the frame number
        // changes ten times a second, so only the total is reported.
        string frames = _document switch
        {
            { IsPaged: true } doc => $"   第 {_frameIndex + 1}/{doc.FrameCount} 页",
            { IsAnimated: true } doc => $"   动图 · {doc.FrameCount} 帧",
            _ => string.Empty,
        };

        UpdateTitle();
        StatusLeft.Text = $"{_currentImage.PixelWidth} × {_currentImage.PixelHeight}{fileInfo}{frames}{position}";
        StatusRight.Text = Canvas.Mode == Controls.ViewMode.Fit
            ? $"{Canvas.Zoom:P0}  Fit"
            : $"{Canvas.Zoom:P0}";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 20 => $"{bytes / 1048576.0:F1} MB",
        >= 1L << 10 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>Transient feedback (save/copy/paste) — never in the status bar.</summary>
    private void ShowToast(string message)
    {
        _toastTimer.Stop();
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Start();
    }
}
