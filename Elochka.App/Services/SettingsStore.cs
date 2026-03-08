using System.Globalization;
using System.Text;
using Elochka.App.Models;

namespace Elochka.App.Services;

internal sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public AppSettings Load()
    {
        var settings = new AppSettings();

        if (!File.Exists(_path))
        {
            return settings;
        }

        var currentSection = string.Empty;

        foreach (var rawLine in File.ReadAllLines(_path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1];
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            switch (currentSection)
            {
                case "General":
                    ReadGeneralValue(settings, key, value);
                    break;
                case "Translation":
                    ReadTranslationValue(settings, key, value);
                    break;
            }
        }

        settings.Normalize();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();

        var builder = new StringBuilder();
        builder.AppendLine("[General]");
        builder.AppendLine($"HotKey={(int)settings.HotKeyMode}");
        builder.AppendLine($"Font={settings.FontFamily}; {settings.FontSize.ToString("0.#", CultureInfo.InvariantCulture)}pt");
        builder.AppendLine($"Color={(int)settings.ColorTheme}");
        builder.AppendLine($"Paused={(settings.Paused ? 1 : 0)}");
        builder.AppendLine();
        builder.AppendLine("[Translation]");
        builder.AppendLine($"Enabled={(settings.TranslationEnabled ? 1 : 0)}");
        builder.AppendLine($"Provider={(int)settings.TranslationProvider}");
        builder.AppendLine($"SourceLanguage={settings.SourceLanguageCode}");
        builder.AppendLine($"TargetLanguage={settings.TargetLanguageCode}");
        builder.AppendLine($"Endpoint={settings.TranslationEndpoint}");
        builder.AppendLine($"ApiKey={settings.TranslationApiKey}");
        builder.AppendLine($"FolderId={settings.TranslationFolderId}");
        builder.AppendLine($"YandexCredentialMode={(int)settings.YandexCredentialMode}");
        builder.AppendLine($"OfflineModelPath={settings.OfflineModelPath}");
        builder.AppendLine($"OfflinePythonPath={settings.OfflinePythonExecutablePath}");

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void ReadGeneralValue(AppSettings settings, string key, string value)
    {
        switch (key)
        {
            case "HotKey":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hotKeyValue)
                    && Enum.IsDefined(typeof(HotKeyMode), hotKeyValue))
                {
                    settings.HotKeyMode = (HotKeyMode)hotKeyValue;
                }

                break;
            case "Font":
                if (TryParseFont(value, out var fontFamily, out var fontSize))
                {
                    settings.FontFamily = fontFamily;
                    settings.FontSize = fontSize;
                }

                break;
            case "Color":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var colorValue)
                    && Enum.IsDefined(typeof(ColorTheme), colorValue))
                {
                    settings.ColorTheme = (ColorTheme)colorValue;
                }

                break;
            case "Paused":
                settings.Paused = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                break;
        }
    }

    private static void ReadTranslationValue(AppSettings settings, string key, string value)
    {
        switch (key)
        {
            case "Enabled":
                settings.TranslationEnabled = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                break;
            case "Provider":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var providerValue)
                    && Enum.IsDefined(typeof(TranslationProviderKind), providerValue))
                {
                    settings.TranslationProvider = (TranslationProviderKind)providerValue;
                }

                break;
            case "SourceLanguage":
                settings.SourceLanguageCode = value;
                break;
            case "TargetLanguage":
                settings.TargetLanguageCode = value;
                break;
            case "Endpoint":
                settings.TranslationEndpoint = value;
                break;
            case "ApiKey":
                settings.TranslationApiKey = value;
                break;
            case "FolderId":
                settings.TranslationFolderId = value;
                break;
            case "YandexCredentialMode":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var credentialModeValue)
                    && Enum.IsDefined(typeof(YandexCredentialMode), credentialModeValue))
                {
                    settings.YandexCredentialMode = (YandexCredentialMode)credentialModeValue;
                }

                break;
            case "OfflineModelPath":
                settings.OfflineModelPath = value;
                break;
            case "OfflinePythonPath":
                settings.OfflinePythonExecutablePath = value;
                break;
        }
    }

    private static bool TryParseFont(string value, out string family, out float size)
    {
        family = "Tahoma";
        size = 12f;

        var parts = value.Split(';', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        family = parts[0];
        var normalizedSize = parts[1].Replace("pt", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return float.TryParse(normalizedSize, NumberStyles.Float, CultureInfo.InvariantCulture, out size);
    }
}
