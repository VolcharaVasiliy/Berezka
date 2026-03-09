# Report - berezka - 2026-03-09

## Summary
- Completed the full internal rename from `Elochka` to `Berezka` across folders, project files, namespaces, classes, scripts, and docs.
- Kept only intentional legacy compatibility points for migrating old `Elochka` settings/env vars.
- Restored local NLLB and PaddleOCR runtime assets on this machine.
- Built local release artifacts matching the GitHub release shape, under the new naming.
- Benchmarked low-cost NLLB/CTranslate2 decoding profiles for slang-heavy and mixed-language cases, then switched the runtime translator from greedy decode to a guarded beam-search profile.
- Tightened translation normalization/post-processing for slang, abbreviations, aliases, and mixed `RU/EN` lines without moving to a heavier model.
- Added a separate domain layer for gaming/MMORPG slang so common in-game jargon is translated into Russian gamer vocabulary instead of literal prose.

## Files
- `Berezka.sln` - canonical renamed solution file.
- `Berezka.App/` - renamed app project root; namespaces updated to `Berezka.App.*`.
- `Berezka.App/Application/BerezkaApplicationContext.cs` - tray hotkey switching remains active after the total rename.
- `Berezka.App/Berezka.App.csproj` - renamed app project file.
- `Berezka.App/BerezkaPaths.cs` - renamed path helper; `BEREZKA_*` env vars added with fallback to old `ELOCHKA_*`.
- `Berezka.App/Program.cs` - entry point updated to `BerezkaApplicationContext`.
- `Berezka.App/Services/PaddleOcrTextService.cs` - switched runtime env var names to `BEREZKA_*` with legacy fallback.
- `Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs` - switched runtime env var names to `BEREZKA_*` with legacy fallback.
- `Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs` - enabled the selected guarded beam-search decode profile and improved normalization/post-fixes for `wtf/slaps`, `cooked with this one`, `banger fr no cap`, `track`, mixed `RU/EN`, and `vocal range and voice`.
- `Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs` - added gaming/MMORPG-specific source normalization and post-processing for `aggro`, `wipe`, `kite`, `adds`, `cooldowns`, `dps`, `oom`, `gear`, `loot`, `build/BiS`, `proc`, `PvE/PvP`, and dungeon/raid phrasing.
- `Berezka.App/Scripts/offline_translate.py` - added decoder CLI options so the C# runtime can control beam search, repetition penalty, no-repeat ngrams, and decode length.
- `Berezka.Installer/` - renamed installer project root and namespaces to `Berezka.Installer`.
- `Berezka.Installer/Berezka.Installer.csproj` - renamed installer project file.
- `.gitignore` - ignore paths updated from `Elochka.*` to `Berezka.*`.
- `README.md` - project title reduced to `Berezka`; build/run paths updated.
- `TRANSFER_README.md` - updated source tree path and environment variable names to `BEREZKA_*`.
- `scripts/benchmark_translation_configs.py` - reusable local benchmark that compares several cheap NLLB/CTranslate2 decode profiles on slang, metadata, and mixed-language examples.
- `scripts/benchmark_translation_configs.py` - extended with gaming/MMORPG corpus entries so future model/config changes can be checked against in-game terminology as well.
- `scripts/build_release.ps1` - project paths updated to `Berezka.*`; package output verified as `berezka-<tag>-win-x64_7z_lzma2_mx5_solid.7z`.
- `scripts/build_installer.ps1` - installer project paths updated to `Berezka.*`; installer output verified as `Berezka.Setup-<tag>-win-x64.exe`.
- `scripts/setup_local_nllb.ps1` - updated Python env var handling to prefer `BEREZKA_PYTHON`.
- `scripts/setup_local_opus_mt.ps1` - updated Python env var handling to prefer `BEREZKA_PYTHON`.
- `scripts/setup_local_paddle_ocr.ps1` - added pip timeout/retry hardening and renamed bootstrap temp file to `berezka_bootstrap_paddle_ocr.py`.

## Rationale
- The previous pass renamed only outward branding; this pass removed the internal `Elochka.*` project and namespace drift so the repo state matches the product name.
- Legacy compatibility was kept only where it materially helps migration: old settings path and old env vars.
- The latest GitHub releases use a two-asset shape, so the local build was aligned to that same structure under the new `Berezka` naming.
- The current `LocalNllb` runtime was still using greedy decode (`beam=1`), which is the cheapest mode but leaves obvious quality on the table for slang-heavy comments and informal phrases.
- A small benchmark on the local `nllb-200-distilled-600m-ctranslate2` model showed the best low-cost profile was `beam=2`, `repetition_penalty=1.08`, `no_repeat_ngram_size=3`; it beat greedy quality without the larger latency jump of `beam=3`.
- The next quality ceiling was not beam search but domain language: MMO jargon like `aggro`, `wipe`, `adds`, `oom`, `pull`, `BiS`, and `proc rate` was still collapsing into literal Russian. That required a domain glossary layer on top of the model, not just another decoder tweak.

