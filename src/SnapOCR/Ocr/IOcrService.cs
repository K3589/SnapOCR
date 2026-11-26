namespace SnapOCR.Ocr;

/// <summary>一次识别的输入：紧排 BGRA（stride = Width * 4）。</summary>
public readonly record struct OcrRequest(byte[] Bgra, int Width, int Height, int Stride);

/// <summary>一次识别的结果。</summary>
public readonly record struct OcrOutcome(bool Success, string Text, int LineCount, string? Error)
{
    public static OcrOutcome Ok(string text, int lineCount) => new(true, text, lineCount, null);

    public static OcrOutcome Failed(string error) => new(false, string.Empty, 0, error);
}

/// <summary>OCR 语言（Tag 例如 zh-Hans-CN）。</summary>
public readonly record struct OcrLanguageInfo(string Tag, string DisplayName);

/// <summary>
/// OCR 后端抽象。当前实现是 Windows.Media.Ocr；
/// 以后要换 RapidOCR / PaddleOCR ONNX，只需要另写一个实现，调用方不用改。
/// </summary>
public interface IOcrService
{
    /// <summary>引擎是否已就绪（预热完成）。</summary>
    bool IsAvailable { get; }

    /// <summary>引擎不可用时的原因，可直接展示给用户。</summary>
    string? AvailabilityMessage { get; }

    /// <summary>系统里安装了的 OCR 识别语言。</summary>
    IReadOnlyList<OcrLanguageInfo> AvailableLanguages { get; }

    /// <summary>按语言创建并预热引擎（进程内单例，重复调用只切换语言）。</summary>
    Task<bool> WarmupAsync(string languageTag);

    /// <summary>识别一块 BGRA 像素。</summary>
    Task<OcrOutcome> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default);
}
