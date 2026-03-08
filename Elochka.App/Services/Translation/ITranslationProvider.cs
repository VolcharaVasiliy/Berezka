using Elochka.App.Models;

namespace Elochka.App.Services.Translation;

internal interface ITranslationProvider
{
    Task<string> TranslateAsync(string sourceText, AppSettings settings, CancellationToken cancellationToken);
}