## Issues
- `scripts/build_installer.ps1` was first launched in parallel with `build_release.ps1` and failed because the `release` folder did not exist yet; rerunning it after the archive build succeeded.
- Raw NLLB quality is still limited on niche slang and names; the latest pass improves this with cheaper decode settings and targeted phrase normalization, not by switching to a heavier model.
- GitHub later confirmed that the repository moved to `VolcharaVasiliy/Berezka`, so installer/release links must now target the new slug instead of the legacy `elochka-rewrite` redirect.

## Functions
- `CreateHotKeyMenuItem` (`Berezka.App/Application/BerezkaApplicationContext.cs`) - builds the tray submenu for hotkey selection.
- `ChangeHotKeyMode` (`Berezka.App/Application/BerezkaApplicationContext.cs`) - applies, persists, and refreshes the selected tray hotkey.
- `TryRegisterHotKey` (`Berezka.App/Application/BerezkaApplicationContext.cs`) - central hotkey registration with rollback-safe error handling.
- `ResolvePaddlexCacheHome` (`Berezka.App/BerezkaPaths.cs`) - resolves `BEREZKA_PADDLEX_CACHE_HOME` with fallback to the legacy env var.
- `ResolvePythonExecutable` (`Berezka.App/Services/PaddleOcrTextService.cs`) - prefers `BEREZKA_PYTHON`, falls back to `ELOCHKA_PYTHON`.
- `ResolveModelPath` (`Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs`) - prefers `BEREZKA_OFFLINE_MODEL`, falls back to `ELOCHKA_OFFLINE_MODEL`.
- `NormalizeSourceForTranslation` (`Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs`) - now normalizes more informal/slang inputs before they hit NLLB.
- `PostProcessTranslation` (`Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs`) - now fixes several persistent literal/slang failures after model decode.
- `BuildArguments` (`Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs`) - now passes the selected guarded beam-search profile into the Python worker.
- `ApplyGamingDomainSourceNormalization` (`Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs`) - rewrites gaming/MMORPG slang into model-friendlier English source forms.
- `ApplyGamingDomainPostProcessing` (`Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs`) - converts literal outputs back into Russian gamer terminology and phrase-level MMO wording.

## Release Assets
- `release\\berezka-v0.1.2-win-x64_7z_lzma2_mx5_solid.7z`
- `release\\Berezka.Setup-v0.1.2-win-x64.exe`

## Next steps
- The source rename and release scripts are ready for the user to publish the new `Berezka` release on GitHub.
- If the GitHub repository itself is renamed later, only the default `Repository` value in `scripts/build_installer.ps1` will still need adjustment.
- If the user still finds recurring misses in a narrow domain, extend the normalization corpus with those exact phrases before considering a heavier local model.
- The current domain layer is tuned for common MMO chatter; if the target game has its own jargon, the next pass should be built from fresh examples from that specific game rather than generic MMO terms.

## Task Update - 2026-03-09 Capitalized words and place-name preservation
- Relaxed one preservation heuristic in `Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs` so generic capitalized metadata-like words are not automatically frozen as aliases.
- This fixes UI-style headings such as `VERSION 1.4`, which now flow through translation/post-processing instead of being preserved verbatim.
- Expanded the dynamic preserve regex for named places so location/structure names ending in `Temple`, `Village`, `Mausoleum`, `Sanctum`, `Pagoda`, `Keep`, `Outpost`, `Ruins`, and similar suffixes stay in original English.
- Removed a stale invalid `Regex.Replace` branch in the `Meridian Touch` post-processing path that could throw `RegexParseException` at runtime.

## Functions - 2026-03-09 Capitalized words and place-name preservation
- `ShouldPreserveEnglishSpan` (`Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs`) - keeps protected named locations and titled names in English while allowing generic capitalized words to be translated.
- `IsLikelyAliasWord` (`Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs`) - now treats only preserving metadata tokens as alias-like instead of freezing generic `version`-style words.
- `ApplyWhereWindsMeetPostProcessing` (`Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs`) - no longer contains the broken runtime regex in the `Meridian Touch` branch.

