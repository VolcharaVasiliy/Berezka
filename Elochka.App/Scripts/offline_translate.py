from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

os.environ.setdefault("OMP_NUM_THREADS", "1")

import ctranslate2
from transformers import AutoTokenizer

if hasattr(sys.stdin, "reconfigure"):
    sys.stdin.reconfigure(encoding="utf-8")
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")


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


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--source-language", default="en")
    parser.add_argument("--target-language", default="ru")
    parser.add_argument("--threads", type=int, default=1)
    parser.add_argument("--beam-size", type=int, default=3)
    parser.add_argument("--server", action="store_true")
    return parser.parse_args()


def normalize_input(raw_text: str) -> str:
    return raw_text.lstrip("\ufeff").strip()


def map_language_code(language_code: str) -> str:
    normalized = (language_code or "").strip().lower()
    if normalized in LANGUAGE_MAP:
        return LANGUAGE_MAP[normalized]

    if "_" in normalized and len(normalized) >= 8:
        return normalized

    raise ValueError(f"Unsupported NLLB language code: {language_code}")


def load_runtime(args: argparse.Namespace) -> tuple[AutoTokenizer, ctranslate2.Translator, str, str]:
    model_path = Path(args.model)
    if not model_path.is_dir():
        raise FileNotFoundError(f"Offline model directory not found: {model_path}")

    tokenizer = AutoTokenizer.from_pretrained(
        model_path.as_posix(),
        local_files_only=True,
        use_fast=False,
    )
    source_language = map_language_code(args.source_language)
    target_language = map_language_code(args.target_language)

    translator = ctranslate2.Translator(
        model_path.as_posix(),
        device="cpu",
        inter_threads=1,
        intra_threads=max(1, args.threads),
    )
    return tokenizer, translator, source_language, target_language


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
    args: argparse.Namespace,
) -> list[str]:
    normalized_texts = [text.strip() for text in texts]
    results = [""] * len(normalized_texts)

    indexes_to_translate: list[int] = []
    token_batches: list[list[str]] = []

    for index, text in enumerate(normalized_texts):
        if not text:
            continue

        indexes_to_translate.append(index)
        token_batches.append(encode_text(tokenizer, text, source_language))

    if indexes_to_translate:
        target_prefix = [[target_language]] * len(token_batches)
        translation_results = translator.translate_batch(
            token_batches,
            target_prefix=target_prefix,
            beam_size=max(1, args.beam_size),
            max_batch_size=max(1, min(len(token_batches), 8)),
        )

        for result_index, translation_result in enumerate(translation_results):
            target_tokens = translation_result.hypotheses[0]
            target_ids = tokenizer.convert_tokens_to_ids(target_tokens)
            results[indexes_to_translate[result_index]] = tokenizer.decode(
                target_ids,
                skip_special_tokens=True,
            ).strip()

    return results


def build_request_payload(raw_payload: dict) -> list[str]:
    texts = raw_payload.get("Texts")
    if isinstance(texts, list):
        return [str(item) for item in texts]

    text = raw_payload.get("Text") or raw_payload.get("text") or ""
    return [str(text)]


def write_response(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False))
    sys.stdout.write("\n")
    sys.stdout.flush()


def process_request(
    request_text: str,
    tokenizer: AutoTokenizer,
    translator: ctranslate2.Translator,
    source_language: str,
    target_language: str,
    args: argparse.Namespace,
) -> dict:
    raw_payload = json.loads(normalize_input(request_text))
    texts = build_request_payload(raw_payload)
    translations = translate_texts(texts, tokenizer, translator, source_language, target_language, args)
    return {"Translations": translations, "Translation": translations[0] if len(translations) == 1 else None}


def run_server(
    tokenizer: AutoTokenizer,
    translator: ctranslate2.Translator,
    source_language: str,
    target_language: str,
    args: argparse.Namespace,
) -> int:
    for request_line in sys.stdin:
        request_line = normalize_input(request_line)
        if not request_line:
            continue

        try:
            response = process_request(request_line, tokenizer, translator, source_language, target_language, args)
        except Exception as exc:  # pragma: no cover - runtime bridge
            response = {"Translations": [], "Translation": "", "Error": str(exc)}

        write_response(response)

    return 0


def run_single(
    tokenizer: AutoTokenizer,
    translator: ctranslate2.Translator,
    source_language: str,
    target_language: str,
    args: argparse.Namespace,
) -> int:
    request_text = normalize_input(sys.stdin.read())
    response = process_request(request_text, tokenizer, translator, source_language, target_language, args)
    write_response(response)
    return 0


def main() -> int:
    args = parse_args()
    tokenizer, translator, source_language, target_language = load_runtime(args)
    if args.server:
        return run_server(tokenizer, translator, source_language, target_language, args)

    return run_single(tokenizer, translator, source_language, target_language, args)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # pragma: no cover - runtime bridge
        print(str(exc), file=sys.stderr)
        raise SystemExit(1)
