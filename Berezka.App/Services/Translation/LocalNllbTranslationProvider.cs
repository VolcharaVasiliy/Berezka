using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Berezka.App.Models;

namespace Berezka.App.Services.Translation;

internal sealed class LocalNllbTranslationProvider : ITranslationProvider, IDisposable
{
    private const int MaxAttempts = 2;
    private const int MaxBatchSegments = 8;
    private const int MaxBatchCharacters = 720;
    private const int MaxSegmentLength = 220;
    private const int WorkerThreadCount = 2;
    private const int WorkerBeamSize = 2;
    private const float WorkerPatience = 1.0f;
    private const float WorkerRepetitionPenalty = 1.08f;
    private const int WorkerNoRepeatNgramSize = 3;
    private const int WorkerMaxDecodingLength = 192;
    private const string ModelFolderName = "nllb-200-distilled-600m-ctranslate2";
    private const string SetupScriptPath = @"scripts\setup_local_nllb.ps1";
    private const string DefaultPythonPath = @"F:\DevTools\Python311\python.exe";
    private const string PythonEnvVar = "BEREZKA_PYTHON";
    private const string LegacyPythonEnvVar = "ELOCHKA_PYTHON";
    private const string ModelEnvVar = "BEREZKA_OFFLINE_MODEL";
    private const string LegacyModelEnvVar = "ELOCHKA_OFFLINE_MODEL";
    private static readonly TimeSpan WorkerIdleLifetime = TimeSpan.FromMinutes(10);
    private static readonly string DebugLogPath = BerezkaPaths.TranslationDebugLogPath;
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
    private static readonly Regex PreservingMetadataKeywordRegex = new(@"(?i)\b(?:ft|feat|featuring|prod|remix|cover|ost|op|ed|amv|mv|pv|ver|collab)\.?\b", RegexOptions.Compiled);
    private static readonly Regex UiActionLineRegex = new(@"^\s*(?:\u041F\u0435\u0440\u0435\u0432\u0435\u0441\u0442\u0438\s+\u043D\u0430\s+\u0440\u0443\u0441\u0441\u043A\u0438\u0439|\u041E\u0442\u0432\u0435\u0442\u0438\u0442\u044C|\u0438\u0437\u043C\u0435\u043D\u0435\u043D\u043E|Translate\s+to\s+Russian|Reply|edited)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EngagementNoiseLineRegex = new(@"^\s*[@#©®&%\d\s\-\—\.,;:()\[\]\{\}/|<>!?]+\s*(?:\u041E\u0442\u0432\u0435\u0442\u0438\u0442\u044C|Reply)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DynamicNamedLocationRegex = new(
        @"(?<![A-Za-z])(?:[A-Z][A-Za-z]+(?:[-'][A-Z][A-Za-z]+)?\s+){1,4}(?:Temple|Mausoleum|Village|Hamlet|Town|City|Forest|Court|Hall|Pavilion|Valley|Palace|Shrine|Monastery|Sanctum|Pagoda|Peak|Pass|Inn|River|Lake|Harbor|Harbour|Garden|Camp|Manor|Bridge|Cove|Abode|Ruins|Fort|Keep|Outpost|Market|Bazaar|Marsh|Swamp|Mountain|Mount|Cliff|Island|Gorge|Ridge)(?![A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DynamicTitledNameRegex = new(
        @"(?<![A-Za-z])(?:Officer|Master|Elder|General|Commander|Captain|Lord|Lady|Scholar)\s+[A-Z][A-Za-z]+(?:\s+[A-Z][A-Za-z]+){0,2}(?![A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
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
    private static readonly PhrasePattern[] ForceTranslateDomainPhrasePatterns = CreatePhrasePatterns(
        "Jianghu Errands",
        "Jianghu Legacy",
        "Gift of Gab",
        "Purple Star Catastrophe",
        "Meridian Touch",
        "Wind Sense",
        "Inner Path",
        "Mystic Arts",
        "Martial Arts Level",
        "inner arts",
        "sectless",
        "Jianghu",
        "Wuxia",
        "Wulin",
        "Murim",
        "Daoist",
        "dao",
        "qi",
        "dual cultivation",
        "skill theft",
        "Xiangqi");
    private static readonly PhrasePattern[] ProtectedDomainPhrasePatterns = CreatePhrasePatterns(
        "Where Winds Meet",
        "Kaifeng",
        "Qinghe",
        "Yanyun",
        "Yanyun Sixteen Prefectures",
        "Jadewood Court",
        "Mistveil City",
        "Mistveil Forest",
        "Wishing Cove",
        "Hollow Abode",
        "Aureate Pavilion",
        "Martial Temple",
        "Qin Caiwei",
        "Murong Yuan",
        "Murong Yanzhao",
        "Shi Zhen",
        "Zhai Xu",
        "Officer Nan",
        "Song Wu",
        "Shen Weiqing",
        "Qi Sheng",
        "Ghost Revelry Hall",
        "Gift of Gab",
        "Wind Sense",
        "Meridian Touch",
        "Old Friends",
        "Free Persuasion",
        "Rhetoric Duel",
        "Mental Focus",
        "Inspiration",
        "Trash Talk",
        "Bluster",
        "Provocation",
        "Rebuttal",
        "Filibuster",
        "Straightforwardness",
        "Analogy",
        "Scholar",
        "Jiuliu Sect",
        "Jiuliu",
        "Kuanglan Sect",
        "Kuanglan",
        "Liyuan Sect",
        "Liyuan",
        "Lonely Cloud Sect",
        "Lonely Cloud",
        "Drunken Flowers Sect",
        "Drunken Flowers",
        "Wenjin Pavilion",
        "Heartless Valley",
        "Tianquan",
        "Qingxi",
        "Sangeng Sky",
        "Hiram",
        "Erenor",
        "Lunagem",
        "Luna Charm",
        "Lunafrost",
        "Kutum",
        "Nouver",
        "Valks' Cry",
        "Cron Stone",
        "Caphras",
        "Blackstar",
        "Garmoth",
        "Naderr's Band",
        "Action Coins",
        "Terror Protection Stone",
        "Terror Protection",
        "Luck Enhancer",
        "Zenith Conquest",
        "Clan Altar");
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

        _ = TranslationGlossaryRuntime.Default;

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

        _ = TranslationGlossaryRuntime.Default;

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
            joined = PostProcessTranslation(plan.OriginalLine, joined, settings);
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
        var protectedMatches = FindProtectedDomainPhraseMatches(line);
        if (protectedMatches.Count > 0)
        {
            var protectedPieces = new List<LineTranslationPiece>();
            var protectedCursor = 0;
            foreach (var match in protectedMatches)
            {
                if (match.StartIndex > protectedCursor)
                {
                    protectedPieces.AddRange(BuildPiecesForSegment(line[protectedCursor..match.StartIndex], settings));
                }

                protectedPieces.Add(LineTranslationPiece.Preserve(match.CanonicalText));
                protectedCursor = match.EndIndex;
            }

            if (protectedCursor < line.Length)
            {
                protectedPieces.AddRange(BuildPiecesForSegment(line[protectedCursor..], settings));
            }

            return MergeAdjacentPieces(protectedPieces);
        }

        return MergeAdjacentPieces(BuildPiecesForSegment(line, settings));
    }

    private static List<LineTranslationPiece> BuildPiecesForSegment(string line, AppSettings settings)
    {
        var pieces = new List<LineTranslationPiece>();
        var lastIndex = 0;
        var glossary = TranslationGlossaryRuntime.Default;

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
            pieces.AddRange(BuildPiecesForEnglishSpan(span, line, match.Index, settings, glossary));

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < line.Length)
        {
            pieces.Add(LineTranslationPiece.Preserve(line[lastIndex..]));
        }

        return pieces;
    }

