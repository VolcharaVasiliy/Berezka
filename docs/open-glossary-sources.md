# Open Glossary Sources

Date: `2026-03-09`

## Integrated Runtime Sources

### 1. Kaikki / Wiktionary raw dictionary dumps

Links:
- https://kaikki.org/dictionary/rawdata.html
- https://kaikki.org/dictionary/English/words/kaikki.org-dictionary-English-words.jsonl.gz
- https://kaikki.org/dictionary/Russian/words/kaikki.org-dictionary-Russian-words.jsonl.gz

Why this source:
- It is large enough to act as a real lexical database, not a toy glossary.
- The raw data carries tags/topics/categories that make filtering possible for `slang`, `internet`, `gaming`, and `video-games`.
- It is much safer to derive a filtered runtime hint layer from this than to keep adding hardcoded `if` branches forever.

How Berezka uses it:
- `scripts/build_open_glossaries.py` streams the English dump and extracts high-value `gaming`, `internet`, and `slang` hint entries.
- These become:
  - `Berezka.App/Resources/Glossaries/kaikki-gaming-hints.en.json`
  - `Berezka.App/Resources/Glossaries/kaikki-internet-hints.en.json`
  - `Berezka.App/Resources/Glossaries/kaikki-slang-hints.en.json`
- The app loads them through `TranslationGlossaryRuntime` and uses them as a force-translate hint layer, so short gamer/internet terms are less likely to be misclassified as metadata or aliases.

### 2. Berezka curated runtime glossary

Source file:
- `scripts/build_open_glossaries.py`

Why this source exists:
- Raw open dictionaries are good at term discovery, but weak at enforcing the exact Russian wording that players actually expect.
- MMO slang and `Where Winds Meet` terminology need a second layer where Berezka can say:
  - translate this to a specific Russian gamer form
  - preserve this exact place/NPC/sect name

How Berezka uses it:
- `scripts/build_open_glossaries.py` writes `Berezka.App/Resources/Glossaries/berezka-curated-runtime.en-ru.json`.
- This file contains:
  - exact `EN -> RU` slang overrides like `aggro -> агро`, `noob -> нуб`, `buff -> баф`
  - preserve rules for names like `Taiping Mausoleum`, `Qin Caiwei`, `Mistveil City`
  - `Where Winds Meet` lore/system terms such as `Jianghu`, `Wuxia`, `Inner Path`, `Gift of Gab`
  - UI/problem lines such as `Cloud Mail`, `Cultivation`, and `Reward Highlights`

## Researched But Not Bundled As Runtime Data

### OPUS OpenSubtitles

Link:
- https://opus.nlpl.eu/OpenSubtitles.php

Why it matters:
- Huge conversational parallel corpus, useful for discovering phrase-level slang and colloquial translation patterns.

Why it is not bundled directly:
- It is too noisy for deterministic runtime glossary use.
- It is much better as an offline mining/research source than as a shipped in-app dictionary.

### OPUS Tatoeba

Link:
- https://opus.nlpl.eu/Tatoeba.php

Why it matters:
- Clean bilingual sentence pairs are useful for mining compact phrase examples and validating short colloquial translations.

Why it is not bundled directly:
- Sentence-pair corpora are still too large and too example-oriented for direct runtime lookup inside Berezka.
- For runtime quality, curated/exact glossary terms plus the local NLLB model are a better latency/precision tradeoff.

### Where Winds Meet terminology references

Links:
- https://www.wherewindsmeetgame.org/terminology
- `docs/translation-domain-glossary.md`

Why it matters:
- `Where Winds Meet` mixes English UI text with culturally loaded Chinese-fantasy vocabulary, romanized names, and wuxia-specific terms.

How it is used:
- The terminology and naming rules were folded into the curated Berezka runtime glossary and into existing translation post-processing rules.

## Practical Result

The runtime now uses a layered approach:
- Kaikki/Wiktionary hint databases: broad detection of slang/internet/gaming terms
- Berezka curated database: exact output or preserve behavior for high-value terms
- Existing model/post-processing layer: full-sentence translation and contextual cleanup

This is intentionally narrower than shipping raw corpora, but much better for latency, determinism, and maintainability.
