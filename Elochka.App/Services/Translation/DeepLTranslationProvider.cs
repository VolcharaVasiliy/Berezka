using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Elochka.App.Models;

namespace Elochka.App.Services.Translation;

internal sealed class DeepLTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;

    public DeepLTranslationProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> TranslateAsync(string sourceText, AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(settings.TranslationApiKey))
        {
            throw new InvalidOperationException("DeepL requires an authentication key.");
        }

        var payload = new Dictionary<string, object?>
        {
            ["text"] = new[] { sourceText },
            ["target_lang"] = settings.TargetLanguageCode.ToUpperInvariant(),
        };

        if (!string.IsNullOrWhiteSpace(settings.SourceLanguageCode))
        {
            payload["source_lang"] = settings.SourceLanguageCode.ToUpperInvariant();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.GetEffectiveTranslationEndpoint())
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", settings.TranslationApiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (document.RootElement.TryGetProperty("translations", out var translationsElement)
            && translationsElement.ValueKind == JsonValueKind.Array
            && translationsElement.GetArrayLength() > 0
            && translationsElement[0].TryGetProperty("text", out var textElement))
        {
            return textElement.GetString()?.Trim() ?? string.Empty;
        }

        throw new InvalidOperationException("DeepL response does not contain translations[0].text.");
    }
}