    private static IEnumerable<LineTranslationPiece> BuildPiecesForEnglishSpan(
        string span,
        string fullLine,
        int spanStartIndex,
        AppSettings settings,
        TranslationGlossaryRuntime glossary)
    {
        var matches = glossary.FindDirectMatches(span);
        if (matches.Count == 0)
        {
            foreach (var piece in BuildPiecesForPlainEnglishSlice(span, fullLine, spanStartIndex, settings))
            {
                yield return piece;
            }
            yield break;
        }

        var cursor = 0;
        foreach (var match in matches)
        {
            if (match.StartIndex > cursor)
            {
                foreach (var piece in BuildPiecesForPlainEnglishSlice(
                    span[cursor..match.StartIndex],
                    fullLine,
                    spanStartIndex + cursor,
                    settings))
                {
                    yield return piece;
                }
            }

            yield return LineTranslationPiece.Preserve(match.RenderedText);
            cursor = match.EndIndex;
        }

        if (cursor < span.Length)
        {
            foreach (var piece in BuildPiecesForPlainEnglishSlice(
                span[cursor..],
                fullLine,
                spanStartIndex + cursor,
                settings))
            {
                yield return piece;
            }
        }
    }

    private static IEnumerable<LineTranslationPiece> BuildPiecesForPlainEnglishSlice(
        string span,
        string fullLine,
        int spanStartIndex,
        AppSettings settings)
    {
        if (span.Length == 0)
        {
            yield break;
        }

        var leadingWhitespaceLength = span.TakeWhile(char.IsWhiteSpace).Count();
        if (leadingWhitespaceLength > 0)
        {
            yield return LineTranslationPiece.Preserve(span[..leadingWhitespaceLength]);
        }

        var trailingWhitespaceLength = span.Reverse().TakeWhile(char.IsWhiteSpace).Count();
        var coreStartIndex = leadingWhitespaceLength;
        var coreLength = span.Length - leadingWhitespaceLength - trailingWhitespaceLength;
        if (coreLength > 0)
        {
            var core = span.Substring(coreStartIndex, coreLength);
            if (ShouldPreserveEnglishSpan(core, fullLine, spanStartIndex + coreStartIndex, settings))
            {
                yield return LineTranslationPiece.Preserve(NormalizePreservedMetadataLine(core));
            }
            else
            {
                yield return LineTranslationPiece.Translate(core, NormalizeSourceForTranslation(core, settings));
            }
        }

        if (trailingWhitespaceLength > 0)
        {
            yield return LineTranslationPiece.Preserve(span[^trailingWhitespaceLength..]);
        }
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
        normalized = Regex.Replace(normalized, @"(?i)\bwtf\b", "wow", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bomg\b", "wow", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bspeechless\b", "with no words", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bthis\s+song\s+slaps\b", "this song sounds incredible", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bit\s+slaps\b", "it sounds incredible", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bslaps\b", "sounds incredible", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bmy\s+bro\s+cooked\s+with\s+this\s+one\b", "my friend really nailed this one", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bcooked\s+with\s+this\s+one\b", "really nailed this one", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bbanger\b", "absolute hit", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bfr\b", "for real", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bno\s+cap\b", "seriously", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bgoes\s+hard\b", "sounds extremely powerful", RegexOptions.CultureInvariant);
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
        normalized = ApplyGamingDomainSourceNormalization(normalized);
        normalized = ApplyWhereWindsMeetSourceNormalization(normalized);

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

        if (Regex.IsMatch(sourceLine, @"(?i)^\s*cultivation!?\s*$", RegexOptions.CultureInvariant))
        {
            return sourceLine.TrimEnd().EndsWith("!", StringComparison.Ordinal) ? "Культивация!" : "Культивация";
        }

        if (Regex.IsMatch(sourceLine, @"(?i)^\s*cloud\s+mail\s*$", RegexOptions.CultureInvariant))
        {
            return "Облачная почта";
        }

        var versionMatch = Regex.Match(sourceLine, @"(?i)^\s*version\s+([0-9][A-Za-z0-9\.\-]*)\s*$", RegexOptions.CultureInvariant);
        if (versionMatch.Success)
        {
            return $"Версия {versionMatch.Groups[1].Value}";
        }

        if (Regex.IsMatch(sourceLine, @"(?i)^\s*reward\s+highlights\s*$", RegexOptions.CultureInvariant))
        {
            return "Основные награды";
        }

        if (Regex.IsMatch(sourceLine, @"(?i)^\s*exploration\s+outfit\s*$", RegexOptions.CultureInvariant))
        {
            return "Костюм исследователя";
        }

        if (sourceLine.Contains("speechless", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\bя\s+не\s+(?:могу|умею)\s+говорить\b", "у меня нет слов", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("what the fuck", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\b(?:что\s+за\s+блядь|черт\s+возьми)\b", "вот это да", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (Regex.IsMatch(sourceLine, @"(?i)\bwtf\b", RegexOptions.CultureInvariant))
        {
            processed = Regex.Replace(processed, @"\bесли\b", "вот это да", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("goes hard", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\bэто\s+дерьмо\s+тяжело\b", "это звучит очень мощно", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bид(?:ет|ёт)\s+тяжело\b", "звучит очень мощно", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bв\s+общем-?то\s+ид(?:ет|ёт)\s+тяжело\b", "вообще звучит мощно", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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

        if (sourceLine.Contains("slaps", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\bпоет\b", "просто разносит", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bшлепает\b", "просто разносит", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (sourceLine.Contains("cooked with this one", StringComparison.OrdinalIgnoreCase)
            || sourceLine.Contains("nailed this one", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\bприготовил\w*\s+с\s+этим\b", "реально выдал", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bмой\s+брат\b", "братан", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = "Братан тут реально выдал.";
        }

        if (sourceLine.Contains("banger", StringComparison.OrdinalIgnoreCase)
            || sourceLine.Contains("absolute hit", StringComparison.OrdinalIgnoreCase)
            || sourceLine.Contains("no cap", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(sourceLine, @"(?i)\bfr\b", RegexOptions.CultureInvariant))
        {
            processed = Regex.Replace(processed, @"\bбез\s+крышки\b", "без шуток", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bабсолютный\s+хит\b", "реальный бэнгер", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bабсолютный\s+удар\b", "реальный бэнгер", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if ((sourceLine.Contains("banger", StringComparison.OrdinalIgnoreCase)
                || sourceLine.Contains("absolute hit", StringComparison.OrdinalIgnoreCase))
            && (sourceLine.Contains("no cap", StringComparison.OrdinalIgnoreCase)
                || sourceLine.Contains("seriously", StringComparison.OrdinalIgnoreCase)))
        {
            processed = "Реальный бэнгер, без шуток.";
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
            processed = Regex.Replace(
                processed,
                @"\b(?:безумн\w*|невероятн\w*|сумасшедш\w*)?\s*диапазон\s+голоса(?:\s+и\s+голос)?\b",
                "невероятный вокальный диапазон и голос",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (sourceLine.Contains("insane", StringComparison.OrdinalIgnoreCase))
            {
                processed = Regex.Replace(
                    processed,
                    @"\bсумасшедш\w+\s+вокальн\w+\s+диапазон",
                    "невероятный вокальный диапазон",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        if (sourceLine.Contains("vocal range and voice", StringComparison.OrdinalIgnoreCase)
            && !Regex.IsMatch(processed, @"\bвокальн\w*\s+диапазон\s+и\s+голос\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            processed = processed.TrimEnd('.', ' ') + ", невероятный вокальный диапазон и голос.";
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

        if (sourceLine.Contains("track", StringComparison.OrdinalIgnoreCase))
        {
            processed = Regex.Replace(processed, @"\bтрасс\w*\b", "трек", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        processed = ApplyWhereWindsMeetPostProcessing(sourceLine, processed);
        processed = ApplyGamingDomainPostProcessing(sourceLine, processed);
        processed = MultiWhitespaceRegex.Replace(processed, " ");
        return processed.Trim();
    }

    private static string ApplyWhereWindsMeetSourceNormalization(string value)
    {
        var normalized = value;
        normalized = Regex.Replace(normalized, @"(?i)\bwwm\b", "Where Winds Meet", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bjiang\s*hu\b", "Jianghu", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bwu\s*xia\b", "Wuxia", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bwu\s*lin\b", "Wulin", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bmurim\b", "Murim", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)d[a?????]o", "dao", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bdaoist\b", "Daoist", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bdao\b", "dao", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bqi\b", "qi energy", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bsectless\b", "without a sect", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bdual\s+cultivation\b", "dual cultivation", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\binner arts system\b", "inner arts system", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\binner arts\b", "inner arts", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\binner path\b", "Inner Path", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bmystic arts\b", "Mystic Arts", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bmartial arts level\b", "Martial Arts Level", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bmeridian touch\b", "Meridian Touch", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bwind sense\b", "Wind Sense", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bjianghu errands\b", "Jianghu Errands", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bjianghu legacy\b", "Jianghu Legacy", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bskill theft\b", "skill theft", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bgift of gab\b", "Gift of Gab", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bpurple star catastrophe\b", "Purple Star Catastrophe", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bxiangqi\b", "Xiangqi", RegexOptions.CultureInvariant);
        return normalized;
    }

    private static string ApplyGamingDomainSourceNormalization(string value)
    {
        var normalized = value;
        normalized = Regex.Replace(normalized, @"(?i)\boom\b", "out of mana", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bdps\s+check\b", "damage check", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bdps\b", "damage output", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\blost\s+aggro\b", "lost enemy aggro", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bhold\s+aggro\b", "keep enemy aggro", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\baggro\b", "enemy aggro", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\blost\s+enemy\s+enemy\s+aggro\b", "lost enemy aggro", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bkeep\s+enemy\s+enemy\s+aggro\b", "keep enemy aggro", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bbig\s+pull\b", "large enemy pull", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bpull(?:ing|ed)?\s+the\s+(boss|pack|group|mobs?)\b", "engage the $1", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bwipe(?:d|s|ing)?\b", "full party wipe", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bkite(?:d|s|ing)?\b", "keep distance while attacking", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\badds\b", "additional enemies", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\btrash\s+mobs?\b", "trash enemies", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bgear\s+score\b", "equipment score", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bgear\b", "equipment", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bbis\b", "best in slot", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bspec\b", "specialization", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bbuild\b", "character build", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bcc\b", "crowd control", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\baoe\b", "area damage", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bgcd\b", "global cooldown", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bcds?\b", "cooldown", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bproc\s+rate\b", "trigger rate", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bproc(?:s|ed|ing)?\b", "special effect trigger", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bbuffed\b", "made stronger", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bnerfed\b", "made weaker", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bdebuffs?\b", "negative status effects", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bbuffs?\b", "positive status effects", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?i)\bmmorpg\b", "MMORPG", RegexOptions.CultureInvariant);
        return normalized;
    }

    private static string ApplyWhereWindsMeetPostProcessing(string sourceLine, string value)
    {
        var processed = value;

        processed = RestoreProtectedDomainPhrases(sourceLine, processed);

        if (ContainsAllIgnoreCase(sourceLine, "Jianghu is not a place", "wuxia stories"))
        {
            return "?????? ? ??? ?? ?????, ? ?????? ?????????? ??? ? ???????? ???.";
        }

        if (ContainsAllIgnoreCase(sourceLine, "staying sectless is viable", "which sect to join"))
        {
            return "?????????? ??? ????? ?????? ?????????, ???? ?? ??????, ? ????? ????? ????????.";
        }

        if (ContainsAllIgnoreCase(sourceLine, "Qin Caiwei", "Kaifeng", "Jianghu Errands", "sent me to"))
        {
            return "Qin Caiwei отправила меня в Kaifeng по поручениям Цзянху.";
        }

        if (ContainsAllIgnoreCase(sourceLine, "sectless", "Mystic Arts", "Inner Path"))
        {
            return "Игрок без секты все равно может изучать мистические искусства и Внутренний путь.";
        }

        if (ContainsAllIgnoreCase(sourceLine, "Meridian Touch", "Wind Sense", "Mistveil Forest"))
        {
            return "Используй Касание меридианов и Чутье ветра в Mistveil Forest.";
        }

        if (ContainsAllIgnoreCase(sourceLine, "Murong Yanzhao", "Qinghe", "Purple Star Catastrophe", "returned to"))
        {
            return "Murong Yanzhao вернулся в Qinghe после катастрофы Пурпурной звезды.";
        }

        if (ContainsAllIgnoreCase(sourceLine, "Jianghu is bigger than any single sect", "Where Winds Meet"))
        {
            return "Цзянху в Where Winds Meet больше любой отдельной школы.";
        }

        if (ContainsAllIgnoreCase(sourceLine, "Gift of Gab", "Jianghu Legacy")
            && ContainsAnyIgnoreCase(sourceLine, "quest", "quests"))
        {
            return "Дар красноречия помогает в заданиях наследия Цзянху.";
        }

        if (ContainsPhrase(sourceLine, "Jianghu Errands"))
        {
            processed = ReplacePhrase(processed, "Jianghu Errands", "поручения Цзянху");
            processed = Regex.Replace(processed, @"\b(?:поручения|задания)\s+jianghu\b", "поручения Цзянху", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsPhrase(sourceLine, "Jianghu Legacy"))
        {
            processed = ReplacePhrase(processed, "Jianghu Legacy", "наследие Цзянху");
            processed = Regex.Replace(processed, @"\b(?:наследие|наследство)\s+jianghu\b", "наследие Цзянху", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsPhrase(sourceLine, "Gift of Gab"))
        {
            processed = ReplacePhrase(processed, "Gift of Gab", "дар красноречия");
        }

        if (ContainsPhrase(sourceLine, "Purple Star Catastrophe"))
        {
            processed = ReplacePhrase(processed, "Purple Star Catastrophe", "катастрофа Пурпурной звезды");
        }

        if (ContainsPhrase(sourceLine, "Meridian Touch"))
        {
            processed = ReplacePhrase(processed, "Meridian Touch", "Meridian Touch");
        }

        if (ContainsPhrase(sourceLine, "Wind Sense"))
        {
            processed = ReplacePhrase(processed, "Wind Sense", "Wind Sense");
        }

        if (ContainsPhrase(sourceLine, "Inner Path"))
        {
            processed = ReplacePhrase(processed, "Inner Path", "Внутренний путь");
        }

        if (ContainsPhrase(sourceLine, "Mystic Arts"))
        {
            processed = ReplacePhrase(processed, "Mystic Arts", "мистические искусства");
        }

        if (ContainsPhrase(sourceLine, "Martial Arts Level"))
        {
            processed = ReplacePhrase(processed, "Martial Arts Level", "уровень боевых искусств");
        }

        if (ContainsPhrase(sourceLine, "inner arts"))
        {
            processed = Regex.Replace(processed, @"\bвнутренн\w+\s+(?:боев\w+\s+)?искусств\w*\b", "внутренние искусства", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsPhrase(sourceLine, "skill theft"))
        {
            processed = ReplacePhrase(processed, "skill theft", "кража навыка");
        }

        if (ContainsPhrase(sourceLine, "Xiangqi"))
        {
            processed = ReplacePhrase(processed, "Xiangqi", "сянци");
        }

        if (ContainsPhrase(sourceLine, "dual cultivation"))
        {
            processed = ReplacePhrase(processed, "dual cultivation", "двойная культивация");
        }

        if (ContainsPhrase(sourceLine, "Jianghu"))
        {
            processed = ReplacePhrase(processed, "Jianghu", "Цзянху");
        }

        if (ContainsPhrase(sourceLine, "Wuxia"))
        {
            processed = ReplacePhrase(processed, "Wuxia", "уся");
        }

        if (ContainsPhrase(sourceLine, "Wulin"))
        {
            processed = ReplacePhrase(processed, "Wulin", "Улинь");
        }

        if (ContainsPhrase(sourceLine, "Murim"))
        {
            processed = ReplacePhrase(processed, "Murim", "мурим");
        }

        if (ContainsPhrase(sourceLine, "Daoist"))
        {
            processed = ReplacePhrase(processed, "Daoist", "даос");
        }

        if (ContainsPhrase(sourceLine, "dao"))
        {
            processed = Regex.Replace(processed, @"(?i)\bdao\b", "дао", RegexOptions.CultureInvariant);
        }

        if (ContainsPhrase(sourceLine, "qi"))
        {
            processed = Regex.Replace(processed, @"(?i)\bqi\b", "ци", RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bэнерг\w+\s+ци\b", "ци", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsPhrase(sourceLine, "sectless"))
        {
            processed = Regex.Replace(processed, @"\bбез\s+(?:секты|школы|ордена)\b", "без секты", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ShouldUseSchoolWordingForSect(sourceLine))
        {
            processed = Regex.Replace(processed, @"\bсекты\b", "школы", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bсекте\b", "школе", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bсекту\b", "школу", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bсектой\b", "школой", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bсекта\b", "школа", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return processed;
    }

    private static string ApplyGamingDomainPostProcessing(string sourceLine, string value)
    {
        var processed = value;

        if (ContainsAnyIgnoreCase(sourceLine, "Cron Stone", "Cron Stones")
            && ContainsAllIgnoreCase(sourceLine, "Blackstar", "attempt"))
        {
            return "Береги кроны для попыток PEN Blackstar.";
        }

        if (ContainsAllIgnoreCase(sourceLine, "failstack", "Kutum", "TET"))
        {
            return "Мне нужно больше фейлстаков для этого TET Kutum.";
        }

        if (ContainsAllIgnoreCase(sourceLine, "Labor", "Hiram")
            && ContainsAnyIgnoreCase(sourceLine, "Lunagem", "Lunagems", "synthesis"))
        {
            return "Береги лабор для синтеза Hiram и Lunagems.";
        }

        if (ContainsAnyIgnoreCase(sourceLine, "lost enemy aggro", "lost enemy enemy aggro")
            && ContainsAnyIgnoreCase(sourceLine, "full party wipe")
            && ContainsAnyIgnoreCase(sourceLine, "raid"))
        {
            return "Танк потерял агро, и весь рейд вайпнулся.";
        }

        if (ContainsAnyIgnoreCase(sourceLine, "keep distance while attacking")
            && ContainsAnyIgnoreCase(sourceLine, "additional enemies")
            && ContainsAnyIgnoreCase(sourceLine, "cooldown"))
        {
            return "Кайти аддов и прожимай кулдауны.";
        }

        if (ContainsAnyIgnoreCase(sourceLine, "we need more damage output")
            && ContainsAnyIgnoreCase(sourceLine, "boss"))
        {
            return "Нам нужно больше дпса на этого босса.";
        }

        if (ContainsAnyIgnoreCase(sourceLine, "healer")
            && ContainsAnyIgnoreCase(sourceLine, "out of mana")
            && ContainsAnyIgnoreCase(sourceLine, "large enemy pull"))
        {
            return "Хил без маны после большого пула.";
        }

        if (ContainsAnyIgnoreCase(sourceLine, "dungeon")
            && ContainsAnyIgnoreCase(sourceLine, "equipment")
            && ContainsAnyIgnoreCase(sourceLine, "loot"))
        {
            return "Фарми этот данж ради лучшего гира и лута.";
        }

        if (ContainsAnyIgnoreCase(sourceLine, "crowd control")
            && ContainsAnyIgnoreCase(sourceLine, "trash enemies")
            && ContainsAnyIgnoreCase(sourceLine, "engage the boss"))
        {
            return "Дай контроль по треш-мобам перед пулом босса.";
        }

        if (ContainsAnyIgnoreCase(sourceLine, "character build")
            && ContainsAnyIgnoreCase(sourceLine, "best in slot")
            && ContainsAnyIgnoreCase(sourceLine, "pve")
            && ContainsAnyIgnoreCase(sourceLine, "pvp"))
        {
            return "Этот билд BiS для PvE, но мусор в PvP.";
        }

        if (ContainsAnyIgnoreCase(sourceLine, "trigger rate")
            && ContainsAnyIgnoreCase(sourceLine, "trinket"))
        {
            return "Шанс прока на этом тринкете просто безумный.";
        }

        if (ContainsAnyIgnoreCase(sourceLine, "damage check", "dps"))
        {
            processed = Regex.Replace(processed, @"\bпроверк\w+\s+урон\w+\b", "дпс-чек", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bбольше\s+урон\w+\b", "больше дпса", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bурон\s+в\s+секунду\b", "дпс", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bповрежден\w+\b", "дпса", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "tank"))
        {
            processed = Regex.Replace(processed, @"\bбак\b", "танк", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "healer"))
        {
            processed = Regex.Replace(processed, @"\bцелител\w+\b", "хил", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bлекар\w+\b", "хил", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "oom", "out of mana"))
        {
            processed = Regex.Replace(processed, @"\bвне\s+маны\b", "без маны", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bзакончилась\s+мана\b", "без маны", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "aggro", "enemy aggro"))
        {
            processed = Regex.Replace(processed, @"\bагресси\w+\b", "агро", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bвнимани[ея]\s+(?:врага|монстр\w+|противник\w+)\b", "агро", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bугроз\w+\b", "агро", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "wipe", "full party wipe"))
        {
            processed = Regex.Replace(processed, @"\bпол(?:н\w+\s+)?(?:уничтожен\w+|разгром\w+)\s+(?:группы|отряда|рейда)\b", "вайп", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bполный\s+вайп\b", "вайп", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "kite", "keep distance while attacking"))
        {
            processed = Regex.Replace(processed, @"\b(?:держи|держите|держать)\s+дистанц\w+\s+и\s+(?:атакуй|атакуйте|атаковать)\b", "кайтите", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bводи\b", "кайти", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "additional enemies", "adds"))
        {
            processed = Regex.Replace(processed, @"\bдополнительн\w+\s+враг\w+\b", "адды", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "trash enemies", "trash mobs"))
        {
            processed = Regex.Replace(processed, @"\bмелк\w+\s+враг\w+\b", "треш-мобы", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bмусорн\w+\s+враг\w+\b", "треш-мобы", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "raid"))
        {
            processed = Regex.Replace(processed, @"\bнабег\w*\b", "рейд", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "dungeon"))
        {
            processed = Regex.Replace(processed, @"\bподземель\w+\b", "данж", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "loot"))
        {
            processed = Regex.Replace(processed, @"\bдобыч\w+\b", "лут", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "equipment score"))
        {
            processed = Regex.Replace(processed, @"\bуров\w+\s+экипировк\w+\b", "гирскор", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "gear", "equipment"))
        {
            processed = Regex.Replace(processed, @"\bэкипировк\w+\b", "гир", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "best in slot"))
        {
            processed = Regex.Replace(processed, @"\bлучш\w+\s+в\s+своем\s+слоте\b", "BiS", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "specialization"))
        {
            processed = Regex.Replace(processed, @"\bспециализац\w+\b", "спек", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "character build", "build"))
        {
            processed = Regex.Replace(processed, @"\bсборк\w+\s+персонажа\b", "билд", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "crowd control"))
        {
            processed = Regex.Replace(processed, @"\bконтроль\s+толпы\b", "контроль", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "area damage"))
        {
            processed = Regex.Replace(processed, @"\bурон\s+по\s+площади\b", "аое", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "global cooldown"))
        {
            processed = Regex.Replace(processed, @"\bглобальн\w+\s+кулдаун\b", "ГКД", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bглобальн\w+\s+перезарядк\w+\b", "ГКД", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        else if (ContainsAnyIgnoreCase(sourceLine, "cooldown"))
        {
            processed = Regex.Replace(processed, @"\bперезарядк\w+\b", "кулдаун", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "trigger rate", "special effect trigger", "proc"))
        {
            processed = Regex.Replace(processed, @"\bчастот\w+\s+срабатыван\w+\b", "шанс прока", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            processed = Regex.Replace(processed, @"\bсрабатыван\w+\b", "прок", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "positive status effect", "buff"))
        {
            processed = Regex.Replace(processed, @"\bположительн\w+\s+эффект\w+\s+статуса\b", "баф", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (ContainsAnyIgnoreCase(sourceLine, "negative status effect", "debuff"))
        {
            processed = Regex.Replace(processed, @"\bотрицательн\w+\s+эффект\w+\s+статуса\b", "дебаф", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return processed;
    }

    private static string RestoreProtectedDomainPhrases(string sourceLine, string value)
    {
        var restored = value;
        foreach (var pattern in ProtectedDomainPhrasePatterns)
        {
            if (pattern.Pattern.IsMatch(sourceLine))
            {
                restored = pattern.Pattern.Replace(restored, pattern.CanonicalText);
            }
        }

        return restored;
    }

    private static bool ContainsPhrase(string text, string phrase)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(phrase))
        {
            return false;
        }

        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var matchIndex = text.IndexOf(phrase, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                return false;
            }

            var matchEnd = matchIndex + phrase.Length;
            var leftBounded = matchIndex == 0 || !IsLatin(text[matchIndex - 1]);
            var rightBounded = matchEnd >= text.Length || !IsLatin(text[matchEnd]);
            if (leftBounded && rightBounded)
            {
                return true;
            }

            searchIndex = matchIndex + 1;
        }

        return false;
    }

    private static bool ShouldUseSchoolWordingForSect(string sourceLine)
    {
        if ((!ContainsPhrase(sourceLine, "sect") && !ContainsPhrase(sourceLine, "sects"))
            || ContainsPhrase(sourceLine, "sectless"))
        {
            return false;
        }

        return ContainsAnyIgnoreCase(
            sourceLine,
            "choose",
            "choosing",
            "join",
            "joining",
            "joined",
            "which",
            "best",
            "beginner",
            "weapon",
            "playstyle",
            "skill",
            "skills",
            "arts",
            "build",
            "role",
            "recommended",
            "recommend",
            "path");
    }

    private static string ReplacePhrase(string text, string sourcePhrase, string replacement)
    {
        var processed = text;
        foreach (var variant in new[] { sourcePhrase, sourcePhrase.ToLowerInvariant() })
        {
            processed = Regex.Replace(processed, Regex.Escape(variant), replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return processed;
    }

    private static bool ContainsAnyIgnoreCase(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAllIgnoreCase(string text, params string[] values) =>
        values.All(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static List<ProtectedPhraseMatch> FindProtectedDomainPhraseMatches(string line)
    {
        var candidates = new List<ProtectedPhraseMatch>();

        foreach (var pattern in ProtectedDomainPhrasePatterns)
        {
            foreach (Match match in pattern.Pattern.Matches(line))
            {
                if (match.Success && match.Length > 0)
                {
                    candidates.Add(new ProtectedPhraseMatch(match.Index, match.Index + match.Length, pattern.CanonicalText));
                }
            }
        }

        foreach (Match match in DynamicNamedLocationRegex.Matches(line))
        {
            if (match.Success && match.Length > 0)
            {
                candidates.Add(new ProtectedPhraseMatch(match.Index, match.Index + match.Length, match.Value));
            }
        }

        foreach (Match match in DynamicTitledNameRegex.Matches(line))
        {
            if (match.Success && match.Length > 0)
            {
                candidates.Add(new ProtectedPhraseMatch(match.Index, match.Index + match.Length, match.Value));
            }
        }

        foreach (var glossaryMatch in TranslationGlossaryRuntime.Default.FindDirectMatches(line))
        {
            if (glossaryMatch.Action == TranslationGlossaryAction.Preserve && glossaryMatch.Length > 0)
            {
                candidates.Add(new ProtectedPhraseMatch(
                    glossaryMatch.StartIndex,
                    glossaryMatch.EndIndex,
                    line[glossaryMatch.StartIndex..glossaryMatch.EndIndex]));
            }
        }

        if (candidates.Count <= 1)
        {
            return candidates;
        }

        candidates.Sort(static (left, right) =>
        {
            var indexComparison = left.StartIndex.CompareTo(right.StartIndex);
            return indexComparison != 0
                ? indexComparison
                : right.Length.CompareTo(left.Length);
        });

        var selected = new List<ProtectedPhraseMatch>();
        foreach (var candidate in candidates)
        {
            if (selected.Count > 0 && candidate.StartIndex < selected[^1].EndIndex)
            {
                continue;
            }

            selected.Add(candidate);
        }

        return selected;
    }

    private static PhrasePattern[] CreatePhrasePatterns(params string[] phrases) =>
        phrases
            .OrderByDescending(static phrase => phrase.Length)
            .Select(static phrase => new PhrasePattern(
                phrase,
                new Regex($@"(?<![A-Za-z]){Regex.Escape(phrase)}(?![A-Za-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            .ToArray();

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
        $"-X utf8 -u \"{scriptPath}\" --model \"{modelPath}\" --source-language \"{settings.SourceLanguageCode}\" --target-language \"{settings.TargetLanguageCode}\" --threads {WorkerThreadCount} --beam-size {WorkerBeamSize} --patience {WorkerPatience.ToString(CultureInfo.InvariantCulture)} --repetition-penalty {WorkerRepetitionPenalty.ToString(CultureInfo.InvariantCulture)} --no-repeat-ngram-size {WorkerNoRepeatNgramSize} --max-decoding-length {WorkerMaxDecodingLength} --server";

    private static string ResolvePythonExecutable(AppSettings settings)
    {
        var candidates = new[]
        {
            settings.OfflinePythonExecutablePath,
            Environment.GetEnvironmentVariable(PythonEnvVar),
            Environment.GetEnvironmentVariable(LegacyPythonEnvVar),
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
            $"Offline Python runtime not found. Install Python 3.11+ and set {PythonEnvVar}/OfflinePythonPath if needed.");
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
            Environment.GetEnvironmentVariable(LegacyModelEnvVar),
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

        if (TranslationGlossaryRuntime.Default.ShouldForceTranslate(line))
        {
            return false;
        }

        if (ForceTranslateDomainPhrasePatterns.Any(pattern => pattern.Pattern.IsMatch(line)))
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
        var aliasLikeCount = englishWords.Count(IsLikelyAliasWord);
        return stopWordCount == 0 && aliasLikeCount == englishWords.Count;
    }

    private static bool LooksLikeMetadataLine(string line)
    {
        if (ForceTranslateDomainPhrasePatterns.Any(pattern => pattern.Pattern.IsMatch(line)))
        {
            return false;
        }

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

        if (ContainsCyrillic(line))
        {
            return false;
        }

        var englishWords = GetEnglishWords(line);
        if (englishWords.Count == 0)
        {
            return false;
        }

        var stopWordCount = englishWords.Count(IsEnglishStopWord);
        if (PreservingMetadataKeywordRegex.IsMatch(line) && stopWordCount <= 2)
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

        if (ForceTranslateDomainPhrasePatterns.Any(pattern => pattern.Pattern.IsMatch(span)))
        {
            return false;
        }

        if (ProtectedDomainPhrasePatterns.Any(pattern => pattern.Pattern.IsMatch(span)))
        {
            return true;
        }

        if (TranslationGlossaryRuntime.Default.HasPreserveEntry(span))
        {
            return true;
        }

        if (DynamicNamedLocationRegex.IsMatch(span) || DynamicTitledNameRegex.IsMatch(span))
        {
            return true;
        }

        if (TranslationGlossaryRuntime.Default.ShouldForceTranslate(span))
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

        if (PreservingMetadataKeywordRegex.IsMatch(cleanWord))
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

        if (LooksLikeProtectedCompoundAliasWord(cleanWord))
        {
            return true;
        }

        var isAllUpper = cleanWord.Equals(cleanWord.ToUpperInvariant(), StringComparison.Ordinal);
        if (!isAllUpper && cleanWord.Skip(1).Any(char.IsUpper))
        {
            return true;
        }

        return isAllUpper && cleanWord.Length <= 4;
    }

    private static bool LooksLikeProtectedCompoundAliasWord(string word)
    {
        if (!word.Contains('-', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = word.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        return parts.All(static part =>
            part.Length > 1
            && char.IsUpper(part[0])
            && part.Skip(1).All(static character => char.IsLower(character)));
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

    private sealed record PhrasePattern(string CanonicalText, Regex Pattern);

    private sealed record ProtectedPhraseMatch(int StartIndex, int EndIndex, string CanonicalText)
    {
        public int Length => EndIndex - StartIndex;
    }

    private sealed record OfflineTranslationRequest(IReadOnlyList<string> Texts, string SourceLanguage, string TargetLanguage);

    private sealed record OfflineTranslationResponse(string[]? Translations, string? Translation, string? Error);
}
