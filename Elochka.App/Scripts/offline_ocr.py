from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Iterable

os.environ.setdefault("FLAGS_allocator_strategy", "auto_growth")
os.environ.setdefault("OMP_NUM_THREADS", "1")
os.environ.setdefault("MKL_NUM_THREADS", "1")
os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")

from paddleocr import PaddleOCR

if hasattr(sys.stdin, "reconfigure"):
    sys.stdin.reconfigure(encoding="utf-8")
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lang", default="ru")
    parser.add_argument("--server", action="store_true")
    return parser.parse_args()


def normalize_input(raw_text: str) -> str:
    return raw_text.lstrip("\ufeff").strip()


def load_runtime(args: argparse.Namespace) -> PaddleOCR:
    primary_options = {
        "lang": args.lang,
        "use_doc_orientation_classify": False,
        "use_doc_unwarping": False,
        "use_textline_orientation": False,
    }
    legacy_options = {
        "lang": args.lang,
        "use_angle_cls": False,
    }

    for options in (primary_options, legacy_options):
        try:
            return PaddleOCR(**options)
        except (TypeError, ValueError) as exc:
            message = str(exc)
            if "Unknown argument" not in message and "unexpected keyword argument" not in message:
                raise

    return PaddleOCR(lang=args.lang)


def extract_texts(payload) -> list[str]:
    texts: list[str] = []

    if payload is None:
        return texts

    if isinstance(payload, str):
        value = payload.strip()
        if value:
            texts.append(value)
        return texts

    if isinstance(payload, dict):
        for key in ("rec_texts", "texts", "text"):
            value = payload.get(key)
            if isinstance(value, list):
                for item in value:
                    texts.extend(extract_texts(item))
                return texts
            if isinstance(value, str):
                texts.extend(extract_texts(value))
                return texts

        for value in payload.values():
            texts.extend(extract_texts(value))
        return texts

    if isinstance(payload, (list, tuple)):
        if len(payload) >= 2 and isinstance(payload[1], (list, tuple)) and payload[1]:
            first = payload[1][0]
            if isinstance(first, str):
                texts.extend(extract_texts(first))
                return texts

        for item in payload:
            texts.extend(extract_texts(item))
        return texts

    return texts


def run_ocr(ocr: PaddleOCR, image_path: str) -> str:
    image = Path(image_path)
    if not image.is_file():
        raise FileNotFoundError(f"OCR image file not found: {image}")

    if hasattr(ocr, "predict"):
        result = ocr.predict(str(image))
    else:
        result = ocr.ocr(str(image), cls=False)

    if not isinstance(result, (list, tuple, dict, str)):
        result = list(result)

    texts = [value for value in extract_texts(result) if value]
    deduped_lines = list(dict.fromkeys(texts))
    return "\n".join(deduped_lines).strip()


def write_response(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False))
    sys.stdout.write("\n")
    sys.stdout.flush()


def process_request(request_line: str, ocr: PaddleOCR) -> dict:
    payload = json.loads(normalize_input(request_line))
    image_path = payload.get("ImagePath") or payload.get("image_path") or ""
    return {"Text": run_ocr(ocr, str(image_path))}


def run_server(ocr: PaddleOCR) -> int:
    for request_line in sys.stdin:
        request_line = normalize_input(request_line)
        if not request_line:
            continue

        try:
            response = process_request(request_line, ocr)
        except Exception as exc:  # pragma: no cover - runtime bridge
            response = {"Text": "", "Error": str(exc)}

        write_response(response)

    return 0


def run_single(ocr: PaddleOCR) -> int:
    request_line = normalize_input(sys.stdin.read())
    response = process_request(request_line, ocr)
    write_response(response)
    return 0


def main() -> int:
    args = parse_args()
    ocr = load_runtime(args)
    if args.server:
        return run_server(ocr)

    return run_single(ocr)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # pragma: no cover - runtime bridge
        print(str(exc), file=sys.stderr)
        raise SystemExit(1)
