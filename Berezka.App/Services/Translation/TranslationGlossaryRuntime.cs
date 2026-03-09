using System.Text.Json;
using System.Text.RegularExpressions;

namespace Berezka.App.Services.Translation;

internal sealed class TranslationGlossaryRuntime
{
    private static readonly Lazy<TranslationGlossaryRuntime> DefaultLazy =
        new(LoadDefault, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Regex TokenRegex = new(@"[A-Za-z][A-Za-z0-9_'.&/+:-]*", RegexOptions.Compiled);
    private static readonly char[] TrimCharacters = { ' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}' };

    private readonly Dictionary<string, TranslationGlossaryEntry> _directEntries;
    private readonly HashSet<string> _hintEntries;
    private readonly int _maxPhraseWordCount;

    private TranslationGlossaryRuntime(
        Dictionary<string, TranslationGlossaryEntry> directEntries,
        HashSet<string> hintEntries,
        int maxPhraseWordCount)
    {
        _directEntries = directEntries;
        _hintEntries = hintEntries;
        _maxPhraseWordCount = Math.Max(1, maxPhraseWordCount);
    }

    public static TranslationGlossaryRuntime Default => DefaultLazy.Value;

    public IReadOnlyList<TranslationGlossaryMatch> FindDirectMatches(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _directEntries.Count == 0)
        {
            return Array.Empty<TranslationGlossaryMatch>();
        }

        var tokens = Tokenize(text);
        if (tokens.Count == 0)
        {
            return Array.Empty<TranslationGlossaryMatch>();
        }

        var matches = new List<TranslationGlossaryMatch>();
        var index = 0;

        while (index < tokens.Count)
        {
            TranslationGlossaryMatch? bestMatch = null;
            var maxLength = Math.Min(_maxPhraseWordCount, tokens.Count - index);

            for (var length = maxLength; length >= 1; length--)
            {
                var key = BuildKey(tokens, index, length);
                if (!_directEntries.TryGetValue(key, out var entry))
                {
                    continue;
                }

                var startIndex = tokens[index].StartIndex;
                var endIndex = tokens[index + length - 1].EndIndex;
                var originalText = text[startIndex..endIndex];
                var renderedText = entry.Action == TranslationGlossaryAction.Preserve
                    ? originalText
                    : ApplySourceCasing(originalText, entry.Target ?? originalText);

                bestMatch = new TranslationGlossaryMatch(
                    startIndex,
                    endIndex,
                    renderedText,
                    entry.Action,
                    entry.Priority);
                break;
            }

            if (bestMatch is null)
            {
                index++;
                continue;
            }

            matches.Add(bestMatch);
            index = tokens.TakeWhile(token => token.StartIndex < bestMatch.EndIndex).Count();
        }

        return matches;
    }

    public bool ShouldForceTranslate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = NormalizeKey(text);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (_directEntries.TryGetValue(normalized, out var directEntry)
            && directEntry.Action == TranslationGlossaryAction.Translate)
        {
            return true;
        }

        if (_hintEntries.Contains(normalized))
        {
            return true;
        }

        var tokens = Tokenize(text);
        if (tokens.Count == 0)
        {
            return false;
        }

