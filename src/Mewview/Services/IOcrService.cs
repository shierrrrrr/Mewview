using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Mewview.Services;

/// <summary>
/// OCR language selection. English text is covered by the Chinese model
/// (PP-OCRv5 ch dict includes ASCII), so there is no separate English entry.
/// </summary>
public enum OcrLanguage
{
    /// <summary>Chinese + English (ch_PP-OCRv5_rec).</summary>
    ChineseEnglish,

    /// <summary>Korean (korean_PP-OCRv5_rec).</summary>
    Korean,
}

/// <summary>One recognized text line with its confidence score (0..1).</summary>
public sealed record OcrLine(string Text, double Score);

/// <summary>Result of a single OCR run.</summary>
public sealed record OcrOutput(
    string FullText,
    IReadOnlyList<OcrLine> Lines,
    TimeSpan Elapsed);

/// <summary>
/// OCR failure that is safe to show to the user (model missing, decode error,
/// inference error). Never exposes raw stack traces in the message.
/// </summary>
public sealed class OcrException : Exception
{
    public OcrException(string message) : base(message) { }
    public OcrException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Local, offline OCR. Everything runs in-process via ONNX Runtime;
/// no image data ever leaves the machine.
/// </summary>
public interface IOcrService : IDisposable
{
    /// <summary>Directory where the ONNX models and dictionaries live.</summary>
    string ModelsDirectory { get; }

    /// <summary>
    /// Checks whether all model files required for the language exist.
    /// When false, <paramref name="missingFiles"/> lists the missing file names.
    /// </summary>
    bool IsLanguageReady(OcrLanguage language, out IReadOnlyList<string> missingFiles);

    /// <summary>
    /// Downloads any missing model files for the language into <see cref="ModelsDirectory"/>.
    /// No-op when the models are already present. Reports overall progress (0..1)
    /// through <paramref name="progress"/>. Throws <see cref="OcrException"/> on failure.
    /// This is the only network access in the application and is always user-initiated.
    /// </summary>
    Task EnsureModelsAsync(
        OcrLanguage language,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Recognizes text in the given image. The BitmapSource must be frozen.
    /// Models are loaded lazily on first use per language.
    /// Throws <see cref="OcrException"/> on any failure — callers must catch it.
    /// </summary>
    Task<OcrOutput> RecognizeAsync(
        BitmapSource image,
        OcrLanguage language,
        CancellationToken cancellationToken = default);
}
