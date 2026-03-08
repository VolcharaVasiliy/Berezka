using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using Elochka.App.Models;

namespace Elochka.App.Services;

internal sealed class PaddleOcrTextService : ITextRecognitionService, IDisposable
{
    private const string DefaultPythonPath = @"F:\DevTools\Python311\python.exe";
    private const string SetupScriptPath = @"F:\Projects\elochka\scripts\setup_local_paddle_ocr.ps1";
    private const string PythonEnvVar = "ELOCHKA_PYTHON";
    private const string PaddleHomeEnvVar = "ELOCHKA_PADDLE_HOME";
    private const string PaddlexCacheHomeEnvVar = "ELOCHKA_PADDLEX_CACHE_HOME";
    private const int WorkerThreadCount = 3;
    private const int TinyTextMinSideThreshold = 140;
    private const int SmallTextMinSideThreshold = 220;
    private const int TinyCaptureAreaThreshold = 180_000;
    private const int SmallCaptureAreaThreshold = 360_000;
    private const int MaxPreparedPixelCount = 900_000;
    private const double TinyCaptureScale = 1.35;
    private const double SmallCaptureScale = 1.15;
    private static readonly TimeSpan WorkerIdleLifetime = TimeSpan.FromMinutes(10);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _workerGate = new(1, 1);
    private readonly object _stderrSync = new();
    private readonly string _cacheDirectory;
    private readonly StringBuilder _stderrBuffer = new();

    private Process? _workerProcess;
    private StreamWriter? _workerInput;
    private StreamReader? _workerOutput;
    private System.Threading.Timer? _idleTimer;
    private DateTime _lastWorkerUseUtc = DateTime.MinValue;
    private bool _disposed;

    public PaddleOcrTextService()
    {
        _cacheDirectory = Path.Combine(AppContext.BaseDirectory, "ocr-cache");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<string> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(bitmap);

        var pythonExecutable = ResolvePythonExecutable();
        var scriptPath = ResolveScriptPath();
        var paddleHome = ResolvePaddleHomePath();
        var paddlexCacheHome = ResolvePaddlexCacheHomePath();
        var imagePath = string.Empty;

        try
        {
            using var prepared = PrepareBitmap(bitmap);
            imagePath = Path.Combine(_cacheDirectory, $"{Guid.NewGuid():N}.bmp");
            prepared.Save(imagePath, ImageFormat.Bmp);

            await _workerGate.WaitAsync(cancellationToken);
            try
            {
                CancelIdleShutdown_NoLock();
                return await ExecuteOcrAsync(imagePath, cancellationToken, pythonExecutable, scriptPath, paddleHome, paddlexCacheHome);
            }
            finally
            {
                _lastWorkerUseUtc = DateTime.UtcNow;
                if (!_disposed)
                {
                    ScheduleIdleShutdown_NoLock();
                }

                _workerGate.Release();
            }
        }
        finally
        {
            TryDeleteFile(imagePath);
        }
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var pythonExecutable = ResolvePythonExecutable();
        var scriptPath = ResolveScriptPath();
        var paddleHome = ResolvePaddleHomePath();
        var paddlexCacheHome = ResolvePaddlexCacheHomePath();

        await _workerGate.WaitAsync(cancellationToken);
        try
        {
            CancelIdleShutdown_NoLock();
            EnsureWorkerStarted_NoLock(pythonExecutable, scriptPath, paddleHome, paddlexCacheHome);
        }
        finally
        {
            _lastWorkerUseUtc = DateTime.UtcNow;
            if (!_disposed)
            {
                ScheduleIdleShutdown_NoLock();
            }

            _workerGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _workerGate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _idleTimer?.Dispose();
            _idleTimer = null;
            DisposeWorker_NoLock();
        }
        finally
        {
            _workerGate.Release();
            _workerGate.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async Task<string> ExecuteOcrAsync(
        string imagePath,
        CancellationToken cancellationToken,
        string pythonExecutable,
        string scriptPath,
        string paddleHome,
        string paddlexCacheHome)
    {
        EnsureWorkerStarted_NoLock(pythonExecutable, scriptPath, paddleHome, paddlexCacheHome);

        using var registration = cancellationToken.Register(KillWorkerForCancellation);

        if (_workerInput is null || _workerOutput is null)
        {
            throw new InvalidOperationException("OCR worker streams are unavailable.");
        }

        var payload = JsonSerializer.Serialize(new OfflineOcrRequest(imagePath));
        await _workerInput.WriteAsync(payload.AsMemory(), cancellationToken);
        await _workerInput.WriteAsync(Environment.NewLine.AsMemory(), cancellationToken);
        await _workerInput.FlushAsync();

        var responseLine = await _workerOutput.ReadLineAsync().WaitAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new InvalidOperationException(BuildWorkerFailureMessage("OCR worker returned an empty response."));
        }

        var response = JsonSerializer.Deserialize<OfflineOcrResponse>(NormalizeJsonResponse(responseLine));
        if (response is null)
        {
            throw new InvalidOperationException(BuildWorkerFailureMessage("OCR worker response could not be parsed."));
        }

        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            throw new InvalidOperationException(BuildWorkerFailureMessage(response.Error.Trim()));
        }

        return NormalizeText(response.Text);
    }

