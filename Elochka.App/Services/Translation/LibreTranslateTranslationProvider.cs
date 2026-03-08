using System.Net.Http.Json;
using System.Text.Json;
using Elochka.App.Models;

namespace Elochka.App.Services.Translation;

internal sealed class LibreTranslateTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;

    public LibreTranslateTranslationProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> TranslateAsync(string sourceText, AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return string.Empty;
        }

        var payload = new Dictionary<string, string?>
        {
            ["q"] = sourceText,
            ["source"] = string.IsNullOrWhiteSpace(settings.SourceLanguageCode) ? "auto" : settings.SourceLanguageCode,
            ["target"] = settings.TargetLanguageCode,
            ["format"] = "text",
        };

        if (!string.IsNullOrWhiteSpace(settings.TranslationApiKey))
        {
            payload["api_key"] = settings.TranslationApiKey;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            settings.GetEffectiveTranslationEndpoint(),
            payload,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (document.RootElement.TryGetProperty("translatedText", out var translatedTextProperty))
        {
            return translatedTextProperty.GetString()?.Trim() ?? string.Empty;
        }

        throw new InvalidOperationException("LibreTranslate response does not contain translatedText.");
    }
}
