using System;
using System.Collections.Generic;
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
    private BitmapSource? _currentImage;
    private string? _currentPath; // null when the image came from the clipboard

    private readonly IOcrService _ocrService = new OcrService();
    private CancellationTokenSource? _ocrCts;
    private string _lastOcrText = string.Empty;

    private AnnotationSession? _session;
    private AnnotationTool _activeTool = AnnotationTool.None;
    private Rect? _displayedCrop;

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

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
        _toastTimer.Tick += (s, e) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(InitialImagePath) && File.Exists(InitialImagePath))
            OpenImage(InitialImagePath);
    }

    // ---------- Opening ----------

    private void OnOpenClick(object sender, RoutedEventArgs e) => ShowOpenDialog();

    private void ShowOpenDialog()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Images (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All files (*.*)|*.*",
            Title = "Open Image"
        };
        if (dlg.ShowDialog(this) == true)
            OpenImage(dlg.FileName);
    }

    private void OpenImage(string path)
    {
        // Normalize separators/relative segments so folder-list matching works
        // even when the path arrives with forward slashes (e.g. from CLI args).
        path = Path.GetFullPath(path);

        if (!ImageService.IsSupported(path))
        {
            MessageBox.Show(this, "Unsupported image format.", AppDisplayName.Current,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _currentImage = ImageService.Load(path);
        }
        catch (Exception)
        {
            MessageBox.Show(this, "Unable to open this image.", AppDisplayName.Current,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ShowImage(_currentImage, path);
    }

    /// <summary>
    /// Makes an image current and resets all per-image state. Path is null for
    /// images that did not come from disk (clipboard paste).
    /// </summary>
    private void ShowImage(BitmapSource image, string? path)
    {
        _currentImage = image;
        _currentPath = path;

        Canvas.SetImage(image);
        _folderImages = path != null
            ? ImageService.GetImagesInSameFolder(path)
            : Array.Empty<string>();
        _currentIndex = path != null ? IndexOfPath(_folderImages, path) : -1;

        // Fresh edit state per image; the original bitmap stays untouched.
        _session = new AnnotationSession(image);
        _session.StateChanged += (s, e) => RefreshFromSession();
        _displayedCrop = null;
        SetTool(AnnotationTool.None);
        RefreshFromSession();

        EmptyState.Visibility = Visibility.Collapsed;
        SetImageUiEnabled(true);
        ResetOcrPanel();
        UpdateStatus();
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
        _session = null;
        _displayedCrop = null;
        SetTool(AnnotationTool.None);
        _currentImage = null;
        _currentPath = null;
        _folderImages = Array.Empty<string>();
        _currentIndex = -1;
        Canvas.Clear();
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

    private void Navigate(int delta)
    {
        if (_currentImage == null || _folderImages.Count == 0 || _currentIndex < 0) return;
        int next = _currentIndex + delta;
        if (next < 0 || next >= _folderImages.Count) return;
        OpenImage(_folderImages[next]);
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
            OpenImage(path);
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
    /// Toolbar Save button: asks before overwriting (确定 / 另存为 / 取消).
    /// The Ctrl+S shortcut skips this confirmation on purpose.
    /// </summary>
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_session == null || _currentImage == null) return;

        bool hasEdits = _session.Annotations.Count > 0 || _session.CropRegion != null;
        if (!hasEdits)
        {
            ShowToast("没有改动，无需保存。");
            return;
        }

        // Clipboard images have no path; BMP has no encoder here → straight to Save As.
        var ext = _currentPath != null ? Path.GetExtension(_currentPath).ToLowerInvariant() : null;
        if (_currentPath == null || ext is not (".png" or ".jpg" or ".jpeg" or ".webp"))
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
        if (_session == null || _currentImage == null) return;

        bool hasEdits = _session.Annotations.Count > 0 || _session.CropRegion != null;
        if (!hasEdits)
        {
            ShowToast("没有改动，无需保存。");
            return;
        }

        // Clipboard images have no path; BMP has no encoder here → both go to Save As.
        var ext = _currentPath != null ? Path.GetExtension(_currentPath).ToLowerInvariant() : null;
        if (_currentPath == null || ext is not (".png" or ".jpg" or ".jpeg" or ".webp"))
        {
            SaveAnnotated();
            return;
        }

        try
        {
            AnnotationRenderer.SaveToFile(
                _session.Original, _session.Annotations, _session.CropRegion, _currentPath);
            ShowToast($"已覆盖保存：{Path.GetFileName(_currentPath)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, AppDisplayName.Current,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Ctrl+C: copy the current result (original + annotations + crop).</summary>
    private void CopyImageToClipboard()
    {
        if (_session == null || _currentImage == null) return;

        BitmapSource image;
        if (_session.Annotations.Count > 0 || _session.CropRegion != null)
        {
            var png = AnnotationRenderer.RenderToPngBytes(
                _session.Original, _session.Annotations, _session.CropRegion);
            image = PngBytesToBitmapSource(png);
        }
        else
        {
            image = _currentImage;
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
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|WebP image (*.webp)|*.webp",
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
        _ocrService.Dispose();
        base.OnClosed(e);
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

        UpdateTitle();
        StatusLeft.Text = $"{_currentImage.PixelWidth} × {_currentImage.PixelHeight}{fileInfo}{position}";
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