    private void EnsureWorkerStarted_NoLock(string pythonExecutable, string scriptPath, string paddleHome, string paddlexCacheHome)
    {
        if (_workerProcess is { HasExited: false } && _workerInput is not null && _workerOutput is not null)
        {
            return;
        }

        DisposeWorker_NoLock();
        ClearWorkerErrorBuffer();

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            Arguments = $"-X utf8 -u \"{scriptPath}\" --lang ru --server",
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PADDLE_HOME"] = paddleHome;
        startInfo.Environment["PADDLE_PDX_CACHE_HOME"] = paddlexCacheHome;
        startInfo.Environment["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True";
        startInfo.Environment["OMP_NUM_THREADS"] = WorkerThreadCount.ToString();
        startInfo.Environment["MKL_NUM_THREADS"] = WorkerThreadCount.ToString();

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        process.ErrorDataReceived += OnWorkerErrorDataReceived;

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start the OCR worker.");
        }

        TryLowerProcessPriority(process);
        process.BeginErrorReadLine();

        _workerProcess = process;
        _workerInput = process.StandardInput;
        _workerOutput = process.StandardOutput;
    }

    private static Bitmap PrepareBitmap(Bitmap original)
    {
        var scale = GetRecommendedScale(original.Width, original.Height);
        var width = Math.Max((int)Math.Round(original.Width * scale), original.Width);
        var height = Math.Max((int)Math.Round(original.Height * scale), original.Height);
        var pixelFormat = PixelFormat.Format24bppRgb;
        var resized = new Bitmap(width, height, pixelFormat);
        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = scale > 1.0 ? InterpolationMode.HighQualityBilinear : InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImage(original, new Rectangle(Point.Empty, resized.Size));
        return resized;
    }

    private static double GetRecommendedScale(int width, int height)
    {
        var minSide = Math.Min(width, height);
        var area = (long)width * height;
        var scale = 1.0;

        if (minSide <= TinyTextMinSideThreshold && area <= TinyCaptureAreaThreshold)
        {
            scale = TinyCaptureScale;
        }
        else if (minSide <= SmallTextMinSideThreshold && area <= SmallCaptureAreaThreshold)
        {
            scale = SmallCaptureScale;
        }

        if (scale <= 1.0)
        {
            return 1.0;
        }

        var scaledArea = area * scale * scale;
        if (scaledArea <= MaxPreparedPixelCount)
        {
            return scale;
        }

        var cappedScale = Math.Sqrt(MaxPreparedPixelCount / (double)area);
        return Math.Clamp(cappedScale, 1.0, scale);
    }

