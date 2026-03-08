from __future__ import annotations

import argparse
import json
import statistics
import string
import time
from dataclasses import dataclass
from difflib import SequenceMatcher
from pathlib import Path

import ctranslate2
from transformers import AutoTokenizer


LANGUAGE_MAP = {
    "ar": "arb_Arab",
    "de": "deu_Latn",
    "en": "eng_Latn",
    "es": "spa_Latn",
    "fr": "fra_Latn",
    "hi": "hin_Deva",
    "it": "ita_Latn",
    "ja": "jpn_Jpan",
    "ko": "kor_Hang",
    "pl": "pol_Latn",
    "pt": "por_Latn",
    "ru": "rus_Cyrl",
    "uk": "ukr_Cyrl",
    "zh": "zho_Hans",
}

DEFAULT_CASES = [
    {
        "category": "slang",
        "source": "the guy singing in hindi has an INSANE vocal range and voice",
        "reference": "парень, который поет на хинди, обладает невероятным вокальным диапазоном и голосом",
    },
    {
        "category": "comment",
        "source": "Dutch metalhead here. This is glorious.",
        "reference": "Я металлист из Нидерландов. Это великолепно.",
    },
    {
        "category": "comment",
        "source": "While it wasnt their cup of tea (not metalheads) there was a glimmer in their eyes.",
        "reference": "Хотя это было не совсем в их вкусе (они не любители метала), в их глазах промелькнула искра.",
    },
    {
        "category": "comment",
        "source": "This song literally makes me bond with them despite being from opposite parts of the planet.",
        "reference": "Эта песня буквально сближает меня с ними, несмотря на то что мы с разных концов планеты.",
    },
    {
        "category": "slang",
        "source": "underrated af",
        "reference": "сильно недооценено",
    },
    {
        "category": "metadata",
        "source": "feat. Kasane Teto",
        "reference": "feat. Kasane Teto",
    },
    {
        "category": "metadata",
        "source": "Hololive EN collab w/ Calli ft. Nerissa",
        "reference": "Hololive EN collab w/ Calli feat. Nerissa",
    },
    {
        "category": "slang",
        "source": "im a 35 yo french woman and this goes hard",
        "reference": "я 35-летняя француженка, и это чертовски мощно",
    },
    {
        "category": "slang",
        "source": "wtf this song slaps",
        "reference": "вот это да, эта песня просто разносит",
    },
    {
        "category": "slang",
        "source": "my bro cooked with this one",
        "reference": "братан тут реально выдал",
    },
    {
        "category": "slang",
        "source": "Banger fr no cap",
        "reference": "реально бэнгер, без шуток",
    },
    {
        "category": "mmorpg",
        "source": "tank lost aggro and the whole raid wiped",
        "reference": "танк потерял агро, и весь рейд вайпнулся",
    },
    {
        "category": "mmorpg",
        "source": "kite the adds and pop your cooldowns",
        "reference": "кайти аддов и прожимай кулдауны",
    },
    {
        "category": "mmorpg",
        "source": "we need more dps for this boss",
        "reference": "нам нужно больше дпса на этого босса",
    },
    {
        "category": "mmorpg",
        "source": "healer is oom after the big pull",
        "reference": "хил без маны после большого пула",
    },
    {
        "category": "mmorpg",
        "source": "farm this dungeon for better gear and loot",
        "reference": "фарми этот данж ради лучшего гира и лута",
    },
    {
        "category": "mmorpg",
        "source": "cc the trash mobs before we pull the boss",
        "reference": "дай контроль по треш-мобам перед пулом босса",
    },
    {
        "category": "mmorpg",
        "source": "this build is BiS for PvE but trash in PvP",
        "reference": "этот билд BiS для PvE, но мусор в PvP",
    },
    {
        "category": "mmorpg",
        "source": "the proc rate on this trinket is insane",
        "reference": "шанс прока на этом тринкете просто безумный",
    },
    {
        "category": "mixed",
        "source": "Это track вообще goes hard, Calli feat. Nerissa прям тащит",
        "reference": "Этот трек вообще звучит мощно, Calli feat. Nerissa прям тащит",
    },
]


@dataclass(frozen=True)
class CandidateConfig:
    name: str
    beam_size: int
    patience: float = 1.0
    repetition_penalty: float = 1.0
    no_repeat_ngram_size: int = 0
    replace_unknowns: bool = False
    disable_unk: bool = False
    max_decoding_length: int = 192


CANDIDATE_CONFIGS = [
    CandidateConfig("greedy", beam_size=1),
    CandidateConfig("beam2", beam_size=2),
    CandidateConfig("beam2_guarded", beam_size=2, repetition_penalty=1.08, no_repeat_ngram_size=3),
    CandidateConfig("beam2_names", beam_size=2, repetition_penalty=1.05, no_repeat_ngram_size=3, replace_unknowns=True, disable_unk=True),
    CandidateConfig("beam3_names", beam_size=3, repetition_penalty=1.08, no_repeat_ngram_size=3, replace_unknowns=True, disable_unk=True),
]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--source-language", default="en")
    parser.add_argument("--target-language", default="ru")
    parser.add_argument("--json-out")
    return parser.parse_args()


