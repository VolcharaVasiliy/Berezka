using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Elochka.App.Models;

namespace Elochka.App.Services.Translation;

internal sealed class LocalNllbTranslationProvider : ITranslationProvider, IDisposable
{
    private const int MaxAttempts = 2;
    private const int MaxBatchSegments = 8;
    private const int MaxBatchCharacters = 720;
    private const int MaxSegmentLength = 220;
    private const int WorkerThreadCount = 2;
    private const int WorkerBeamSize = 1;
    private const string ModelFolderName = "nllb-200-distilled-600m-ctranslate2";
    private const string SetupScriptPath = @"F:\Projects\elochka\scripts\setup_local_nllb.ps1";
    private const string DefaultPythonPath = @"F:\DevTools\Python311\python.exe";
    private const string PythonEnvVar = "ELOCHKA_PYTHON";
    private const string ModelEnvVar = "ELOCHKA_OFFLINE_MODEL";
    private static readonly TimeSpan WorkerIdleLifetime = TimeSpan.FromMinutes(10);
    private static readonly string DebugLogPath = Path.Combine(AppContext.BaseDirectory, "translation-debug.log");
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex SentenceBoundaryRegex = new(@"(?<=[\.\!\?\;\:])\s+", RegexOptions.Compiled);
    private static readonly Regex EnglishSpanRegex = new(@"[A-Za-z][A-Za-z0-9_'.&/+:-]*(?:\s+[A-Za-z][A-Za-z0-9_'.&/+:-]*)*", RegexOptions.Compiled);
    private static readonly Regex EnglishWordRegex = new(@"[A-Za-z][A-Za-z0-9_'.-]*", RegexOptions.Compiled);
    private static readonly Regex PunctuationSpacingRegex = new(@"\s+([,.;:!?])", RegexOptions.Compiled);
    private static readonly Regex OpenBracketSpacingRegex = new(@"\(\s+", RegexOptions.Compiled);
    private static readonly Regex CloseBracketSpacingRegex = new(@"\s+\)", RegexOptions.Compiled);
    private static readonly Regex MultiWhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex AllCapsWordRegex = new(@"\b[A-Z]{4,}\b", RegexOptions.Compiled);
    private static readonly Regex StandaloneOcrIPattern = new(@"(?<=^|[\s\(\[""'])\|(?=$|[\s\)\],\.\!\?:;""'])", RegexOptions.Compiled);
    private static readonly Regex VocalAndVoiceRegex = new(@"\bвокал\s+и\s+голос\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RangeAndVoiceRegex = new(@"\bдиапазон\s+голос(?:а|ов)\s+и\s+голос\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SingingPhraseRegex = new(@"\bпо(е|ё)т(?:\s+\w+){0,4}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SingingParticipleRegex = new(@"\bпоющ(?:ий|его|ему|им|ая|ую|ей|ие|их|ими)(?:\s+\w+){0,4}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SingingNounRegex = new(@"\bпени(?:е|я|ю|ем|и)(?:\s+\w+){0,4}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SenseOfJoyPhraseRegex = new(@"(?i)\bsense\s+of\s+joy\s+and\s+pride\b", RegexOptions.Compiled);
    private static readonly Regex CupOfTeaPhraseRegex = new(@"(?i)\bcup\s+of\s+tea\b", RegexOptions.Compiled);
    private static readonly Regex GlimmerPhraseRegex = new(@"(?i)\bglimmer\s+in\s+their\s+eyes\b", RegexOptions.Compiled);
    private static readonly Regex BondWithThemPhraseRegex = new(@"(?i)\bbond\s+with\s+them\b", RegexOptions.Compiled);
    private static readonly Regex MusicTranscendsPhraseRegex = new(@"(?i)\bmusic\s+transcends\b", RegexOptions.Compiled);
    private static readonly Regex ThisIsGloriousPhraseRegex = new(@"(?i)\bthis\s+is\s+glorious\b", RegexOptions.Compiled);
    private static readonly Regex DutchMetalheadHerePhraseRegex = new(@"(?i)\bdutch\s+metalhead\s+here\b", RegexOptions.Compiled);
    private static readonly Regex IndianColleaguesPhraseRegex = new(@"(?i)\bindian\s+colleagues\b", RegexOptions.Compiled);
    private static readonly Regex NotMetalheadsPhraseRegex = new(@"(?i)\(not\s+metalheads?\)", RegexOptions.Compiled);
    private static readonly Regex FeaturingRegex = new(@"(?i)\b(?:ft|feat|featuring)\.?\b", RegexOptions.Compiled);
    private static readonly Regex CollaborationRegex = new(@"(?i)\bcollab\b", RegexOptions.Compiled);
    private static readonly Regex WithSlashRegex = new(@"(?i)\bw/\b", RegexOptions.Compiled);
    private static readonly Regex MetadataKeywordRegex = new(@"(?i)\b(?:ft|feat|featuring|prod|remix|cover|ost|op|ed|amv|mv|pv|ver|version|lyrics?)\.?\b", RegexOptions.Compiled);
    private static readonly Regex UiActionLineRegex = new(@"^\s*(?:\u041F\u0435\u0440\u0435\u0432\u0435\u0441\u0442\u0438\s+\u043D\u0430\s+\u0440\u0443\u0441\u0441\u043A\u0438\u0439|\u041E\u0442\u0432\u0435\u0442\u0438\u0442\u044C|\u0438\u0437\u043C\u0435\u043D\u0435\u043D\u043E|Translate\s+to\s+Russian|Reply|edited)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EngagementNoiseLineRegex = new(@"^\s*[@#©®&%\d\s\-\—\.,;:()\[\]\{\}/|<>!?]+\s*(?:\u041E\u0442\u0432\u0435\u0442\u0438\u0442\u044C|Reply)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MetalheadSingularRegex = new(@"(?i)\bmetalhead\b", RegexOptions.Compiled);
    private static readonly Regex MetalheadPluralRegex = new(@"(?i)\bmetalheads\b", RegexOptions.Compiled);
    private static readonly Regex ContractionlessWordRegex = new(@"(?i)\b(im|ive|ill|id|dont|cant|wont|didnt|doesnt|isnt|arent|wasnt|werent|shouldnt|couldnt|wouldnt|thats|theres|theyre|youre|weve|theyve|youve|hes|shes|lets|itll|theyll|we'll|i'm|i've)\b", RegexOptions.Compiled);
    private static readonly HashSet<string> EnglishStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a",
        "an",
        "and",
        "are",
        "as",
        "at",
        "be",
        "been",
        "being",
        "but",
        "by",
        "for",
        "from",
        "has",
        "have",
        "he",
        "her",
        "here",
        "him",
        "his",
        "i",
        "if",
        "in",
        "into",
        "is",
        "it",
        "its",
        "me",
        "my",
        "of",
        "on",
        "or",
        "our",
        "she",
        "so",
        "that",
        "the",
        "their",
        "them",
        "there",
        "they",
        "this",
        "to",
        "us",
        "was",
        "we",
        "were",
        "what",
        "when",
        "where",
        "while",
        "who",
        "with",
        "you",
        "your",
    };
    private static readonly HashSet<string> UppercaseAcronymAllowList = new(StringComparer.Ordinal)
    {
        "AI",
        "API",
        "CPU",
        "DIY",
        "GPU",
        "HP",
        "MMO",
        "MS",
        "NPC",
        "RPG",
        "URL",
        "USB",
        "UI",
        "UX",
    };
    private static readonly HashSet<string> ForceTranslateShortWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "action",
        "actions",
        "album",
        "albums",
        "code",
        "comment",
        "comments",
        "insight",
        "insights",
        "issue",
        "issues",
        "live",
        "lyrics",
        "music",
        "official",
        "project",
        "projects",
        "request",
        "requests",
        "security",
        "song",
        "songs",
        "track",
        "tracks",
        "video",
    };
    private static readonly LanguageHint[] LanguageHints =
    {
        new("Hindi", "хинди"),
        new("English", "английском"),
        new("Japanese", "японском"),
        new("Korean", "корейском"),
        new("Chinese", "китайском"),
        new("Spanish", "испанском"),
        new("German", "немецком"),
        new("French", "французском"),
        new("Italian", "итальянском"),
        new("Arabic", "арабском"),
        new("Portuguese", "португальском"),
        new("Polish", "польском"),
        new("Russian", "русском"),
        new("Ukrainian", "украинском"),
    };

    private readonly SemaphoreSlim _workerGate = new(1, 1);
    private readonly object _stderrSync = new();

    private Process? _workerProcess;
    private StreamWriter? _workerInput;
    private StreamReader? _workerOutput;
    private System.Threading.Timer? _idleTimer;
    private readonly StringBuilder _stderrBuffer = new();
    private DateTime _lastWorkerUseUtc = DateTime.MinValue;
    private bool _disposed;

    public async Task<string> TranslateAsync(string sourceText, AppSettings settings, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return string.Empty;
        }

        var plans = BuildLinePlans(sourceText, settings);
        if (plans.Count == 0)
        {
            return string.Empty;
        }

        var translatableSegments = plans
            .SelectMany(static plan => plan.Pieces.Where(static piece => !piece.PreserveOriginal))
            .Select(static piece => piece.TranslationSourceText!)
            .ToArray();

        if (translatableSegments.Length == 0)
        {
            var preserved = string.Join(Environment.NewLine, plans.Select(static plan => plan.OriginalLine));
            AppendDebugLog(
                $"SKIP provider={settings.TranslationProvider} src={settings.SourceLanguageCode}->{settings.TargetLanguageCode} reason=no-translatable-segments{Environment.NewLine}" +
                $"SOURCE: {sourceText}{Environment.NewLine}" +
                $"TRANSLATION: {preserved}{Environment.NewLine}");
            return preserved;
        }

        var pythonExecutable = ResolvePythonExecutable(settings);
        var scriptPath = ResolveScriptPath();
        var modelPath = ResolveModelPath(settings);
        Exception? lastException = null;

        await _workerGate.WaitAsync(cancellationToken);
        try
        {
            CancelIdleShutdown_NoLock();

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    var translatedSegments = await TranslateSegmentsWithWorkerAsync(
                        translatableSegments,
                        settings,
                        cancellationToken,
                        pythonExecutable,
                        scriptPath,
                        modelPath);

                    var translation = RebuildTranslation(plans, translatedSegments, settings);
                    AppendDebugLog(
                        $"SUCCESS attempt={attempt} provider={settings.TranslationProvider} src={settings.SourceLanguageCode}->{settings.TargetLanguageCode} segments={translatableSegments.Length}{Environment.NewLine}" +
                        $"SOURCE: {sourceText}{Environment.NewLine}" +
                        $"TRANSLATION: {translation}{Environment.NewLine}");

                    return translation;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lastException = exception;
                    AppendDebugLog(
                        $"FAIL attempt={attempt} provider={settings.TranslationProvider} src={settings.SourceLanguageCode}->{settings.TargetLanguageCode} segments={translatableSegments.Length}{Environment.NewLine}" +
                        $"SOURCE: {sourceText}{Environment.NewLine}" +
                        $"ERROR: {exception}{Environment.NewLine}");
                    DisposeWorker_NoLock();
                }
            }

            throw lastException ?? new InvalidOperationException("Offline translator failed without exception details.");
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

    public async Task WarmUpAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var pythonExecutable = ResolvePythonExecutable(settings);
        var scriptPath = ResolveScriptPath();
        var modelPath = ResolveModelPath(settings);

        await _workerGate.WaitAsync(cancellationToken);
        try
        {
            CancelIdleShutdown_NoLock();
            EnsureWorkerStarted_NoLock(settings, pythonExecutable, scriptPath, modelPath);
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

    private async Task<string[]> TranslateSegmentsWithWorkerAsync(
        IReadOnlyList<string> segments,
        AppSettings settings,
        CancellationToken cancellationToken,
        string pythonExecutable,
        string scriptPath,
        string modelPath)
    {
        var translations = new List<string>(segments.Count);

        foreach (var batch in BuildBatches(segments))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchTranslations = await ExecuteTranslationBatchAsync(
                batch,
                settings,
                cancellationToken,
                pythonExecutable,
                scriptPath,
                modelPath);

            translations.AddRange(batchTranslations);
        }

        return translations.ToArray();
    }

    private async Task<string[]> ExecuteTranslationBatchAsync(
        IReadOnlyList<string> sourceTexts,
        AppSettings settings,
        CancellationToken cancellationToken,
        string pythonExecutable,
        string scriptPath,
        string modelPath)
    {
        EnsureWorkerStarted_NoLock(settings, pythonExecutable, scriptPath, modelPath);

        using var registration = cancellationToken.Register(KillWorkerForCancellation);

        var payload = JsonSerializer.Serialize(
            new OfflineTranslationRequest(sourceTexts, settings.SourceLanguageCode, settings.TargetLanguageCode));

        if (_workerInput is null || _workerOutput is null)
        {
            throw new InvalidOperationException("Offline translator worker streams are unavailable.");
        }

        await _workerInput.WriteAsync(payload.AsMemory(), cancellationToken);
        await _workerInput.WriteAsync(Environment.NewLine.AsMemory(), cancellationToken);
        await _workerInput.FlushAsync();

        var responseLine = await _workerOutput.ReadLineAsync().WaitAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new InvalidOperationException(BuildWorkerFailureMessage("Offline translator worker returned an empty response."));
        }

        var response = JsonSerializer.Deserialize<OfflineTranslationResponse>(NormalizeJsonResponse(responseLine));
        if (response is null)
        {
            throw new InvalidOperationException(BuildWorkerFailureMessage("Offline translator response could not be parsed."));
        }

        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            throw new InvalidOperationException(BuildWorkerFailureMessage(response.Error.Trim()));
        }

        var translations = response.Translations;
        if ((translations is null || translations.Length == 0) && !string.IsNullOrWhiteSpace(response.Translation))
        {
            translations = new[] { response.Translation.Trim() };
        }

        if (translations is null || translations.Length != sourceTexts.Count)
        {
            throw new InvalidOperationException(
                BuildWorkerFailureMessage(
                    $"Offline translator returned {translations?.Length ?? 0} items for {sourceTexts.Count} source segments."));
        }

        return translations
            .Select((translation, index) => string.IsNullOrWhiteSpace(translation) ? sourceTexts[index] : translation.Trim())
            .ToArray();
    }

