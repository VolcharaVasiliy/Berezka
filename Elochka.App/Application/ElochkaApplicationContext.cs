using System.Drawing;
using System.Runtime.InteropServices;
using Elochka.App.Forms;
using Elochka.App.Interop;
using Elochka.App.Models;
using Elochka.App.Services;
using Elochka.App.Services.Translation;

namespace Elochka.App.Application;

internal sealed class ElochkaApplicationContext : ApplicationContext
{
    private const int HotkeyId = 0xE110;
    private const int DoubleTapDelayMs = 275;

    private readonly HotkeyWindow _hotkeyWindow;
    private readonly SettingsStore _settingsStore;
    private readonly NotifyIcon _notifyIcon;
    private readonly ResultForm _resultForm;
    private readonly System.Windows.Forms.Timer _singleTapTimer;
    private readonly ToolStripMenuItem _hotKeyMenuItem;
    private readonly ToolStripMenuItem _repeatMenuItem;
    private readonly ToolStripMenuItem _pauseMenuItem;
    private readonly HttpClient _httpClient;
    private PaddleOcrTextService? _ocrTextService;
    private LocalNllbTranslationProvider? _localNllbTranslationProvider;
    private RoutingTranslationProvider? _translationProvider;

    private AppSettings _settings;
    private CaptureTranslationPipeline? _pipeline;
    private SelectionOverlayForm? _selectionOverlay;
    private CancellationTokenSource? _activeCaptureCts;
    private Rectangle _lastRegion;