## Verification - 2026-03-09 Capitalized words and place-name preservation
- `dotnet build F:\Projects\berezka\Berezka.sln -nologo`
- `dotnet run --project F:\Projects\berezka\.tmp\ProviderHarness\ProviderHarness.csproj`
- Spot checks:
  - `VERSION 1.4` -> `Версия 1.4`
  - `Taiping Mausoleum` -> `Taiping Mausoleum`
  - `Can't buy Mystic Skills Box from Taiping Mausoleum anymore?` -> location name preserved in English
  - `Use Wind Sense near Mistveil City, then distract Qin Caiwei with Meridian Touch` -> protected names remain intact and the provider no longer crashes

## Task Update - 2026-03-09 Domain glossary research
- Researched Reddit/community slang and public guide terminology for `ArcheAge`, `Black Desert`, `The Quinfall`, and `Where Winds Meet`.
- Added `F:\Projects\berezka\docs\translation-domain-glossary.md` as a reusable glossary for future translation normalization, preserve-lists, and benchmark expansion.
- Captured shared MMO slang, game-specific jargon, and `Where Winds Meet` wuxia/jianghu terminology, plus proper-name preserve rules and high-risk translation traps.
- Kept this pass documentation-first so the next integration step can cherry-pick high-value terms instead of blindly hardcoding every scraped word into runtime rules.

## Verification - Domain glossary
- Web review of Reddit/community and public guide pages for the four target games.
- File review: `F:\Projects\berezka\docs\translation-domain-glossary.md`

## Task Update - 2026-03-09 GitHub repo move alignment
- GitHub push confirmed the repository now lives at `https://github.com/VolcharaVasiliy/Berezka`.
- Updated the local git remote to the new slug.
- Updated `scripts/build_installer.ps1` so future installer manifests default to `Repository = "Berezka"` instead of the legacy slug.
- Rebuilt installer metadata against the new repository location so release/download links no longer rely on redirect behavior.

## Verification
- `dotnet build F:\Projects\berezka\Berezka.sln`
- `powershell -NoProfile -ExecutionPolicy Bypass -File F:\Projects\berezka\scripts\setup_local_nllb.ps1 -ProjectRoot F:\Projects\berezka -PythonExe F:\DevTools\Python311\python.exe`
- `powershell -NoProfile -ExecutionPolicy Bypass -File F:\Projects\berezka\scripts\setup_local_paddle_ocr.ps1 -ProjectRoot F:\Projects\berezka -PythonExe F:\DevTools\Python311\python.exe`
- `powershell -NoProfile -ExecutionPolicy Bypass -File F:\Projects\berezka\scripts\build_release.ps1 -ProjectRoot F:\Projects\berezka -Version v0.1.2`
- `powershell -NoProfile -ExecutionPolicy Bypass -File F:\Projects\berezka\scripts\build_installer.ps1 -ProjectRoot F:\Projects\berezka -Tag v0.1.2`
- Smoke test: `F:\Projects\berezka\release\berezka-v0.1.2-win-x64\Berezka.App.exe`
- Smoke test: `F:\Projects\berezka\release\Berezka.Setup-v0.1.2-win-x64.exe`
- `F:\DevTools\Python311\python.exe F:\Projects\berezka\scripts\benchmark_translation_configs.py --model F:\Projects\berezka\Models\nllb-200-distilled-600m-ctranslate2`
- `F:\DevTools\Python311\python.exe F:\Projects\berezka\scripts\benchmark_translation_configs.py --model F:\Projects\berezka\Models\nllb-200-distilled-600m-ctranslate2 --json-out F:\Projects\berezka\.tmp\translation-benchmark-gaming.json`
- Runtime harness against the built app provider on sample phrases:
  - `wtf this song slaps` -> `Ух, эта песня звучит невероятно.`
  - `my bro cooked with this one` -> `Братан тут реально выдал.`
  - `Banger fr no cap` -> `Реальный бэнгер, без шуток.`
  - `Это track вообще goes hard, Calli feat. Nerissa прям тащит` -> `Это трек вообще звучит чрезвычайно мощно, Calli feat. Nerissa прям тащит`
  - `the guy singing in hindi has an INSANE vocal range and voice` -> `У парня, который поет на хинди, невероятный вокальный диапазон и голос.`
  - `tank lost aggro and the whole raid wiped` -> `Танк потерял агро, и весь рейд вайпнулся.`
  - `kite the adds and pop your cooldowns` -> `Кайти аддов и прожимай кулдауны.`
  - `healer is oom after the big pull` -> `Хил без маны после большого пула.`
  - `farm this dungeon for better gear and loot` -> `Фарми этот данж ради лучшего гира и лута.`

## Task Update - 2026-03-09 Open glossary databases and v0.1.4 release
- Added open-data glossary generation via `scripts/build_open_glossaries.py`.
- The generator now builds four runtime glossary resources in `Berezka.App/Resources/Glossaries`:
  - `berezka-curated-runtime.en-ru.json`
  - `kaikki-gaming-hints.en.json`
  - `kaikki-internet-hints.en.json`
  - `kaikki-slang-hints.en.json`