    private void EnsureWorkerStarted_NoLock(
        AppSettings settings,
        string pythonExecutable,
        string scriptPath,
        string modelPath)
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
            Arguments = BuildArguments(scriptPath, modelPath, settings),
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["OMP_NUM_THREADS"] = WorkerThreadCount.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["MKL_NUM_THREADS"] = WorkerThreadCount.ToString(CultureInfo.InvariantCulture);

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        process.ErrorDataReceived += OnWorkerErrorDataReceived;

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start the offline translation worker.");
        }

        TryLowerProcessPriority(process);
        process.BeginErrorReadLine();

        _workerProcess = process;
        _workerInput = process.StandardInput;
        _workerOutput = process.StandardOutput;
    }

    private static string RebuildTranslation(
        IReadOnlyList<LineTranslationPlan> plans,
        IReadOnlyList<string> translatedSegments,
        AppSettings settings)
    {
        var translatedLines = new List<string>(plans.Count);
        var segmentIndex = 0;

        foreach (var plan in plans)
        {
            if (plan.PreserveOriginal)
            {
                translatedLines.Add(plan.RenderPreserved());
                continue;
            }

            var parts = new List<string>(plan.Pieces.Count);
            foreach (var piece in plan.Pieces)
            {
                if (piece.PreserveOriginal)
                {
                    parts.Add(piece.OutputText);
                    continue;
                }

                var translatedPart = translatedSegments[segmentIndex++];
                parts.Add(string.IsNullOrWhiteSpace(translatedPart) ? piece.OutputText : translatedPart);
            }

            var joined = JoinTranslatedSegments(parts);
            joined = PostProcessTranslation(plan.TranslationSourceLine ?? plan.OriginalLine, joined, settings);
            translatedLines.Add(string.IsNullOrWhiteSpace(joined) ? plan.RenderPreserved() : joined);
        }

        return string.Join(Environment.NewLine, translatedLines);
    }

    private static List<LineTranslationPlan> BuildLinePlans(string sourceText, AppSettings settings)
    {
        var normalizedText = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalizedText.Split('\n');
        var plans = new List<LineTranslationPlan>(lines.Length);

        foreach (var rawLine in lines)
        {
            var line = NormalizeOcrLine(rawLine);
            if (ShouldDiscardNoiseLine(line))
            {
                continue;
            }

            plans.Add(BuildPlanForLine(line, settings));
        }

        return MergeContinuationPlans(plans, settings);
    }

    private static string NormalizeOcrLine(string line)
    {
        var normalized = line
            .Replace('\t', ' ')
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('’', '\'')
            .Trim();

        normalized = normalized.TrimStart('•', '·', '▪', '◦', '»', '+', '*');
        normalized = MultiWhitespaceRegex.Replace(normalized, " ");
        return normalized.Trim();
    }

    private static LineTranslationPlan BuildPlanForLine(string rawLine, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return LineTranslationPlan.Preserve(string.Empty);
        }

        var line = rawLine;
        if (string.IsNullOrWhiteSpace(line))
        {
            return LineTranslationPlan.Preserve(string.Empty);
        }

        if (!ContainsLatin(line))
        {
            return LineTranslationPlan.Preserve(line);
        }

        if (ShouldPreserveLine(line, settings) || LooksLikeMetadataLine(line))
        {
            var preservedLine = NormalizePreservedMetadataLine(line);
            return LineTranslationPlan.Preserve(preservedLine);
        }

        var pieces = BuildPiecesForLine(line, settings);
        if (pieces.Count == 0 || pieces.All(static piece => piece.PreserveOriginal))
        {
            return LineTranslationPlan.Preserve(string.Concat(pieces.Select(static piece => piece.OutputText)));
        }

        var translationSourceLine = string.Concat(
            pieces.Select(static piece => piece.TranslationSourceText ?? piece.OutputText));

        return LineTranslationPlan.Translate(line, translationSourceLine, pieces);
    }

    private static List<LineTranslationPiece> BuildPiecesForLine(string line, AppSettings settings)
    {
        var pieces = new List<LineTranslationPiece>();
        var lastIndex = 0;

        foreach (Match match in EnglishSpanRegex.Matches(line))
        {
            if (!match.Success || match.Length == 0)
            {
                continue;
            }

            if (match.Index > lastIndex)
            {
                pieces.Add(LineTranslationPiece.Preserve(line[lastIndex..match.Index]));
            }

            var span = match.Value;
            if (ShouldPreserveEnglishSpan(span, line, match.Index, settings))
            {
                pieces.Add(LineTranslationPiece.Preserve(NormalizePreservedMetadataLine(span)));
            }
            else
            {
                pieces.Add(LineTranslationPiece.Translate(span, NormalizeSourceForTranslation(span, settings)));
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < line.Length)
        {
            pieces.Add(LineTranslationPiece.Preserve(line[lastIndex..]));
        }

        return MergeAdjacentPieces(pieces);
    }

    private static List<LineTranslationPiece> MergeAdjacentPieces(IReadOnlyList<LineTranslationPiece> pieces)
    {
        if (pieces.Count <= 1)
        {
            return pieces.ToList();
        }

        var merged = new List<LineTranslationPiece>();
        foreach (var piece in pieces)
        {
            if (piece.OutputText.Length == 0 && piece.PreserveOriginal)
            {
                continue;
            }

            if (merged.Count == 0)
            {
                merged.Add(piece);
                continue;
            }

            var previous = merged[^1];
            if (previous.PreserveOriginal != piece.PreserveOriginal)
            {
                merged.Add(piece);
                continue;
            }

            merged[^1] = previous.PreserveOriginal
                ? LineTranslationPiece.Preserve(previous.OutputText + piece.OutputText)
                : LineTranslationPiece.Translate(
                    previous.OutputText + piece.OutputText,
                    (previous.TranslationSourceText ?? string.Empty) + (piece.TranslationSourceText ?? string.Empty));
        }

        return merged;
    }

    private static IReadOnlyList<string> SplitForTranslation(string line)
    {
        if (line.Length <= MaxSegmentLength)
        {
            return new[] { line };
        }

        var result = new List<string>();
        var sentenceSegments = SentenceBoundaryRegex
            .Split(line)
            .Where(static segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();

        if (sentenceSegments.Length == 0)
        {
            sentenceSegments = new[] { line };
        }

        foreach (var sentenceSegment in sentenceSegments)
        {
            SplitSegmentRecursively(sentenceSegment.Trim(), result);
        }

        return result;
    }

    private static string NormalizeSourceForTranslation(string line, AppSettings settings)
    {
        if (!settings.SourceLanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase)
            || !settings.TargetLanguageCode.Equals("ru", StringComparison.OrdinalIgnoreCase))
        {
            return line;
        }

        var normalized = line;
        normalized = StandaloneOcrIPattern.Replace(normalized, "I");
        normalized = ContractionlessWordRegex.Replace(normalized, static match => ExpandContraction(match.Value));
        normalized = FeaturingRegex.Replace(normalized, "featuring");
        normalized = CollaborationRegex.Replace(normalized, "collaboration");
        normalized = WithSlashRegex.Replace(normalized, "with");
        normalized = Regex.Replace(normalized, @"[.·•]{5,}", ". ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\boff\s+icial\b", "official", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bmuhiple\b", "multiple", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bonw\b", "own", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bseding\b", "sending", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\btums\s+out\b", "turns out", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\b(\d{1,2})\s+yo\b", "$1 year old", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\by[’'‘]?all\b", "all of you", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bunderrated\s+af\b", "seriously underrated", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bmind\s+blown(?:\s+to\s+oblivion)?\b", "completely amazed", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\brock\s+on\b", "keep rocking", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\braw\s+intensity\b", "raw power", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bgot\s+the\s+best\s+of\s+me\b", "is overwhelming me", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bcaught\s+me\s+off\s+guard\b", "surprised me", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bthe\s+sudden\s+beat\s+at\b", "the sudden beat drop at", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bit(?:'s| is)\s+really\s+fucking\s+good\b", "it is extremely good", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bfucking\s+good\b", "extremely good", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bwhat\s+the\s+fuck\b", "wow", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bspeechless\b", "with no words", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bthis\s+shit\s+goes\s+hard\b", "this sounds incredibly powerful", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bgo\s+that\s+hard\s+(?:on|with)\b", "play with such intensity on", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bstand\s+up\s+for\s+that\b", "show respect for that", RegexOptions.CultureInvariant);

        normalized = DutchMetalheadHerePhraseRegex.Replace(normalized, "I am a metal fan from the Netherlands");
        normalized = ThisIsGloriousPhraseRegex.Replace(normalized, "This is amazing");
        normalized = IndianColleaguesPhraseRegex.Replace(normalized, "colleagues from India");
        normalized = CupOfTeaPhraseRegex.Replace(normalized, "something they usually enjoy");
        normalized = NotMetalheadsPhraseRegex.Replace(normalized, "(they are not fans of metal music)");
        normalized = GlimmerPhraseRegex.Replace(normalized, "spark in their eyes");
        normalized = SenseOfJoyPhraseRegex.Replace(normalized, "joy and pride");
        normalized = BondWithThemPhraseRegex.Replace(normalized, "feel close to them");
        normalized = MusicTranscendsPhraseRegex.Replace(normalized, "Music transcends boundaries");
        normalized = MetalheadPluralRegex.Replace(normalized, "fans of metal music");
        normalized = MetalheadSingularRegex.Replace(normalized, "metal fan");

        foreach (var hint in LanguageHints)
        {
            normalized = Regex.Replace(
                normalized,
                $@"\b{Regex.Escape(hint.SourceName)}\b",
                hint.SourceName,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        normalized = AllCapsWordRegex.Replace(
            normalized,
            match => UppercaseAcronymAllowList.Contains(match.Value)
                ? match.Value
                : match.Value.ToLowerInvariant());

        normalized = MultiWhitespaceRegex.Replace(normalized, " ");
        return normalized.Trim();
    }

    private static string NormalizePreservedMetadataLine(string line)
    {
        var normalized = FeaturingRegex.Replace(line, "feat.");
        normalized = Regex.Replace(normalized, @"(?i)\bfeat\.(?:\.)+", "feat.");
        normalized = MultiWhitespaceRegex.Replace(normalized, " ");
        return normalized.Trim();
    }

    private static bool ShouldDiscardNoiseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        if (UiActionLineRegex.IsMatch(line) || EngagementNoiseLineRegex.IsMatch(line))
        {
            return true;
        }

        if ((line.Contains("@", StringComparison.Ordinal) || line.Contains('#', StringComparison.Ordinal))
            && (line.Contains("\u043D\u0430\u0437\u0430\u0434", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ago", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static void SplitSegmentRecursively(string text, List<string> output)
    {
        var normalized = MultiWhitespaceRegex.Replace(text.Trim(), " ");
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (normalized.Length <= MaxSegmentLength)
        {
            output.Add(normalized);
            return;
        }

        var breakIndex = FindBreakIndex(normalized);
        if (breakIndex <= 0 || breakIndex >= normalized.Length)
        {
            output.Add(normalized);
            return;
        }

        SplitSegmentRecursively(normalized[..breakIndex], output);
        SplitSegmentRecursively(normalized[breakIndex..], output);
    }

    private static int FindBreakIndex(string text)
    {
        var preferredUpperBound = Math.Min(MaxSegmentLength, text.Length - 1);
        var preferredLowerBound = Math.Max(48, preferredUpperBound - 96);

        for (var index = preferredUpperBound; index >= preferredLowerBound; index--)
        {
            if (IsPreferredBoundary(text[index]))
            {
                return index + 1;
            }
        }

        for (var index = preferredUpperBound; index >= 32; index--)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                return index + 1;
            }
        }

        return preferredUpperBound;
    }

    private static bool IsPreferredBoundary(char character) =>
        character is '.' or '!' or '?' or ';' or ':' or ',' or ')' or ']'
        || char.IsWhiteSpace(character);

    private static IEnumerable<string[]> BuildBatches(IReadOnlyList<string> segments)
    {
        var batch = new List<string>(MaxBatchSegments);
        var characterCount = 0;

        foreach (var segment in segments)
        {
            var nextLength = characterCount + segment.Length;
            if (batch.Count > 0 && (batch.Count >= MaxBatchSegments || nextLength > MaxBatchCharacters))
            {
                yield return batch.ToArray();
                batch.Clear();
                characterCount = 0;
            }

            batch.Add(segment);
            characterCount += segment.Length;
        }

        if (batch.Count > 0)
        {
            yield return batch.ToArray();
        }
    }

    private static string JoinTranslatedSegments(IReadOnlyList<string> segments)
    {
        var joined = string.Concat(segments);
        joined = PunctuationSpacingRegex.Replace(joined, "$1");
        joined = OpenBracketSpacingRegex.Replace(joined, "(");
        joined = CloseBracketSpacingRegex.Replace(joined, ")");
        joined = MultiWhitespaceRegex.Replace(joined, " ");
        return joined.Trim();
    }

    private static string PostProcessTranslation(string sourceLine, string translation, AppSettings settings)
    {
        if (!settings.SourceLanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase)
            || !settings.TargetLanguageCode.Equals("ru", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(translation))
        {
            return translation;
        }

        var processed = translation.Trim();

        if (sourceLine.Contains("speechless", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\bя\s+не\s+(?:могу|умею)\s+говорить\b", "у меня нет слов", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("what the fuck", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\b(?:что\s+за\s+блядь|черт\s+возьми)\b", "вот это да", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("goes hard", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\bэто\s+дерьмо\s+тяжело\b", "это звучит очень мощно", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("caught me off guard", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\b(?:удар\w*|улов\w*)\s+меня(?:\s+внезапно)?\b", "застал меня врасплох", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bудивил\w*\s+меня\b", "застал меня врасплох", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("fucking good", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\b(?:чрезвычайно|очень)\s+хорош\w*\b", "чертовски хорошо", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("underrated af", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\bнедооценены\b", "сильно недооценены", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bвместо\s+Индии\b", "чем в самой Индии", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bчем в Индии\b", "чем в самой Индии", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("rock on", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\bрок\s+на\s+парнях\b", "жгите дальше, ребята", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("insane", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\bИНСАН\b", "невероятный", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bинсан\b", "невероятный", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("Indian", StringComparison.OrdinalIgnoreCase)
            || sourceLine.Contains("India", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\bиндейск", "индийск", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("vocal range", StringComparison.OrdinalIgnoreCase))
        {
            processed = VocalAndVoiceRegex.Replace(processed, "вокальный диапазон и голос");
            processed = RangeAndVoiceRegex.Replace(processed, "вокальный диапазон и голос");

            if (sourceLine.Contains("insane", StringComparison.OrdinalIgnoreCase))
            {
                processed = Regex.Replace(
                    processed,
                    @"\bсумасшедш\w+\s+вокальн\w+\s+диапазон",
                    "невероятный вокальный диапазон",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        if (TryGetSingingLanguageHint(sourceLine, out var languageHint) && !processed.Contains(languageHint.RussianForm, StringComparison.OrdinalIgnoreCase))
        {
            processed = PatchSingingLanguagePhrase(processed, languageHint);
        }

        if (sourceLine.Contains("metalhead", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(
                processed,
                @"^Голландск\w+\s+металл(?:ическ\w+\s+голов\w+|ическ\w+\s+голова)\s+здесь\.?\s*",
                "Я металлист из Нидерландов. ",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(
                processed,
                @"\bметаллическ\w+\s+голов\w+\b",
                "металлист",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(
                processed,
                @"\bне\s+металлическ\w+\s+голов\w+\b",
                "не любители металла",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("cup of tea", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(
                processed,
                @"\bне\s+их\s+чашка\s+чая\b",
                "не совсем то, что им по душе",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("glimmer in their eyes", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(
                processed,
                @"\bпроблеск\b",
                "искра",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("joy and pride", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(
                processed,
                @"\bу\s+них\s+был[ао]?\s+чувств\w+\s+радости\s+и\s+гордости,\s+чтобы\s+увидеть,\s+что\b",
                "они почувствовали радость и гордость, увидев, что ",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(
                processed,
                @"\bчувство\s+радости\s+и\s+гордости,\s+чтобы\s+увидеть,\s+что\b",
                "радость и гордость от того, что ",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("bond with them", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(
                processed,
                @"\bбуквально\s+заставляет\s+меня\s+общаться\s+с\s+ними\b",
                "буквально сближает меня с ними",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(
                processed,
                @"\bпомогает\s+мне\s+чувствовать\s+себя\s+ближе\s+к\s+ним\b",
                "сближает меня с ними",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("opposite parts of the planet", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(
                processed,
                @"\bнесмотря\s+на\s+то,\s+что\s+я\s+из\s+разных\s+частей\s+планеты\b",
                "несмотря на то, что мы с разных концов планеты",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("Music transcends", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(
                processed,
                @"\bМузыка\s+выходит\s+за\s+рамки\b",
                "Музыка стирает границы",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("thanks for this guys", StringComparison.OrdinalIgnoreCase)
            || sourceLine.Contains("thanks for this, guys", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(
                processed,
                @"\bспасибо\s+за\s+этих\s+парней\b",
                "спасибо вам за это, ребята",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(
                processed,
                @"\bспасибо\s+за\s+это,\s+ребята\b",
                "спасибо вам за это, ребята",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        processed = MultiWhitespaceRegex.Replace(processed, " ");
        return processed.Trim();
    }

    private static bool TryGetSingingLanguageHint(string sourceLine, out LanguageHint hint)
    {
        if (!sourceLine.Contains("sing", StringComparison.OrdinalIgnoreCase))
        {
            hint = default!;
            return false;
        }

        foreach (var currentHint in LanguageHints)
        {
            if (sourceLine.Contains($"in {currentHint.SourceName}", StringComparison.OrdinalIgnoreCase))
            {
                hint = currentHint;
                return true;
            }
        }

        hint = default!;
        return false;
    }

    private static string PatchSingingLanguagePhrase(string translation, LanguageHint hint)
    {
        if (SingingPhraseRegex.IsMatch(translation))
        {
            return SingingPhraseRegex.Replace(translation, match => ReplaceSingingTail(match.Value, $"на {hint.RussianForm}"), 1);
        }

        if (SingingParticipleRegex.IsMatch(translation))
        {
            return SingingParticipleRegex.Replace(translation, match => ReplaceSingingTail(match.Value, $"на {hint.RussianForm}"), 1);
        }

        if (SingingNounRegex.IsMatch(translation))
        {
            return SingingNounRegex.Replace(translation, match => ReplaceSingingTail(match.Value, $"на {hint.RussianForm}"), 1);
        }

        return translation;
    }

    private static string ReplaceSingingTail(string phrase, string languageTail)
    {
        var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return phrase;
        }

        return words[0] + " " + languageTail;
    }

    private static List<LineTranslationPlan> MergeContinuationPlans(IReadOnlyList<LineTranslationPlan> plans, AppSettings settings)
    {
        if (plans.Count <= 1)
        {
            return plans.ToList();
        }

        var merged = new List<LineTranslationPlan>();

        foreach (var plan in plans)
        {
            if (merged.Count == 0)
            {
                merged.Add(plan);
                continue;
            }

            var previous = merged[^1];
            if (previous.PreserveOriginal || plan.PreserveOriginal)
            {
                merged.Add(plan);
                continue;
            }

            if (!ShouldMergePlans(previous, plan))
            {
                merged.Add(plan);
                continue;
            }

            var mergedOriginal = JoinLines(previous.OriginalLine, plan.OriginalLine);
            merged[^1] = BuildPlanForLine(mergedOriginal, settings);
        }

        return merged;
    }

    private static bool ShouldMergePlans(LineTranslationPlan previous, LineTranslationPlan current)
    {
        var previousSource = previous.TranslationSourceLine ?? previous.OriginalLine;
        var currentSource = current.TranslationSourceLine ?? current.OriginalLine;
        if (string.IsNullOrWhiteSpace(previousSource) || string.IsNullOrWhiteSpace(currentSource))
        {
            return false;
        }

        if (EndsWithStrongBoundary(previousSource) && StartsLikeNewSentence(currentSource))
        {
            return false;
        }

        return !EndsWithStrongBoundary(previousSource)
            || StartsWithContinuationToken(currentSource)
            || previousSource.Length < 48;
    }

    private static string JoinLines(string left, string right) =>
        $"{left.TrimEnd()} {right.TrimStart()}".Trim();

    private static bool EndsWithStrongBoundary(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var last = trimmed[^1];
        return last is '.' or '!' or '?' or ':' or ';';
    }

    private static bool StartsLikeNewSentence(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var first = trimmed[0];
        return char.IsUpper(first) || char.IsDigit(first);
    }

    private static bool StartsWithContinuationToken(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (char.IsLower(trimmed[0]))
        {
            return true;
        }

        var firstWord = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return firstWord.Equals("and", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("or", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("but", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("because", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("that", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("which", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("who", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("when", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("while", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("despite", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("despite", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("of", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("to", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExpandContraction(string value) =>
        value.ToLowerInvariant() switch
        {
            "im" => "I'm",
            "ive" => "I've",
            "ill" => "I'll",
            "id" => "I'd",
            "dont" => "don't",
            "cant" => "can't",
            "wont" => "won't",
            "didnt" => "didn't",
            "doesnt" => "doesn't",
            "isnt" => "isn't",
            "arent" => "aren't",
            "wasnt" => "wasn't",
            "werent" => "weren't",
            "shouldnt" => "shouldn't",
            "couldnt" => "couldn't",
            "wouldnt" => "wouldn't",
            "thats" => "that's",
            "theres" => "there's",
            "theyre" => "they're",
            "youre" => "you're",
            "weve" => "we've",
            "theyve" => "they've",
            "youve" => "you've",
            "hes" => "he's",
            "shes" => "she's",
            "lets" => "let's",
            "itll" => "it'll",
            "theyll" => "they'll",
            _ => value,
        };

    private static string BuildArguments(string scriptPath, string modelPath, AppSettings settings) =>
        $"-X utf8 -u \"{scriptPath}\" --model \"{modelPath}\" --source-language \"{settings.SourceLanguageCode}\" --target-language \"{settings.TargetLanguageCode}\" --threads {WorkerThreadCount} --beam-size {WorkerBeamSize} --server";

    private static string ResolvePythonExecutable(AppSettings settings)
    {
        var candidates = new[]
        {
            settings.OfflinePythonExecutablePath,
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
            $"Offline Python runtime not found. Install it on F: or set {PythonEnvVar}/OfflinePythonPath.");
    }

    private static string ResolveScriptPath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Scripts", "offline_translate.py");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new InvalidOperationException("offline_translate.py was not found in the application output.");
    }

    private static string ResolveModelPath(AppSettings settings)
    {
        var directCandidates = new[]
        {
            settings.OfflineModelPath,
            Environment.GetEnvironmentVariable(ModelEnvVar),
            Path.Combine(AppContext.BaseDirectory, "offline-models", ModelFolderName),
        };

        foreach (var candidate in directCandidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Models", ModelFolderName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Offline model not found. Run {SetupScriptPath} or set {ModelEnvVar}/OfflineModelPath.");
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

    private static bool ShouldPreserveLine(string line, AppSettings settings)
    {
        if (!settings.TargetLanguageCode.Equals("ru", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ContainsLatin(line))
        {
            return true;
        }

        if (!ContainsCyrillic(line))
        {
            return false;
        }

        var englishWords = GetEnglishWords(line);
        if (englishWords.Count == 0)
        {
            return true;
        }

        var stopWordCount = englishWords.Count(IsEnglishStopWord);
        var translatableRun = GetMaxTranslatableRun(englishWords);
        return stopWordCount == 0 && translatableRun <= 1;
    }

    private static bool LooksLikeMetadataLine(string line)
    {
        var letterCount = line.Count(char.IsLetter);
        var hashCount = line.Count(static character => character == '#');

        if (line.Contains("://", StringComparison.Ordinal) && letterCount <= 24)
        {
            return true;
        }

        if (hashCount >= 2 && letterCount <= (line.Length / 2))
        {
            return true;
        }

        if (!ContainsLatin(line))
        {
            return false;
        }

        var englishWords = GetEnglishWords(line);
        if (englishWords.Count == 0)
        {
            return false;
        }

        var stopWordCount = englishWords.Count(IsEnglishStopWord);
        if (MetadataKeywordRegex.IsMatch(line) && stopWordCount <= 2)
        {
            return true;
        }

        return line.Contains(" - ", StringComparison.Ordinal)
            && stopWordCount <= 1
            && !line.Contains(". ", StringComparison.Ordinal);
    }

    private static bool ShouldPreserveEnglishSpan(string span, string fullLine, int spanStartIndex, AppSettings settings)
    {
        if (!settings.TargetLanguageCode.Equals("ru", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var words = GetEnglishWords(span);
        if (words.Count == 0)
        {
            return true;
        }

        if (ShouldForceTranslateShortSpan(words))
        {
            return false;
        }

        if (LooksLikeMetadataLine(fullLine))
        {
            return true;
        }

        if (words.Any(static word => word.Any(static character => char.IsDigit(character)) || word.Contains('_')))
        {
            return true;
        }

        var stopWordCount = words.Count(IsEnglishStopWord);
        if (stopWordCount > 0)
        {
            return false;
        }

        if (words.Count <= 3 && words.All(IsLikelyAliasWord))
        {
            return true;
        }

        if (ContainsCyrillic(fullLine) && words.Count <= 2 && words.All(IsLikelyAliasWord))
        {
            return true;
        }

        var leftContext = fullLine[..spanStartIndex];
        return FeaturingRegex.IsMatch(leftContext)
            || leftContext.EndsWith("@", StringComparison.Ordinal)
            || leftContext.EndsWith("#", StringComparison.Ordinal);
    }

    private static bool ShouldForceTranslateShortSpan(IReadOnlyList<string> words)
    {
        if (words.Count == 0 || words.Count > 3)
        {
            return false;
        }

        return words.All(static word => ForceTranslateShortWords.Contains(word.Trim('.', '\'', '"')));
    }

    private static int GetMaxTranslatableRun(IReadOnlyList<string> words)
    {
        var maxRun = 0;
        var currentRun = 0;

        foreach (var word in words)
        {
            if (IsEnglishStopWord(word) || !IsLikelyAliasWord(word))
            {
                currentRun++;
                maxRun = Math.Max(maxRun, currentRun);
                continue;
            }

            currentRun = 0;
        }

        return maxRun;
    }

    private static List<string> GetEnglishWords(string text) =>
        EnglishWordRegex.Matches(text)
            .Select(static match => match.Value)
            .Where(static word => word.Length > 0)
            .ToList();

    private static bool IsEnglishStopWord(string word) =>
        EnglishStopWords.Contains(word.Trim('.', '\'', '"'));

    private static bool IsLikelyAliasWord(string word)
    {
        var cleanWord = word.Trim('.', '\'', '"');
        if (cleanWord.Length == 0)
        {
            return false;
        }

        if (MetadataKeywordRegex.IsMatch(cleanWord))
        {
            return true;
        }

        if (UppercaseAcronymAllowList.Contains(cleanWord))
        {
            return true;
        }

        if (cleanWord.Any(static character => char.IsDigit(character)) || cleanWord.Contains('_'))
        {
            return true;
        }

        if (char.IsUpper(cleanWord[0]))
        {
            return true;
        }

        return cleanWord.Skip(1).Any(char.IsUpper)
            || cleanWord.Equals(cleanWord.ToUpperInvariant(), StringComparison.Ordinal);
    }

    private static bool ContainsLatin(string text) => text.Any(IsLatin);

    private static bool ContainsCyrillic(string text) => text.Any(IsCyrillic);

    private static bool IsLatin(char character) =>
        (character >= 'A' && character <= 'Z')
        || (character >= 'a' && character <= 'z');

    private static bool IsCyrillic(char character) =>
        (character >= '\u0400' && character <= '\u04FF')
        || character is '\u0451' or '\u0401';

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
        return string.IsNullOrWhiteSpace(stderr)
            ? message
            : $"{message} {stderr}";
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

    private static void AppendDebugLog(string message)
    {
        try
        {
            var entry =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}" +
                new string('-', 80) + Environment.NewLine;
            File.AppendAllText(DebugLogPath, entry, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private sealed record LineTranslationPlan(string OriginalLine, string? TranslationSourceLine, IReadOnlyList<LineTranslationPiece> Pieces)
    {
        public bool PreserveOriginal => Pieces.All(static piece => piece.PreserveOriginal);

        public string RenderPreserved() => string.Concat(Pieces.Select(static piece => piece.OutputText));

        public static LineTranslationPlan Preserve(string originalLine) =>
            new(originalLine, null, new[] { LineTranslationPiece.Preserve(originalLine) });

        public static LineTranslationPlan Translate(
            string originalLine,
            string translationSourceLine,
            IReadOnlyList<LineTranslationPiece> pieces) =>
            new(originalLine, translationSourceLine, pieces);
    }

    private sealed record LineTranslationPiece(string OutputText, string? TranslationSourceText)
    {
        public bool PreserveOriginal => TranslationSourceText is null;

        public static LineTranslationPiece Preserve(string text) => new(text, null);

        public static LineTranslationPiece Translate(string outputText, string translationSourceText) =>
            new(outputText, translationSourceText);
    }

    private sealed record LanguageHint(string SourceName, string RussianForm);

    private sealed record OfflineTranslationRequest(IReadOnlyList<string> Texts, string SourceLanguage, string TargetLanguage);

    private sealed record OfflineTranslationResponse(string[]? Translations, string? Translation, string? Error);
}
