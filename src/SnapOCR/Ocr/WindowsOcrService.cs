using System.Diagnostics;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace SnapOCR.Ocr;

/// <summary>
/// Windows.Media.Ocr 的本地离线实现。进程内单例，启动时预热。
/// 整个过程在本地完成，不联网、不上传像素。
/// </summary>
public sealed class WindowsOcrService : IOcrService, IDisposable
{
    private static readonly TimeSpan RecognitionTimeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OcrEngine? _engine;
    private string _languageTag = string.Empty;
    private string? _availabilityMessage;
    private bool _warmedUp;
    private bool _disposed;

    public bool IsAvailable => _engine is not null;

    public string? AvailabilityMessage => _availabilityMessage;

    public IReadOnlyList<OcrLanguageInfo> AvailableLanguages
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages
                    .Select(l => new OcrLanguageInfo(
                        l.LanguageTag,
                        string.IsNullOrWhiteSpace(l.DisplayName) ? l.LanguageTag : l.DisplayName))
                    .ToList();
            }
            catch (Exception)
            {
                return Array.Empty<OcrLanguageInfo>();
            }
        }
    }

    /// <summary>系统一共有多少种 OCR 语言可用（用于「缺语言包」提示）。</summary>
    public static int CountAvailableLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages.Count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public async Task<bool> WarmupAsync(string languageTag)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            if (_disposed)
            {
                return false;
            }

            if (_engine is not null
                && _warmedUp
                && string.Equals(_languageTag, languageTag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var engine = CreateEngine(languageTag);
            if (engine is null)
            {
                _engine = null;
                _warmedUp = false;
                return false;
            }

            _engine = engine;
            _languageTag = languageTag ?? string.Empty;
            _warmedUp = true;

            // 用一张 32×32 的浅色图跑一次完整识别，把 OCR 管线的首次初始化成本挪到启动阶段。
            try
            {
                using var probe = await SoftwareBitmapFactory
                    .CreateFromBgraAsync(new byte[32 * 32 * 4], 32, 32, 32 * 4)
                    .ConfigureAwait(false);
                await engine.RecognizeAsync(probe).AsTask().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 预热失败不影响后续真实识别，忽略。
            }

            return true;
        }
        finally
        {
            ReleaseGateSafe();
        }
    }

    public async Task<OcrOutcome> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Width <= 0 || request.Height <= 0)
        {
            return OcrOutcome.Failed("选区为空。");
        }

        if (_disposed)
        {
            return OcrOutcome.Failed("OCR 服务已关闭。");
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OcrOutcome.Failed("已取消。");
        }
        catch (ObjectDisposedException)
        {
            return OcrOutcome.Failed("OCR 服务已关闭。");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (_disposed)
            {
                return OcrOutcome.Failed("OCR 服务已关闭。");
            }

            var engine = _engine;
            if (engine is null)
            {
                // 启动后立即按热键时 WarmupAsync 可能还没抢到锁，按需懒初始化兜底。
                engine = CreateEngine(_languageTag);
                if (engine is null)
                {
                    return OcrOutcome.Failed(_availabilityMessage ?? "OCR 引擎未初始化。");
                }

                _engine = engine;
            }

            using var bitmap = await SoftwareBitmapFactory
                .CreateFromBgraAsync(request.Bgra, request.Width, request.Height, request.Stride)
                .ConfigureAwait(false);

            var prepared = await SoftwareBitmapFactory
                .EnsureMinimumHeightAsync(bitmap, SoftwareBitmapFactory.MinOcrHeight, SoftwareBitmapFactory.MaxUpscale)
                .ConfigureAwait(false);

            SoftwareBitmap? shrunk = null;
            var maxDimension = (int)OcrEngine.MaxImageDimension;
            if (Math.Max(prepared.PixelWidth, prepared.PixelHeight) > maxDimension)
            {
                shrunk = await SoftwareBitmapFactory
                    .EnsureMaximumDimensionAsync(prepared, maxDimension)
                    .ConfigureAwait(false);
            }

            var input = shrunk ?? prepared;

            try
            {
                var operation = engine.RecognizeAsync(input).AsTask(cancellationToken);
                var result = await operation.WaitAsync(RecognitionTimeout, cancellationToken).ConfigureAwait(false);

                var text = TextComposer.Compose(result.Lines.Select(line => line.Text));
                Debug.WriteLine($"[SnapOCR] OCR {request.Width}x{request.Height} → {result.Lines.Count} 行 / {text.Length} 字 / {stopwatch.ElapsedMilliseconds} ms");

                return OcrOutcome.Ok(text, result.Lines.Count);
            }
            finally
            {
                shrunk?.Dispose();
                if (!ReferenceEquals(prepared, bitmap))
                {
                    prepared.Dispose();
                }
            }
        }
        catch (TimeoutException)
        {
            return OcrOutcome.Failed("识别超时。");
        }
        catch (OperationCanceledException)
        {
            return OcrOutcome.Failed("识别超时或已取消。");
        }
        catch (Exception ex)
        {
            return OcrOutcome.Failed(ex.Message);
        }
        finally
        {
            stopwatch.Stop();
            ReleaseGateSafe();
        }
    }

    private OcrEngine? CreateEngine(string languageTag)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(languageTag))
            {
                var fallback = OcrEngine.TryCreateFromUserProfileLanguages();
                if (fallback is null)
                {
                    _availabilityMessage = "系统没有可用的 OCR 语言包。";
                }
                else
                {
                    _availabilityMessage = null;
                }

                return fallback;
            }

            var language = new Language(languageTag);
            if (!OcrEngine.IsLanguageSupported(language))
            {
                _availabilityMessage = $"系统未安装 {languageTag} 的 OCR 语言包。";
                return null;
            }

            var engine = OcrEngine.TryCreateFromLanguage(language);
            _availabilityMessage = engine is null ? $"无法为 {languageTag} 创建 OCR 引擎。" : null;
            return engine;
        }
        catch (Exception ex)
        {
            _availabilityMessage = ex.Message;
            return null;
        }
    }

    private void ReleaseGateSafe()
    {
        try
        {
            _gate.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine = null;
        _gate.Dispose();
    }
}
