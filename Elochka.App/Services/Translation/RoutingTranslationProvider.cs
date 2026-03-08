using Elochka.App.Models;

namespace Elochka.App.Services.Translation;

internal sealed class RoutingTranslationProvider : ITranslationProvider, IDisposable
{
    private readonly IReadOnlyDictionary<TranslationProviderKind, ITranslationProvider> _providers;

    public RoutingTranslationProvider(IReadOnlyDictionary<TranslationProviderKind, ITranslationProvider> providers)
    {
        _providers = providers;
    }

    public Task<string> TranslateAsync(string sourceText, AppSettings settings, CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(settings.TranslationProvider, out var provider))
        {
            throw new InvalidOperationException($"Unsupported provider: {settings.TranslationProvider}");
        }

        return provider.TranslateAsync(sourceText, settings, cancellationToken);
    }

    public void Dispose()
    {
        foreach (var provider in _providers.Values.Distinct())
        {
            if (provider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