    public ElochkaApplicationContext()
    {
        _settingsStore = new SettingsStore(Path.Combine(AppContext.BaseDirectory, "settings.ini"));
        _settings = _settingsStore.Load();

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12),
        };

        TryInitializePipeline();

        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotkeyPressed += OnHotkeyPressed;

        _singleTapTimer = new System.Windows.Forms.Timer
        {
            Interval = DoubleTapDelayMs,
        };
        _singleTapTimer.Tick += (_, _) =>
        {
            _singleTapTimer.Stop();
            ShowSelectionOverlay();
        };

        _resultForm = new ResultForm();
        _resultForm.ApplySettings(_settings);

        var trayMenu = new ContextMenuStrip();
        _hotKeyMenuItem = new ToolStripMenuItem { Enabled = false };
        _repeatMenuItem = new ToolStripMenuItem("Перевести последнюю область", null, (_, _) => ProcessLastRegion());
        _pauseMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => TogglePause());
        var settingsMenuItem = new ToolStripMenuItem("Настройки...", null, (_, _) => OpenSettings());
        var exitMenuItem = new ToolStripMenuItem("Выход", null, (_, _) => ExitApplication());

        trayMenu.Items.AddRange(
            new ToolStripItem[]
            {
                _hotKeyMenuItem,
                _repeatMenuItem,
                _pauseMenuItem,
                new ToolStripSeparator(),
                settingsMenuItem,
                exitMenuItem,
            });

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = trayMenu,
            Icon = SystemIcons.Application,
            Text = "Ёлочка",
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => TogglePause();

        RegisterHotKey();
        RefreshUiState();
        StartBackgroundWarmup();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelActiveCapture();
            User32.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
            _selectionOverlay?.Dispose();
            _resultForm.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _singleTapTimer.Dispose();
            _hotkeyWindow.Dispose();
            _ocrTextService?.Dispose();
            _translationProvider?.Dispose();
            _httpClient.Dispose();
        }

        base.Dispose(disposing);
    }

    private void TryInitializePipeline()
    {
        try
        {
            _ocrTextService = new PaddleOcrTextService();
            _localNllbTranslationProvider = new LocalNllbTranslationProvider();
            _translationProvider = new RoutingTranslationProvider(
                new Dictionary<TranslationProviderKind, ITranslationProvider>
                {
                    [TranslationProviderKind.LibreTranslate] = new LibreTranslateTranslationProvider(_httpClient),
                    [TranslationProviderKind.YandexCloud] = new YandexCloudTranslationProvider(_httpClient),
                    [TranslationProviderKind.DeepL] = new DeepLTranslationProvider(_httpClient),
                    [TranslationProviderKind.LocalNllb] = _localNllbTranslationProvider,
                });
            _pipeline = new CaptureTranslationPipeline(
                new ScreenCaptureService(),
                _ocrTextService,
                _translationProvider);
        }
        catch (Exception exception)
        {
            _pipeline = null;
            _ocrTextService?.Dispose();
            _ocrTextService = null;
            _localNllbTranslationProvider = null;
            _translationProvider?.Dispose();
            _translationProvider = null;
            _settings.Paused = true;
            MessageBox.Show(
                $"Не удалось инициализировать OCR: {exception.Message}",
                "Ёлочка",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void RegisterHotKey()
    {
        User32.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
        var modifiers = _settings.HotKeyMode.GetModifiers();
        var virtualKey = (uint)_settings.HotKeyMode.GetKey();

        if (!User32.RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, modifiers, virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            ShowBalloon("Горячая клавиша", $"Не удалось зарегистрировать хоткей. Win32={error}");
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (_settings.Paused || _activeCaptureCts is not null || _selectionOverlay is not null)
        {
            return;
        }

        if (_lastRegion.IsEmpty)
        {
            ShowSelectionOverlay();
            return;
        }

        if (_singleTapTimer.Enabled)
        {
            _singleTapTimer.Stop();
            _ = ProcessRegionAsync(_lastRegion);
            return;
        }

        _singleTapTimer.Start();
    }

    private void ShowSelectionOverlay()
    {
        if (_selectionOverlay is not null || _settings.Paused)
        {
            return;
        }

        _selectionOverlay = new SelectionOverlayForm(_lastRegion);
        _selectionOverlay.SelectionCompleted += OnSelectionCompleted;
        _selectionOverlay.SelectionCancelled += OnSelectionCancelled;
        _selectionOverlay.FormClosed += (_, _) => _selectionOverlay = null;
        _selectionOverlay.Show();
    }

    private void OnSelectionCompleted(Rectangle selection)
    {
        _lastRegion = selection;
        _ = ProcessRegionAsync(selection);
    }

    private void OnSelectionCancelled()
    {
        _selectionOverlay = null;
    }

    private async Task ProcessRegionAsync(Rectangle region)
    {
        if (_pipeline is null)
        {
            ShowBalloon("OCR недоступен", "Не удалось инициализировать OCR.");
            return;
        }

        CancelActiveCapture();

        var settingsSnapshot = _settings.Clone();
        _activeCaptureCts = new CancellationTokenSource();
        var currentCts = _activeCaptureCts;

        RefreshUiState();
        _resultForm.ShowLoading(settingsSnapshot, region);

        try
        {
            var result = await _pipeline.ProcessAsync(region, settingsSnapshot, currentCts.Token);
            if (!currentCts.IsCancellationRequested)
            {
                _resultForm.ShowResult(result, region, settingsSnapshot);

                if (!string.IsNullOrWhiteSpace(result.TranslationError))
                {
                    ShowBalloon("Перевод недоступен", result.TranslationError);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            var errorResult = new CaptureResult(
                string.Empty,
                string.Empty,
                "Ошибка обработки области.",
                exception.Message);
            _resultForm.ShowResult(errorResult, region, settingsSnapshot);
            ShowBalloon("Ошибка", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_activeCaptureCts, currentCts))
            {
                _activeCaptureCts.Dispose();
                _activeCaptureCts = null;
            }

            RefreshUiState();
        }
    }

    private void ProcessLastRegion()
    {
        if (_lastRegion.IsEmpty || _activeCaptureCts is not null || _settings.Paused)
        {
            return;
        }

        _ = ProcessRegionAsync(_lastRegion);
    }

    private void TogglePause()
    {
        _settings.Paused = !_settings.Paused;
        _settingsStore.Save(_settings);
        RefreshUiState();
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(_settings);
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        _settings = dialog.ResultSettings.Clone();
        _settingsStore.Save(_settings);
        RegisterHotKey();
        _resultForm.ApplySettings(_settings);
        RefreshUiState();
        StartBackgroundWarmup();
    }

    private void ExitApplication()
    {
        _singleTapTimer.Stop();
        CancelActiveCapture();
        ExitThread();
    }

    private void CancelActiveCapture()
    {
        if (_activeCaptureCts is null)
        {
            return;
        }

        _activeCaptureCts.Cancel();
        _activeCaptureCts.Dispose();
        _activeCaptureCts = null;
    }

    private void RefreshUiState()
    {
        _hotKeyMenuItem.Text = $"Горячая клавиша: {_settings.HotKeyMode.GetDisplayName()}";
        _repeatMenuItem.Enabled = !_lastRegion.IsEmpty && !_settings.Paused && _activeCaptureCts is null;
        _pauseMenuItem.Text = _settings.Paused ? "Возобновить" : "Пауза";
        _notifyIcon.Text = _settings.Paused ? "Ёлочка (пауза)" : "Ёлочка";
        _notifyIcon.Icon = _settings.Paused ? SystemIcons.Warning : SystemIcons.Application;
    }

    private void ShowBalloon(string title, string text)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(2500);
    }

    private void StartBackgroundWarmup()
    {
        if (_ocrTextService is null && _localNllbTranslationProvider is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var warmupTasks = new List<Task>(capacity: 2);

                if (_ocrTextService is not null)
                {
                    warmupTasks.Add(_ocrTextService.WarmUpAsync(cts.Token));
                }

                if (_settings.TranslationEnabled
                    && _settings.TranslationProvider == TranslationProviderKind.LocalNllb
                    && _localNllbTranslationProvider is not null)
                {
                    warmupTasks.Add(_localNllbTranslationProvider.WarmUpAsync(_settings.Clone(), cts.Token));
                }

                if (warmupTasks.Count > 0)
                {
                    await Task.WhenAll(warmupTasks);
                }
            }
            catch
            {
            }
        });
    }
}