    private static string ResolvePythonExecutable()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable(PythonEnvVar),
            Path.Combine(AppContext.BaseDirectory, "python", "python.exe"),
            DefaultPythonPath,
            "python",
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!Path.IsPathRooted(candidate))
            {
                return candidate;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Offline Python runtime not found. Install it on F: or set {PythonEnvVar}."
        );
    }

    private static string ResolveScriptPath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Scripts", "offline_ocr.py");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new InvalidOperationException("offline_ocr.py was not found in the application output.");
    }

    private static string ResolvePaddleHomePath()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable(PaddleHomeEnvVar),
            Path.Combine(AppContext.BaseDirectory, "paddle-home"),
            @"F:\Projects\elochka\.paddle-home",
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!Directory.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
            }

            return candidate;
        }

        throw new InvalidOperationException(
            $"PaddleOCR home directory could not be resolved. Run {SetupScriptPath} or set {PaddleHomeEnvVar}."
        );
    }

    private static string ResolvePaddlexCacheHomePath()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable(PaddlexCacheHomeEnvVar),
            Path.Combine(AppContext.BaseDirectory, "paddlex-cache"),
            @"F:\Projects\elochka\.paddlex-cache",
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!Directory.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
            }

            return candidate;
        }

        throw new InvalidOperationException(
            $"PaddleOCR cache directory could not be resolved. Run {SetupScriptPath} or set {PaddlexCacheHomeEnvVar}."
        );
    }

    private static void TryLowerProcessPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            process.PriorityBoostEnabled = false;
        }
        catch
        {
        }
    }

    private static string NormalizeJsonResponse(string response)
    {
        if (string.IsNullOrEmpty(response))
        {
            return string.Empty;
        }

        return response.TrimStart('\uFEFF', '\u200B', '\u2060', ' ', '\t', '\r', '\n');
    }

    private static string NormalizeText(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            rawText
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0));
    }

    private void OnWorkerErrorDataReceived(object sender, DataReceivedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            return;
        }

        lock (_stderrSync)
        {
            if (_stderrBuffer.Length > 4096)
            {
                _stderrBuffer.Remove(0, _stderrBuffer.Length - 2048);
            }

            _stderrBuffer.AppendLine(eventArgs.Data);
        }
    }

    private void ScheduleIdleShutdown_NoLock()
    {
        _idleTimer ??= new System.Threading.Timer(_ => OnIdleTimerElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _idleTimer.Change(WorkerIdleLifetime, Timeout.InfiniteTimeSpan);
    }

    private void CancelIdleShutdown_NoLock()
    {
        _idleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnIdleTimerElapsed()
    {
        if (_disposed || !_workerGate.Wait(0))
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            if (DateTime.UtcNow - _lastWorkerUseUtc < WorkerIdleLifetime)
            {
                ScheduleIdleShutdown_NoLock();
                return;
            }

            DisposeWorker_NoLock();
        }
        finally
        {
            _workerGate.Release();
        }
    }

    private void KillWorkerForCancellation()
    {
        try
        {
            if (_workerProcess is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private void DisposeWorker_NoLock()
    {
        if (_workerProcess is not null)
        {
            try
            {
                _workerProcess.ErrorDataReceived -= OnWorkerErrorDataReceived;
            }
            catch
            {
            }

            try
            {
                if (!_workerProcess.HasExited)
                {
                    _workerProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                _workerProcess.Dispose();
            }
            catch
            {
            }
        }

        _workerInput = null;
        _workerOutput = null;
        _workerProcess = null;
    }

    private string BuildWorkerFailureMessage(string message)
    {
        var stderr = GetWorkerErrorSnapshot();
        return string.IsNullOrWhiteSpace(stderr) ? message : $"{message} {stderr}";
    }

    private string GetWorkerErrorSnapshot()
    {
        lock (_stderrSync)
        {
            return _stderrBuffer.ToString().Trim();
        }
    }

    private void ClearWorkerErrorBuffer()
    {
        lock (_stderrSync)
        {
            _stderrBuffer.Clear();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record OfflineOcrRequest(string ImagePath);

    private sealed record OfflineOcrResponse(string? Text, string? Error);
}