        for (var startIndex = 0; startIndex < tokens.Count; startIndex++)
        {
            var maxLength = Math.Min(_maxPhraseWordCount, tokens.Count - startIndex);
            for (var length = maxLength; length >= 1; length--)
            {
                var key = BuildKey(tokens, startIndex, length);
                if (_hintEntries.Contains(key))
                {
                    return true;
                }

                if (_directEntries.TryGetValue(key, out var entry)
                    && entry.Action == TranslationGlossaryAction.Translate)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool HasPreserveEntry(string text)
    {
        var normalized = NormalizeKey(text);
        return normalized.Length > 0
            && _directEntries.TryGetValue(normalized, out var entry)
            && entry.Action == TranslationGlossaryAction.Preserve;
    }

    private static TranslationGlossaryRuntime LoadDefault()
    {
        var resourcesRoot = Path.Combine(AppContext.BaseDirectory, "Resources", "Glossaries");
        var indexPath = Path.Combine(resourcesRoot, "index.json");
        if (!File.Exists(indexPath))
        {
            return new TranslationGlossaryRuntime(new Dictionary<string, TranslationGlossaryEntry>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), 1);
        }

        var directEntries = new Dictionary<string, TranslationGlossaryEntry>(StringComparer.Ordinal);
        var hintEntries = new HashSet<string>(StringComparer.Ordinal);
        var maxPhraseWordCount = 1;

        var indexDocument = JsonDocument.Parse(File.ReadAllText(indexPath));
        if (!indexDocument.RootElement.TryGetProperty("sources", out var sourcesElement) || sourcesElement.ValueKind != JsonValueKind.Array)
        {
            return new TranslationGlossaryRuntime(directEntries, hintEntries, maxPhraseWordCount);
        }

        foreach (var sourceElement in sourcesElement.EnumerateArray())
        {
            var fileName = sourceElement.GetString();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var glossaryPath = Path.Combine(resourcesRoot, fileName);
            if (!File.Exists(glossaryPath))
            {
                continue;
            }

            using var glossaryDocument = JsonDocument.Parse(File.ReadAllText(glossaryPath));
            if (!glossaryDocument.RootElement.TryGetProperty("entries", out var entriesElement) || entriesElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entryElement in entriesElement.EnumerateArray())
            {
                var source = entryElement.TryGetProperty("source", out var sourceElementValue)
                    ? sourceElementValue.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                var normalizedSource = entryElement.TryGetProperty("normalizedSource", out var normalizedElement)
                    ? normalizedElement.GetString()
                    : NormalizeKey(source);
                if (string.IsNullOrWhiteSpace(normalizedSource))
                {
                    continue;
                }

                var actionText = entryElement.TryGetProperty("action", out var actionElement)
                    ? actionElement.GetString()
                    : "hint";
                var action = actionText?.ToLowerInvariant() switch
                {
                    "preserve" => TranslationGlossaryAction.Preserve,
                    "translate" => TranslationGlossaryAction.Translate,
                    _ => TranslationGlossaryAction.Hint,
                };

                var priority = entryElement.TryGetProperty("priority", out var priorityElement) && priorityElement.TryGetInt32(out var parsedPriority)
                    ? parsedPriority
                    : 0;
                var target = entryElement.TryGetProperty("target", out var targetElement)
                    ? targetElement.GetString()
                    : null;

                maxPhraseWordCount = Math.Max(maxPhraseWordCount, normalizedSource.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

                if (action == TranslationGlossaryAction.Hint)
                {
                    hintEntries.Add(normalizedSource);
                    continue;
                }

                var entry = new TranslationGlossaryEntry(normalizedSource, target, action, priority);
                if (directEntries.TryGetValue(normalizedSource, out var existing) && existing.Priority > priority)
                {
                    continue;
                }

                directEntries[normalizedSource] = entry;
            }
        }

        return new TranslationGlossaryRuntime(directEntries, hintEntries, maxPhraseWordCount);
    }

    private static List<TokenSpan> Tokenize(string text)
    {
        var tokens = new List<TokenSpan>();
        foreach (Match match in TokenRegex.Matches(text))
        {
            if (!match.Success || match.Length == 0)
            {
                continue;
            }

            var normalized = NormalizeKey(match.Value);
            if (normalized.Length == 0)
            {
                continue;
            }

            tokens.Add(new TokenSpan(match.Index, match.Index + match.Length, normalized));
        }

        return tokens;
    }

    private static string BuildKey(IReadOnlyList<TokenSpan> tokens, int startIndex, int length)
    {
        return string.Join(
            ' ',
            tokens
                .Skip(startIndex)
                .Take(length)
                .Select(static token => token.Normalized));
    }

    private static string NormalizeKey(string value)
    {
        var trimmed = value.Trim(TrimCharacters);
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var tokens = TokenRegex.Matches(trimmed)
            .Select(static match => match.Value.ToLowerInvariant())
            .Where(static token => token.Length > 0)
            .ToArray();
        if (tokens.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(' ', tokens).Trim();
    }

    private static string ApplySourceCasing(string originalText, string replacement)
    {
        if (replacement.Length == 0)
        {
            return replacement;
        }

        var originalLetters = originalText.Where(char.IsLetter).ToArray();
        if (originalLetters.Length == 0)
        {
            return replacement;
        }

        if (originalLetters.All(char.IsUpper))
        {
            return replacement.All(static character => !char.IsLetter(character) || character is >= 'A' and <= 'Z')
                ? replacement.ToUpperInvariant()
                : char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }

        if (char.IsUpper(originalLetters[0]))
        {
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }

        return replacement;
    }

    private readonly record struct TokenSpan(int StartIndex, int EndIndex, string Normalized);
}

internal enum TranslationGlossaryAction
{
    Hint,
    Translate,
    Preserve,
}

internal sealed record TranslationGlossaryEntry(
    string NormalizedSource,
    string? Target,
    TranslationGlossaryAction Action,
    int Priority);

internal sealed record TranslationGlossaryMatch(
    int StartIndex,
    int EndIndex,
    string RenderedText,
    TranslationGlossaryAction Action,
    int Priority)
{
    public int Length => EndIndex - StartIndex;
}
