using Elochka.App.Models;

namespace Elochka.App.Forms;

internal sealed class SettingsForm : Form
{
    private readonly ComboBox _providerComboBox;
    private readonly ComboBox _hotKeyComboBox;
    private readonly TextBox _fontFamilyTextBox;
    private readonly NumericUpDown _fontSizeNumeric;
    private readonly ComboBox _themeComboBox;
    private readonly CheckBox _translationEnabledCheckBox;
    private readonly TextBox _sourceLanguageTextBox;
    private readonly TextBox _targetLanguageTextBox;
    private readonly TextBox _translationEndpointTextBox;
    private readonly TextBox _translationApiKeyTextBox;
    private readonly TextBox _translationFolderIdTextBox;
    private readonly TextBox _offlineModelPathTextBox;
    private readonly TextBox _offlinePythonPathTextBox;
    private readonly ComboBox _yandexCredentialModeComboBox;
    private readonly Label _endpointLabel;
    private readonly Label _apiCredentialLabel;
    private readonly Label _folderIdLabel;
    private readonly Label _yandexCredentialModeLabel;
    private readonly Label _offlineModelPathLabel;
    private readonly Label _offlinePythonPathLabel;

    public SettingsForm(AppSettings settings)
    {
        ResultSettings = settings.Clone();

        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Настройки Ёлочки";
        Width = 680;
        Height = 620;

        _providerComboBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _hotKeyComboBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _fontFamilyTextBox = new TextBox { Dock = DockStyle.Fill };
        _fontSizeNumeric = new NumericUpDown
        {
            DecimalPlaces = 0,
            Dock = DockStyle.Left,
            Maximum = 48,
            Minimum = 8,
            Width = 120,
        };
        _themeComboBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _translationEnabledCheckBox = new CheckBox
        {
            AutoSize = true,
            Text = "Включить перевод",
        };
        _sourceLanguageTextBox = new TextBox { Dock = DockStyle.Fill };
        _targetLanguageTextBox = new TextBox { Dock = DockStyle.Fill };
        _translationEndpointTextBox = new TextBox { Dock = DockStyle.Fill };
        _translationApiKeyTextBox = new TextBox { Dock = DockStyle.Fill };
        _translationFolderIdTextBox = new TextBox { Dock = DockStyle.Fill };
        _offlineModelPathTextBox = new TextBox { Dock = DockStyle.Fill };
        _offlinePythonPathTextBox = new TextBox { Dock = DockStyle.Fill };
        _yandexCredentialModeComboBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };

        _endpointLabel = CreateLabel("Endpoint");
        _apiCredentialLabel = CreateLabel("API key / token");
        _folderIdLabel = CreateLabel("Yandex Folder ID");
        _yandexCredentialModeLabel = CreateLabel("Режим авторизации Yandex");
        _offlineModelPathLabel = CreateLabel("Путь к локальной модели");
        _offlinePythonPathLabel = CreateLabel("Путь к Python");

        PopulateOptions();
        ApplyIncomingSettings(settings);

        _providerComboBox.SelectedIndexChanged += (_, _) => UpdateTranslationFieldState();