- Added `Berezka.App/Services/Translation/TranslationGlossaryRuntime.cs` to load those resources once and expose fast phrase/hint matching at runtime.
- Integrated the glossary runtime into `LocalNllbTranslationProvider`:
  - direct glossary matches can now bypass the model for exact short spans
  - preserve rules for names/locations can now come from the runtime glossary, not only from hardcoded regex arrays
  - hint terms now stop preserve heuristics from incorrectly freezing gamer slang or internet shorthand as metadata
- Added source documentation at `docs/open-glossary-sources.md`.
- Built new local release artifacts:
  - `release\\berezka-v0.1.4-win-x64_7z_lzma2_mx5_solid.7z`
  - `release\\Berezka.Setup-v0.1.4-win-x64.exe`

## Functions - 2026-03-09 Open glossary databases and v0.1.4 release
- `build_open_glossaries.py` (`scripts/build_open_glossaries.py`) - extracts Kaikki/Wiktionary hint terms and emits runtime glossary JSON files plus curated overrides.
- `FindDirectMatches` (`Berezka.App/Services/Translation/TranslationGlossaryRuntime.cs`) - tokenizes an English span and returns exact glossary matches for preserve or direct replacement.
- `ShouldForceTranslate` (`Berezka.App/Services/Translation/TranslationGlossaryRuntime.cs`) - checks whether a line/span contains hint terms that should not be preserved as metadata/aliases.
- `BuildPiecesForEnglishSpan` (`Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs`) - splits English OCR spans into glossary-direct and model-translated pieces.
- `BuildPiecesForPlainEnglishSlice` (`Berezka.App/Services/Translation/LocalNllbTranslationProvider.cs`) - preserves whitespace boundaries while still allowing the core slice to be translated or preserved.

## Verification - 2026-03-09 Open glossary databases and v0.1.4 release
- `F:\DevTools\Python311\python.exe -X utf8 F:\Projects\berezka\scripts\build_open_glossaries.py`
- `dotnet build F:\Projects\berezka\Berezka.sln -nologo`
- `dotnet run --project F:\Projects\berezka\.tmp\ProviderHarness\ProviderHarness.csproj`
- Harness spot checks:
  - `Cultivation!` -> `�����������!`
  - `Cloud Mail` -> `�������� �����`
  - `GG` -> `GG`
  - `noob` -> `���`
  - `aggro` -> `����`
  - `cooldown` -> `�������`
  - `Taiping Mausoleum` -> `Taiping Mausoleum`
- `powershell -NoProfile -ExecutionPolicy Bypass -File F:\Projects\berezka\scripts\build_release.ps1 -ProjectRoot F:\Projects\berezka -Version v0.1.4`
- `powershell -NoProfile -ExecutionPolicy Bypass -File F:\Projects\berezka\scripts\build_installer.ps1 -ProjectRoot F:\Projects\berezka -Tag v0.1.4`
- Smoke test:
  - `release\\berezka-v0.1.4-win-x64\\Berezka.App.exe`
  - `release\\Berezka.Setup-v0.1.4-win-x64.exe`

## Task Update - 2026-03-09 Berezka v0.1.4 push and GitHub release
- Committed glossary/runtime/release updates in `F:\Projects\berezka` as commit `e6bb2b6` (`Add open glossary runtime data for slang and MMO terms`).
- Pushed `main` to `origin` and published tag `v0.1.4`.
- Created GitHub release without assets at `https://github.com/VolcharaVasiliy/Berezka/releases/tag/v0.1.4`.
- Left release assets local only as requested:
  - `F:\Projects\berezka\release\berezka-v0.1.4-win-x64_7z_lzma2_mx5_solid.7z`
  - `F:\Projects\berezka\release\Berezka.Setup-v0.1.4-win-x64.exe`

## Verification - 2026-03-09 Berezka v0.1.4 push and GitHub release
- `F:\DevTools\Portable\MinGit\cmd\git.exe -C F:\Projects\berezka push origin main`
- `F:\DevTools\Portable\MinGit\cmd\git.exe -C F:\Projects\berezka tag -a v0.1.4 -m "Berezka v0.1.4"`
- `F:\DevTools\Portable\MinGit\cmd\git.exe -C F:\Projects\berezka push origin v0.1.4`
- GitHub API verification:
  - release URL: `https://github.com/VolcharaVasiliy/Berezka/releases/tag/v0.1.4`
  - asset count: `0`
  - repo status: clean after push
