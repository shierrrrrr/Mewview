using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RapidOcrNet;
using SkiaSharp;

namespace Mewview.Services;

/// <summary>
/// RapidOcrNet-based local OCR (PP-OCRv5 ONNX models, Apache-2.0).
/// Engines are created lazily per language and reused across calls.
/// All inference runs on a background thread; failures surface as OcrException.
/// </summary>
public sealed class OcrService : IOcrService
{
    private const string DetModelFile = "ch_PP-OCRv5_det_mobile.onnx";
    private const string ClsModelFile = "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx";

    private static readonly Dictionary<OcrLanguage, (string RecModel, string Dict)> LanguageFiles = new()
    {
        [OcrLanguage.ChineseEnglish] = ("ch_PP-OCRv5_rec_mobile.onnx", "ppocrv5_dict.txt"),
        [OcrLanguage.Korean] = ("korean_PP-OCRv5_rec_mobile.onnx", "ppocrv5_korean_dict.txt"),
    };

    // Official PP-OCRv5 model download URLs (RapidOCR default_models.yaml).
    // Apache-2.0 models hosted on ModelScope; downloaded once on first use.
    private const string ModelBaseUrl =
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2";

    private static readonly Dictionary<string, string> ModelUrls = new()
    {
        [DetModelFile] = ModelBaseUrl + "/onnx/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx",
        [ClsModelFile] = ModelBaseUrl + "/onnx/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
        ["ch_PP-OCRv5_rec_mobile.onnx"] = ModelBaseUrl + "/onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx",
        ["ppocrv5_dict.txt"] = ModelBaseUrl + "/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt",
        ["korean_PP-OCRv5_rec_mobile.onnx"] = ModelBaseUrl + "/onnx/PP-OCRv5/rec/korean_PP-OCRv5_rec_mobile.onnx",
        ["ppocrv5_korean_dict.txt"] = ModelBaseUrl + "/paddle/PP-OCRv5/rec/korean_PP-OCRv5_rec_mobile/ppocrv5_korean_dict.txt",
    };

    private readonly Dictionary<OcrLanguage, RapidOcr> _engines = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _disposed;

    public string ModelsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mewview", "models");

    public bool IsLanguageReady(OcrLanguage language, out IReadOnlyList<string> missingFiles)
    {
        var required = new List<string> { DetModelFile, ClsModelFile };
        var (rec, dict) = LanguageFiles[language];
        required.Add(rec);
        required.Add(dict);

        missingFiles = required
            .Where(f => !File.Exists(Path.Combine(ModelsDirectory, f)))
            .ToList();
        return missingFiles.Count == 0;
    }

    /// <summary>
    /// Downloads the model files required by <paramref name="language"/> if they
    /// are missing. Each file is written to a temporary ".part" file and moved
    /// into place only after a full, non-empty download, so an interrupted run
    /// can be resumed without re-downloading already-complete files.
    /// </summary>
    public async Task EnsureModelsAsync(
        OcrLanguage language,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var required = new List<string> { DetModelFile, ClsModelFile };
        var (rec, dict) = LanguageFiles[language];
        required.Add(rec);
        required.Add(dict);

        var missing = required
            .Where(f => !File.Exists(Path.Combine(ModelsDirectory, f)))
            .ToList();
        if (missing.Count == 0)
            return;

        Directory.CreateDirectory(ModelsDirectory);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mewview/0.1 (local OCR model download)");

        for (var i = 0; i < missing.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = missing[i];
            var url = ModelUrls[file];
            var dest = Path.Combine(ModelsDirectory, file);
            var tmp = dest + ".part";

            try
            {
                using var response = await http.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var src = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var dst = new FileStream(
                    tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tmp);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                throw new OcrException($"模型下载失败（{file}）：{ex.Message}", ex);
            }

            if (new FileInfo(tmp).Length == 0)
            {
                TryDelete(tmp);
                throw new OcrException($"模型下载失败（{file}）：下载内容为空。");
            }

            File.Move(tmp, dest, overwrite: true);
            progress?.Report((double)(i + 1) / missing.Count);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of partial downloads.
        }
    }

    public async Task<OcrOutput> RecognizeAsync(
        BitmapSource image,
        OcrLanguage language,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (image == null) throw new OcrException("没有可识别的图片。");
        if (!image.IsFrozen) throw new ArgumentException("BitmapSource must be frozen.", nameof(image));

        if (!IsLanguageReady(language, out var missing))
        {
            throw new OcrException(
                "OCR 模型文件缺失：" + string.Join("、", missing) +
                $"。请将模型放入 {ModelsDirectory} 后重试。");
        }

        var engine = await GetEngineAsync(language, cancellationToken).ConfigureAwait(false);

        SKBitmap bitmap;
        try
        {
            bitmap = ToSKBitmap(image);
        }
        catch (Exception ex)
        {
            throw new OcrException("图片格式转换失败，无法识别。", ex);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await engine
                .DetectAsync(bitmap, RapidOcrOptions.Default, cancellationToken)
                .ConfigureAwait(false);
            sw.Stop();

            var lines = (result.TextBlocks ?? Array.Empty<TextBlock>())
                .Select(b => new OcrLine(b.Text ?? string.Empty, b.BoxScore))
                .Where(l => l.Text.Length > 0)
                .ToList();
            var fullText = string.Join(Environment.NewLine, lines.Select(l => l.Text));
            return new OcrOutput(fullText, lines, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OcrException("OCR 识别失败：" + ex.Message, ex);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <summary>Lazy per-language engine creation, serialized to avoid double init.</summary>
    private async Task<RapidOcr> GetEngineAsync(OcrLanguage language, CancellationToken ct)
    {
        if (_engines.TryGetValue(language, out var ready))
            return ready;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_engines.TryGetValue(language, out ready))
                return ready;

            var (rec, dict) = LanguageFiles[language];
            var engine = await Task.Run(() =>
            {
                var ocr = new RapidOcr();
                ocr.InitModels(
                    Path.Combine(ModelsDirectory, DetModelFile),
                    Path.Combine(ModelsDirectory, ClsModelFile),
                    Path.Combine(ModelsDirectory, rec),
                    Path.Combine(ModelsDirectory, dict),
                    Math.Max(1, Environment.ProcessorCount / 2));
                return ocr;
            }, ct).ConfigureAwait(false);

            _engines[language] = engine;
            return engine;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OcrException("OCR 模型加载失败：" + ex.Message, ex);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Copies a frozen BitmapSource into an SKBitmap (BGRA).</summary>
    private static SKBitmap ToSKBitmap(BitmapSource source) =>
        BitmapConversion.ToSKBitmap(source);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var engine in _engines.Values)
            engine.Dispose();
        _engines.Clear();
        _initLock.Dispose();
    }
}