        var formLayout = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            RowCount = 14,
        };
        formLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        formLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        for (var row = 0; row < 14; row++)
        {
            formLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        formLayout.Controls.Add(CreateLabel("Кнопка"), 0, 0);
        formLayout.Controls.Add(_hotKeyComboBox, 1, 0);
        formLayout.Controls.Add(CreateLabel("Шрифт"), 0, 1);
        formLayout.Controls.Add(_fontFamilyTextBox, 1, 1);
        formLayout.Controls.Add(CreateLabel("Размер шрифта"), 0, 2);
        formLayout.Controls.Add(_fontSizeNumeric, 1, 2);
        formLayout.Controls.Add(CreateLabel("Цвет"), 0, 3);
        formLayout.Controls.Add(_themeComboBox, 1, 3);
        formLayout.Controls.Add(CreateLabel("Перевод"), 0, 4);
        formLayout.Controls.Add(_translationEnabledCheckBox, 1, 4);
        formLayout.Controls.Add(CreateLabel("Провайдер перевода"), 0, 5);
        formLayout.Controls.Add(_providerComboBox, 1, 5);
        formLayout.Controls.Add(CreateLabel("Язык оригинала"), 0, 6);
        formLayout.Controls.Add(_sourceLanguageTextBox, 1, 6);
        formLayout.Controls.Add(CreateLabel("Язык перевода"), 0, 7);
        formLayout.Controls.Add(_targetLanguageTextBox, 1, 7);
        formLayout.Controls.Add(_endpointLabel, 0, 8);
        formLayout.Controls.Add(_translationEndpointTextBox, 1, 8);
        formLayout.Controls.Add(_apiCredentialLabel, 0, 9);
        formLayout.Controls.Add(_translationApiKeyTextBox, 1, 9);
        formLayout.Controls.Add(_folderIdLabel, 0, 10);
        formLayout.Controls.Add(_translationFolderIdTextBox, 1, 10);
        formLayout.Controls.Add(_yandexCredentialModeLabel, 0, 11);
        formLayout.Controls.Add(_yandexCredentialModeComboBox, 1, 11);
        formLayout.Controls.Add(_offlineModelPathLabel, 0, 12);
        formLayout.Controls.Add(_offlineModelPathTextBox, 1, 12);
        formLayout.Controls.Add(_offlinePythonPathLabel, 0, 13);
        formLayout.Controls.Add(_offlinePythonPathTextBox, 1, 13);

        var buttonsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(14, 0, 14, 14),
        };

        var okButton = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.OK,
            Text = "Сохранить",
        };
        okButton.Click += (_, _) => SaveResult();

        var cancelButton = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Text = "Отмена",
        };

        buttonsPanel.Controls.Add(okButton);
        buttonsPanel.Controls.Add(cancelButton);

        Controls.Add(formLayout);
        Controls.Add(buttonsPanel);

        AcceptButton = okButton;
        CancelButton = cancelButton;
        UpdateTranslationFieldState();
    }

    public AppSettings ResultSettings { get; private set; }

    private void PopulateOptions()
    {
        _providerComboBox.Items.Add(new Option<TranslationProviderKind>("LibreTranslate", TranslationProviderKind.LibreTranslate));
        _providerComboBox.Items.Add(new Option<TranslationProviderKind>("Yandex Cloud", TranslationProviderKind.YandexCloud));
        _providerComboBox.Items.Add(new Option<TranslationProviderKind>("DeepL", TranslationProviderKind.DeepL));
        _providerComboBox.Items.Add(new Option<TranslationProviderKind>("Локальный NLLB-200", TranslationProviderKind.LocalNllb));

        _hotKeyComboBox.Items.Add(new Option<HotKeyMode>("~ / Ё", HotKeyMode.Tilde));
        _hotKeyComboBox.Items.Add(new Option<HotKeyMode>("Alt + ~", HotKeyMode.AltTilde));
        _hotKeyComboBox.Items.Add(new Option<HotKeyMode>("Ctrl + ~", HotKeyMode.ControlTilde));

        _themeComboBox.Items.Add(new Option<ColorTheme>("Светлая", ColorTheme.Light));
        _themeComboBox.Items.Add(new Option<ColorTheme>("Тёмная", ColorTheme.Dark));
        _themeComboBox.Items.Add(new Option<ColorTheme>("Сепия", ColorTheme.Sepia));

        _yandexCredentialModeComboBox.Items.Add(new Option<YandexCredentialMode>("API key", YandexCredentialMode.ApiKey));
        _yandexCredentialModeComboBox.Items.Add(new Option<YandexCredentialMode>("IAM token", YandexCredentialMode.IamToken));
    }

    private void ApplyIncomingSettings(AppSettings settings)
    {
        SelectOption(_providerComboBox, settings.TranslationProvider);
        SelectOption(_hotKeyComboBox, settings.HotKeyMode);
        SelectOption(_themeComboBox, settings.ColorTheme);
        SelectOption(_yandexCredentialModeComboBox, settings.YandexCredentialMode);

        _fontFamilyTextBox.Text = settings.FontFamily;
        _fontSizeNumeric.Value = (decimal)settings.FontSize;
        _translationEnabledCheckBox.Checked = settings.TranslationEnabled;
        _sourceLanguageTextBox.Text = settings.SourceLanguageCode;
        _targetLanguageTextBox.Text = settings.TargetLanguageCode;
        _translationEndpointTextBox.Text = settings.TranslationEndpoint;
        _translationApiKeyTextBox.Text = settings.TranslationApiKey;
        _translationFolderIdTextBox.Text = settings.TranslationFolderId;
        _offlineModelPathTextBox.Text = settings.OfflineModelPath;
        _offlinePythonPathTextBox.Text = settings.OfflinePythonExecutablePath;
    }

    private void SaveResult()
    {
        var provider = ((Option<TranslationProviderKind>)_providerComboBox.SelectedItem!).Value;
        var hotKeyMode = ((Option<HotKeyMode>)_hotKeyComboBox.SelectedItem!).Value;
        var theme = ((Option<ColorTheme>)_themeComboBox.SelectedItem!).Value;
        var yandexCredentialMode = ((Option<YandexCredentialMode>)_yandexCredentialModeComboBox.SelectedItem!).Value;

        ResultSettings = new AppSettings
        {
            HotKeyMode = hotKeyMode,
            FontFamily = string.IsNullOrWhiteSpace(_fontFamilyTextBox.Text) ? "Tahoma" : _fontFamilyTextBox.Text.Trim(),
            FontSize = (float)_fontSizeNumeric.Value,
            ColorTheme = theme,
            TranslationEnabled = _translationEnabledCheckBox.Checked,
            TranslationProvider = provider,
            SourceLanguageCode = _sourceLanguageTextBox.Text.Trim(),
            TargetLanguageCode = _targetLanguageTextBox.Text.Trim(),
            TranslationEndpoint = _translationEndpointTextBox.Text.Trim(),
            TranslationApiKey = _translationApiKeyTextBox.Text.Trim(),
            TranslationFolderId = _translationFolderIdTextBox.Text.Trim(),
            YandexCredentialMode = yandexCredentialMode,
            OfflineModelPath = _offlineModelPathTextBox.Text.Trim(),
            OfflinePythonExecutablePath = _offlinePythonPathTextBox.Text.Trim(),
            Paused = ResultSettings.Paused,
        };

        ResultSettings.Normalize();
    }

    private void UpdateTranslationFieldState()
    {
        var provider = GetSelectedProvider();
        var isYandex = provider == TranslationProviderKind.YandexCloud;
        var isOffline = provider == TranslationProviderKind.LocalNllb;
        var providerName = provider.GetDisplayName();

        _endpointLabel.Text = $"{providerName} endpoint";
        _apiCredentialLabel.Text = provider switch
        {
            TranslationProviderKind.DeepL => "Ключ DeepL",
            TranslationProviderKind.YandexCloud => "Yandex API key / IAM token",
            _ => "API key / token",
        };

        _endpointLabel.Enabled = !isOffline;
        _translationEndpointTextBox.Enabled = !isOffline;
        _apiCredentialLabel.Enabled = !isOffline;
        _translationApiKeyTextBox.Enabled = !isOffline;
        _folderIdLabel.Enabled = isYandex;
        _translationFolderIdTextBox.Enabled = isYandex;
        _yandexCredentialModeLabel.Enabled = isYandex;
        _yandexCredentialModeComboBox.Enabled = isYandex;
        _offlineModelPathLabel.Enabled = isOffline;
        _offlineModelPathTextBox.Enabled = isOffline;
        _offlinePythonPathLabel.Enabled = isOffline;
        _offlinePythonPathTextBox.Enabled = isOffline;
    }

    private TranslationProviderKind GetSelectedProvider()
    {
        if (_providerComboBox.SelectedItem is Option<TranslationProviderKind> option)
        {
            return option.Value;
        }

        return TranslationProviderKind.LibreTranslate;
    }

    private static void SelectOption<T>(ComboBox comboBox, T value)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is Option<T> option && EqualityComparer<T>.Default.Equals(option.Value, value))
            {
                comboBox.SelectedItem = option;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private static Label CreateLabel(string text) =>
        new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 6),
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
        };

    private sealed record Option<T>(string Text, T Value)
    {
        public override string ToString() => Text;
    }
}