def map_language_code(language_code: str) -> str:
    normalized = (language_code or "").strip().lower()
    if normalized in LANGUAGE_MAP:
        return LANGUAGE_MAP[normalized]
    if "_" in normalized and len(normalized) >= 8:
        return normalized
    raise ValueError(f"Unsupported NLLB language code: {language_code}")


def load_runtime(model_path: Path, source_language: str, target_language: str) -> tuple[AutoTokenizer, ctranslate2.Translator, str, str]:
    tokenizer = AutoTokenizer.from_pretrained(
        model_path.as_posix(),
        local_files_only=True,
        use_fast=False,
    )
    translator = ctranslate2.Translator(
        model_path.as_posix(),
        device="cpu",
        inter_threads=1,
        intra_threads=2,
    )
    return tokenizer, translator, map_language_code(source_language), map_language_code(target_language)


def encode_text(tokenizer: AutoTokenizer, text: str, source_language: str) -> list[str]:
    tokenizer.src_lang = source_language
    token_ids = tokenizer.encode(text, truncation=True, max_length=512)
    return tokenizer.convert_ids_to_tokens(token_ids)


def translate_texts(
    texts: list[str],
    tokenizer: AutoTokenizer,
    translator: ctranslate2.Translator,
    source_language: str,
    target_language: str,
    config: CandidateConfig,
) -> list[str]:
    token_batches = [encode_text(tokenizer, text, source_language) for text in texts]
    results = translator.translate_batch(
        token_batches,
        target_prefix=[[target_language]] * len(token_batches),
        beam_size=config.beam_size,
        patience=config.patience,
        max_batch_size=min(len(token_batches), 8),
        repetition_penalty=config.repetition_penalty,
        no_repeat_ngram_size=config.no_repeat_ngram_size,
        replace_unknowns=config.replace_unknowns,
        disable_unk=config.disable_unk,
        max_decoding_length=config.max_decoding_length,
    )

    translations: list[str] = []
    for result in results:
        target_ids = tokenizer.convert_tokens_to_ids(result.hypotheses[0])
        translations.append(tokenizer.decode(target_ids, skip_special_tokens=True).strip())
    return translations


def normalize_for_score(text: str) -> list[str]:
    translation_table = str.maketrans({character: " " for character in string.punctuation + "«»…"})
    normalized = text.lower().translate(translation_table)
    return [part for part in normalized.split() if part]


def score_translation(output: str, reference: str) -> float:
    sequence_ratio = SequenceMatcher(None, output.lower(), reference.lower()).ratio()

    output_tokens = normalize_for_score(output)
    reference_tokens = normalize_for_score(reference)
    if not output_tokens and not reference_tokens:
        token_score = 1.0
    elif not output_tokens or not reference_tokens:
        token_score = 0.0
    else:
        output_set = set(output_tokens)
        reference_set = set(reference_tokens)
        overlap = len(output_set & reference_set)
        precision = overlap / len(output_set)
        recall = overlap / len(reference_set)
        token_score = 0.0 if precision + recall == 0 else (2 * precision * recall) / (precision + recall)

    return (sequence_ratio * 0.55) + (token_score * 0.45)


def summarize_case(case: dict, output: str) -> dict:
    reference = case["reference"]
    return {
        "category": case["category"],
        "source": case["source"],
        "reference": reference,
        "output": output,
        "score": round(score_translation(output, reference), 4),
    }


def main() -> int:
    args = parse_args()
    model_path = Path(args.model)
    tokenizer, translator, source_language, target_language = load_runtime(
        model_path=model_path,
        source_language=args.source_language,
        target_language=args.target_language,
    )

    corpus = DEFAULT_CASES
    sources = [case["source"] for case in corpus]
    results: list[dict] = []

    for config in CANDIDATE_CONFIGS:
        start = time.perf_counter()
        translations = translate_texts(sources, tokenizer, translator, source_language, target_language, config)
        elapsed_ms = (time.perf_counter() - start) * 1000

        case_results = [summarize_case(case, output) for case, output in zip(corpus, translations, strict=True)]
        score_values = [item["score"] for item in case_results]
        results.append(
            {
                "config": config.__dict__,
                "latency_ms": round(elapsed_ms, 2),
                "score_avg": round(statistics.fmean(score_values), 4),
                "score_min": round(min(score_values), 4),
                "cases": case_results,
            }
        )

    ranked = sorted(results, key=lambda item: (-item["score_avg"], item["latency_ms"]))
    payload = {
        "model": model_path.as_posix(),
        "ranked": ranked,
    }

    print(json.dumps(payload, ensure_ascii=False, indent=2))
    if args.json_out:
        Path(args.json_out).write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
