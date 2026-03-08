using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Elochka.App.Models;

namespace Elochka.App.Services.Translation;

internal sealed class YandexCloudTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;

    public YandexCloudTranslationProvider(HttpClient httpClient)
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
            throw new InvalidOperationException("Yandex Cloud requires an API key or IAM token.");
        }

        if (settings.YandexCredentialMode == YandexCredentialMode.ApiKey
            && string.IsNullOrWhiteSpace(settings.TranslationFolderId))
        {
            throw new InvalidOperationException("Yandex Cloud API key mode requires Folder ID.");
        }

        var payload = new Dictionary<string, object?>
        {
            ["targetLanguageCode"] = settings.TargetLanguageCode,
            ["texts"] = new[] { sourceText },
        };

        if (!string.IsNullOrWhiteSpace(settings.SourceLanguageCode))
        {
            payload["sourceLanguageCode"] = settings.SourceLanguageCode;
        }

        if (!string.IsNullOrWhiteSpace(settings.TranslationFolderId))
        {
            payload["folderId"] = settings.TranslationFolderId;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.GetEffectiveTranslationEndpoint())
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = settings.YandexCredentialMode switch
        {
            YandexCredentialMode.IamToken => new AuthenticationHeaderValue("Bearer", settings.TranslationApiKey),
            _ => new AuthenticationHeaderValue("Api-Key", settings.TranslationApiKey),
        };

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

        throw new InvalidOperationException("Yandex Cloud response does not contain translations[0].text.");
    }
}
