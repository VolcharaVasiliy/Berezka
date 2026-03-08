using System.Diagnostics;
using Elochka.App.Models;
using Elochka.App.Services.Translation;

namespace Elochka.App.Services;

internal sealed class CaptureTranslationPipeline
{
    private static readonly string DebugLogPath = Path.Combine(AppContext.BaseDirectory, "pipeline-debug.log");
    private static readonly object DebugLogSync = new();

    private readonly ScreenCaptureService _screenCaptureService;
    private readonly ITextRecognitionService _ocrTextService;
    private readonly ITranslationProvider _translationProvider;

    public CaptureTranslationPipeline(
        ScreenCaptureService screenCaptureService,
        ITextRecognitionService ocrTextService,
        ITranslationProvider translationProvider)
    {
        _screenCaptureService = screenCaptureService;
        _ocrTextService = ocrTextService;
        _translationProvider = translationProvider;
    }

    public async Task<CaptureResult> ProcessAsync(Rectangle region, AppSettings settings, CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var captureStopwatch = Stopwatch.StartNew();
        using var capture = _screenCaptureService.Capture(region);
        captureStopwatch.Stop();

        var ocrStopwatch = Stopwatch.StartNew();
        var sourceText = await _ocrTextService.RecognizeAsync(capture, cancellationToken);
        ocrStopwatch.Stop();

        if (string.IsNullOrWhiteSpace(sourceText))
        {
            var emptyResult = new CaptureResult(
                string.Empty,
                string.Empty,
                "Текст не распознан. Попробуйте выделить более контрастную область.");
            AppendDebugLog(region, settings, captureStopwatch.Elapsed, ocrStopwatch.Elapsed, TimeSpan.Zero, totalStopwatch.Elapsed, emptyResult);
            return emptyResult;
        }

        if (!settings.TranslationEnabled)
        {
            var passthroughResult = new CaptureResult(
                sourceText,
                sourceText,
                "Показан распознанный текст без перевода.");
            AppendDebugLog(region, settings, captureStopwatch.Elapsed, ocrStopwatch.Elapsed, TimeSpan.Zero, totalStopwatch.Elapsed, passthroughResult);
            return passthroughResult;
        }

        var translationStopwatch = Stopwatch.StartNew();
        try
        {
            var translatedText = await _translationProvider.TranslateAsync(sourceText, settings, cancellationToken);
            translationStopwatch.Stop();

            if (string.IsNullOrWhiteSpace(translatedText))
            {
                var emptyTranslationResult = new CaptureResult(
                    sourceText,
                    sourceText,
                    "Сервис перевода вернул пустой ответ. Показан распознанный текст.");
                AppendDebugLog(region, settings, captureStopwatch.Elapsed, ocrStopwatch.Elapsed, translationStopwatch.Elapsed, totalStopwatch.Elapsed, emptyTranslationResult);
                return emptyTranslationResult;
            }

            var successResult = new CaptureResult(
                sourceText,
                translatedText,
                "Перевод готов.");
            AppendDebugLog(region, settings, captureStopwatch.Elapsed, ocrStopwatch.Elapsed, translationStopwatch.Elapsed, totalStopwatch.Elapsed, successResult);
            return successResult;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            translationStopwatch.Stop();
            var fallbackResult = new CaptureResult(
                sourceText,
                sourceText,
                "Перевод недоступен. Показан распознанный текст.",
                exception.Message);
            AppendDebugLog(region, settings, captureStopwatch.Elapsed, ocrStopwatch.Elapsed, translationStopwatch.Elapsed, totalStopwatch.Elapsed, fallbackResult);
            return fallbackResult;
        }
    }

    private static void AppendDebugLog(
        Rectangle region,
        AppSettings settings,
        TimeSpan captureElapsed,
        TimeSpan ocrElapsed,
        TimeSpan translationElapsed,
        TimeSpan totalElapsed,
        CaptureResult result)
    {
        var translationError = string.IsNullOrWhiteSpace(result.TranslationError)
            ? "-"
            : result.TranslationError.Replace(Environment.NewLine, " ", StringComparison.Ordinal);

        var line =
            $"{DateTime.Now:O} region={region.Width}x{region.Height}+{region.X}+{region.Y} provider={settings.TranslationProvider} " +
            $"captureMs={captureElapsed.TotalMilliseconds:F0} ocrMs={ocrElapsed.TotalMilliseconds:F0} " +
            $"translateMs={translationElapsed.TotalMilliseconds:F0} totalMs={totalElapsed.TotalMilliseconds:F0} " +
            $"srcChars={result.SourceText.Length} displayChars={result.DisplayText.Length} " +
            $"translationError={translationError}" +
            Environment.NewLine;

        lock (DebugLogSync)
        {
            File.AppendAllText(DebugLogPath, line);
        }
    }
}
