from __future__ import annotations

import argparse
import gzip
import json
import re
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable


ROOT = Path(r"F:\Projects\berezka")
KAIKKI_ROOT = ROOT / ".cache" / "glossary-sources" / "kaikki"
OUTPUT_ROOT = ROOT / "Berezka.App" / "Resources" / "Glossaries"

ENGLISH_DUMP = KAIKKI_ROOT / "english.jsonl.gz"
RUSSIAN_DUMP = KAIKKI_ROOT / "russian.jsonl.gz"

TOKEN_WORD_RE = re.compile(r"[A-Za-z][A-Za-z0-9_'.&/+:-]*")
MULTI_SPACE_RE = re.compile(r"\s{2,}")

GAMING_TOPIC_MARKERS = {
    "video-games",
    "gaming",
    "mmorpg",
    "role-playing-games",
    "role-playing games",
}

INTERNET_TAG_MARKERS = {
    "internet",
    "texting",
    "online",
}

SLANG_TAG_MARKERS = {
    "slang",
    "colloquial",
    "vulgar",
    "derogatory",
    "offensive",
    "informal",
    "emphatic",
    "jargon",
}

AMBIGUOUS_SINGLE_WORD_BLACKLIST = {
    "book",
    "cat",
    "free",
    "tank",
    "wipe",
    "grind",
    "buff",
    "kite",
    "proc",
    "cooldown",
}

CURATED_ENTRIES: list[dict[str, object]] = [
    {"source": "aggro", "target": "агро", "action": "translate", "domains": ["gaming", "mmo"], "priority": 120},
    {"source": "wipe", "target": "вайп", "action": "translate", "domains": ["gaming", "mmo"], "priority": 120},
    {"source": "wiped", "target": "вайпнулся", "action": "translate", "domains": ["gaming", "mmo"], "priority": 120},
    {"source": "adds", "target": "адды", "action": "translate", "domains": ["gaming", "mmo"], "priority": 120},
    {"source": "kite", "target": "кайтить", "action": "translate", "domains": ["gaming", "mmo"], "priority": 120},
    {"source": "kiting", "target": "кайтинг", "action": "translate", "domains": ["gaming", "mmo"], "priority": 120},
    {"source": "buff", "target": "баф", "action": "translate", "domains": ["gaming", "mmo"], "priority": 120},
    {"source": "debuff", "target": "дебаф", "action": "translate", "domains": ["gaming", "mmo"], "priority": 120},
    {"source": "proc", "target": "прок", "action": "translate", "domains": ["gaming", "mmo"], "priority": 120},
    {"source": "cooldown", "target": "кулдаун", "action": "translate", "domains": ["gaming", "mmo"], "priority": 120},
    {"source": "cooldowns", "target": "кулдауны", "action": "translate", "domains": ["gaming", "mmo"], "priority": 120},
    {"source": "gear", "target": "гир", "action": "translate", "domains": ["gaming", "mmo"], "priority": 110},
    {"source": "loot", "target": "лут", "action": "translate", "domains": ["gaming", "mmo"], "priority": 110},
    {"source": "build", "target": "билд", "action": "translate", "domains": ["gaming", "mmo"], "priority": 110},
    {"source": "spec", "target": "спек", "action": "translate", "domains": ["gaming", "mmo"], "priority": 110},
    {"source": "grind", "target": "гринд", "action": "translate", "domains": ["gaming", "mmo"], "priority": 110},
    {"source": "farm", "target": "фарм", "action": "translate", "domains": ["gaming", "mmo"], "priority": 110},
    {"source": "raid", "target": "рейд", "action": "translate", "domains": ["gaming", "mmo"], "priority": 110},
    {"source": "dungeon", "target": "данж", "action": "translate", "domains": ["gaming", "mmo"], "priority": 110},
    {"source": "world boss", "target": "ворлд-босс", "action": "translate", "domains": ["gaming", "mmo"], "priority": 110},
    {"source": "noob", "target": "нуб", "action": "translate", "domains": ["gaming", "internet"], "priority": 110},
    {"source": "nerf", "target": "нерф", "action": "translate", "domains": ["gaming", "internet"], "priority": 110},
    {"source": "nerfed", "target": "понерфили", "action": "translate", "domains": ["gaming", "internet"], "priority": 110},
    {"source": "good game", "target": "GG", "action": "preserve", "domains": ["gaming", "internet"], "priority": 100},
    {"source": "gg", "target": "GG", "action": "preserve", "domains": ["gaming", "internet"], "priority": 100},
    {"source": "ggs", "target": "GGs", "action": "preserve", "domains": ["gaming", "internet"], "priority": 100},
    {"source": "dps", "target": "DPS", "action": "preserve", "domains": ["gaming", "mmo"], "priority": 100},
    {"source": "pve", "target": "PvE", "action": "preserve", "domains": ["gaming", "mmo"], "priority": 100},
    {"source": "pvp", "target": "PvP", "action": "preserve", "domains": ["gaming", "mmo"], "priority": 100},
    {"source": "gvg", "target": "GvG", "action": "preserve", "domains": ["gaming", "mmo"], "priority": 100},
    {"source": "lfm", "target": "LFM", "action": "preserve", "domains": ["gaming", "mmo"], "priority": 100},
    {"source": "lfp", "target": "LFP", "action": "preserve", "domains": ["gaming", "mmo"], "priority": 100},
    {"source": "lfr", "target": "LFR", "action": "preserve", "domains": ["gaming", "mmo"], "priority": 100},
    {"source": "bis", "target": "BiS", "action": "preserve", "domains": ["gaming", "mmo"], "priority": 100},
    {"source": "ftw", "target": "FTW", "action": "preserve", "domains": ["internet", "slang"], "priority": 90},
    {"source": "qol", "target": "QoL", "action": "preserve", "domains": ["gaming", "internet"], "priority": 90},
    {"source": "jianghu", "target": "Цзянху", "action": "translate", "domains": ["wwm", "wuxia"], "priority": 130},
    {"source": "wuxia", "target": "уся", "action": "translate", "domains": ["wwm", "wuxia"], "priority": 130},
    {"source": "wulin", "target": "улинь", "action": "translate", "domains": ["wwm", "wuxia"], "priority": 130},
    {"source": "murim", "target": "мурим", "action": "translate", "domains": ["wwm", "wuxia"], "priority": 130},
    {"source": "dao", "target": "дао", "action": "translate", "domains": ["wwm", "wuxia"], "priority": 130},
    {"source": "qi", "target": "ци", "action": "translate", "domains": ["wwm", "wuxia"], "priority": 130},
    {"source": "sectless", "target": "без секты", "action": "translate", "domains": ["wwm", "wuxia"], "priority": 130},
    {"source": "gift of gab", "target": "дар красноречия", "action": "translate", "domains": ["wwm"], "priority": 130},
    {"source": "jianghu errands", "target": "поручения Цзянху", "action": "translate", "domains": ["wwm"], "priority": 130},
    {"source": "jianghu legacy", "target": "наследие Цзянху", "action": "translate", "domains": ["wwm"], "priority": 130},
    {"source": "inner path", "target": "Внутренний путь", "action": "translate", "domains": ["wwm"], "priority": 130},
    {"source": "mystic arts", "target": "мистические искусства", "action": "translate", "domains": ["wwm"], "priority": 130},
    {"source": "martial arts level", "target": "уровень боевых искусств", "action": "translate", "domains": ["wwm"], "priority": 130},
    {"source": "wind sense", "target": "Wind Sense", "action": "preserve", "domains": ["wwm", "skill"], "priority": 140},
    {"source": "meridian touch", "target": "Meridian Touch", "action": "preserve", "domains": ["wwm", "skill"], "priority": 140},
    {"source": "purple star catastrophe", "target": "катастрофа Пурпурной звезды", "action": "translate", "domains": ["wwm"], "priority": 130},
    {"source": "cultivation", "target": "культивация", "action": "translate", "domains": ["ui", "wuxia"], "priority": 140},
    {"source": "cloud mail", "target": "облачная почта", "action": "translate", "domains": ["ui"], "priority": 140},
    {"source": "reward highlights", "target": "основные награды", "action": "translate", "domains": ["ui"], "priority": 140},
    {"source": "exploration outfit", "target": "костюм исследователя", "action": "translate", "domains": ["ui"], "priority": 140},
    {"source": "taiping mausoleum", "target": "Taiping Mausoleum", "action": "preserve", "domains": ["wwm", "location"], "priority": 160},
    {"source": "mistveil city", "target": "Mistveil City", "action": "preserve", "domains": ["wwm", "location"], "priority": 160},
    {"source": "mistveil forest", "target": "Mistveil Forest", "action": "preserve", "domains": ["wwm", "location"], "priority": 160},
    {"source": "kaifeng", "target": "Kaifeng", "action": "preserve", "domains": ["wwm", "location"], "priority": 160},
    {"source": "qinghe", "target": "Qinghe", "action": "preserve", "domains": ["wwm", "location"], "priority": 160},
    {"source": "jadewood court", "target": "Jadewood Court", "action": "preserve", "domains": ["wwm", "location"], "priority": 160},
    {"source": "wishing cove", "target": "Wishing Cove", "action": "preserve", "domains": ["wwm", "location"], "priority": 160},
    {"source": "hollow abode", "target": "Hollow Abode", "action": "preserve", "domains": ["wwm", "location"], "priority": 160},
    {"source": "aureate pavilion", "target": "Aureate Pavilion", "action": "preserve", "domains": ["wwm", "location"], "priority": 160},
    {"source": "martial temple", "target": "Martial Temple", "action": "preserve", "domains": ["wwm", "location"], "priority": 160},
    {"source": "ghost revelry hall", "target": "Ghost Revelry Hall", "action": "preserve", "domains": ["wwm", "location"], "priority": 160},
    {"source": "wenjin pavilion", "target": "Wenjin Pavilion", "action": "preserve", "domains": ["wwm", "location"], "priority": 160},
    {"source": "heartless valley", "target": "Heartless Valley", "action": "preserve", "domains": ["wwm", "location"], "priority": 160},
    {"source": "qin caiwei", "target": "Qin Caiwei", "action": "preserve", "domains": ["wwm", "character"], "priority": 170},
    {"source": "murong yuan", "target": "Murong Yuan", "action": "preserve", "domains": ["wwm", "character"], "priority": 170},
    {"source": "murong yanzhao", "target": "Murong Yanzhao", "action": "preserve", "domains": ["wwm", "character"], "priority": 170},
    {"source": "shi zhen", "target": "Shi Zhen", "action": "preserve", "domains": ["wwm", "character"], "priority": 170},
    {"source": "zhai xu", "target": "Zhai Xu", "action": "preserve", "domains": ["wwm", "character"], "priority": 170},
    {"source": "officer nan", "target": "Officer Nan", "action": "preserve", "domains": ["wwm", "character"], "priority": 170},
    {"source": "song wu", "target": "Song Wu", "action": "preserve", "domains": ["wwm", "character"], "priority": 170},
    {"source": "shen weiqing", "target": "Shen Weiqing", "action": "preserve", "domains": ["wwm", "character"], "priority": 170},
    {"source": "qi sheng", "target": "Qi Sheng", "action": "preserve", "domains": ["wwm", "character"], "priority": 170},
    {"source": "jiuliu sect", "target": "Jiuliu Sect", "action": "preserve", "domains": ["wwm", "sect"], "priority": 170},
    {"source": "kuanglan sect", "target": "Kuanglan Sect", "action": "preserve", "domains": ["wwm", "sect"], "priority": 170},
    {"source": "liyuan sect", "target": "Liyuan Sect", "action": "preserve", "domains": ["wwm", "sect"], "priority": 170},
    {"source": "lonely cloud sect", "target": "Lonely Cloud Sect", "action": "preserve", "domains": ["wwm", "sect"], "priority": 170},
    {"source": "drunken flowers sect", "target": "Drunken Flowers Sect", "action": "preserve", "domains": ["wwm", "sect"], "priority": 170},
]


@dataclass(slots=True)
class HintEntry:
    source: str
    domains: set[str] = field(default_factory=set)
    tags: set[str] = field(default_factory=set)
    topics: set[str] = field(default_factory=set)
    russian: set[str] = field(default_factory=set)
    glosses: set[str] = field(default_factory=set)
    score: int = 0


def normalize_phrase(value: str) -> str:
    tokens = TOKEN_WORD_RE.findall(value)
    return " ".join(token.lower() for token in tokens)


def sense_markers(sense: dict[str, object]) -> tuple[set[str], set[str], set[str]]:
    tags = {str(tag).strip().lower() for tag in (sense.get("tags") or [])}
    topics = {str(topic).strip().lower() for topic in (sense.get("topics") or [])}
    category_names = {
        str(category.get("name", "")).strip().lower()
        for category in (sense.get("categories") or [])
        if isinstance(category, dict)
    }
    return tags, topics, category_names


def classify_sense(tags: set[str], topics: set[str], categories: set[str]) -> set[str]:
    domains: set[str] = set()
    category_blob = " ".join(categories)

    if topics & GAMING_TOPIC_MARKERS or any(
        marker in category_blob
        for marker in ("video game", "video-game", "gaming slang", "gaming", "mmorpg", "role-playing")
    ):
        domains.add("gaming")

    if tags & INTERNET_TAG_MARKERS or topics & {"internet"} or any("internet slang" in category or "internet" == category for category in categories):
        domains.add("internet")

    if tags & SLANG_TAG_MARKERS or any(
        marker in category_blob
        for marker in ("internet slang", "gaming slang", "video-game slang", "mmorpg slang")
    ):
        domains.add("slang")

    return domains


def is_high_value_hint(word: str, domains: set[str], tags: set[str], topics: set[str]) -> bool:
    normalized = normalize_phrase(word)
    if not normalized:
        return False

    if len(normalized) <= 1:
        return False

    token_count = len(normalized.split())
    if token_count > 5:
        return False

    if not word[:1].isalpha():
        return False

    if token_count > 1:
        return True

    plain = normalized
    if plain in {"gg", "ftw", "bis", "dps", "oom", "aggro", "proc", "buff", "debuff", "cooldown", "noob", "nerf"}:
        return True

    if "gaming" in domains or "internet" in domains:
        return True

    if plain in AMBIGUOUS_SINGLE_WORD_BLACKLIST and "gaming" not in domains and "internet" not in domains:
        return False

    if "slang" in domains and ("gaming" in domains or "internet" in domains):
        return True

    if "slang" in domains and (len(plain) <= 8 or "-" in plain or "'" in plain or plain.isupper()):
        return True

    return False


def extract_russian_translations(entry: dict[str, object]) -> set[str]:
    values: set[str] = set()
    for translation in entry.get("translations") or []:
        if not isinstance(translation, dict):
            continue
        if translation.get("lang_code") != "ru":
            continue
        word = str(translation.get("word") or "").strip()
        if word:
            values.add(word)
    return values


def extract_kaikki_hints(path: Path) -> dict[str, HintEntry]:
    hints: dict[str, HintEntry] = {}

    with gzip.open(path, "rt", encoding="utf-8") as handle:
        for line in handle:
            entry = json.loads(line)
            if entry.get("lang_code") != "en":
                continue

            word = str(entry.get("word") or "").strip()
            if not word:
                continue

            per_entry_domains: set[str] = set()
            per_entry_tags: set[str] = set()
            per_entry_topics: set[str] = set()
            glosses: set[str] = set()

            for sense in entry.get("senses") or []:
                if not isinstance(sense, dict):
                    continue

                tags, topics, categories = sense_markers(sense)
                domains = classify_sense(tags, topics, categories)
                if not domains:
                    continue

                per_entry_domains.update(domains)
                per_entry_tags.update(tags)
                per_entry_topics.update(topics)
                for gloss in sense.get("glosses") or []:
                    if isinstance(gloss, str) and gloss:
                        glosses.add(gloss.strip())

            if not per_entry_domains:
                continue

            if not is_high_value_hint(word, per_entry_domains, per_entry_tags, per_entry_topics):
                continue

            key = normalize_phrase(word)
            if not key:
                continue

            bucket = hints.setdefault(key, HintEntry(source=word))
            bucket.domains.update(per_entry_domains)
            bucket.tags.update(per_entry_tags)
            bucket.topics.update(per_entry_topics)
            bucket.russian.update(extract_russian_translations(entry))
            bucket.glosses.update(list(glosses)[:3])
            bucket.score += 1 + (2 if "gaming" in per_entry_domains else 0) + (1 if "internet" in per_entry_domains else 0)

            if word.isupper() or len(word.split()) > len(bucket.source.split()):
                bucket.source = word

    return hints


def filter_hint_entries(hints: dict[str, HintEntry], domain: str) -> list[dict[str, object]]:
    items: list[dict[str, object]] = []
    for key, hint in sorted(hints.items(), key=lambda item: (-item[1].score, item[0])):
        if domain not in hint.domains:
            continue

        if domain == "slang":
            token_count = len(key.split())
            keep_slang = (
                "gaming" in hint.domains
                or "internet" in hint.domains
                or token_count > 1
                or len(key) <= 6
                or bool(hint.russian)
                or "-" in key
                or "'" in key
            )
            if not keep_slang:
                continue

        entry = {
            "source": hint.source,
            "normalizedSource": key,
            "action": "hint",
            "domains": sorted(hint.domains),
            "tags": sorted(tag for tag in hint.tags if tag),
            "topics": sorted(topic for topic in hint.topics if topic),
            "russianHints": sorted(hint.russian),
            "glosses": sorted(hint.glosses)[:3],
            "priority": 20 if domain == "slang" else 30,
            "sourceDatabase": "Kaikki/Wiktionary",
        }
        items.append(entry)
    return items


def build_curated_entries() -> list[dict[str, object]]:
    items: list[dict[str, object]] = []
    for entry in CURATED_ENTRIES:
        normalized = normalize_phrase(str(entry["source"]))
        if not normalized:
            continue

        item = dict(entry)
        item["normalizedSource"] = normalized
        item["sourceDatabase"] = "Berezka Curated"
        items.append(item)

    return sorted(items, key=lambda item: (-int(item["priority"]), str(item["normalizedSource"])))


def write_json(path: Path, payload: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--english-dump", type=Path, default=ENGLISH_DUMP)
    parser.add_argument("--russian-dump", type=Path, default=RUSSIAN_DUMP)
    parser.add_argument("--output-root", type=Path, default=OUTPUT_ROOT)
    args = parser.parse_args()

    if not args.english_dump.is_file():
        raise SystemExit(f"English Kaikki dump not found: {args.english_dump}")
    if not args.russian_dump.is_file():
        raise SystemExit(f"Russian Kaikki dump not found: {args.russian_dump}")

    hints = extract_kaikki_hints(args.english_dump)

    output_root = args.output_root
    curated = build_curated_entries()
    gaming_hints = filter_hint_entries(hints, "gaming")
    slang_hints = filter_hint_entries(hints, "slang")
    internet_hints = filter_hint_entries(hints, "internet")

    write_json(
        output_root / "berezka-curated-runtime.en-ru.json",
        {
            "name": "Berezka curated runtime glossary",
            "source": "Berezka Curated",
            "entryCount": len(curated),
            "entries": curated,
        },
    )
    write_json(
        output_root / "kaikki-gaming-hints.en.json",
        {
            "name": "Kaikki gaming hints",
            "source": "Kaikki/Wiktionary",
            "entryCount": len(gaming_hints),
            "entries": gaming_hints,
        },
    )
    write_json(
        output_root / "kaikki-slang-hints.en.json",
        {
            "name": "Kaikki slang hints",
            "source": "Kaikki/Wiktionary",
            "entryCount": len(slang_hints),
            "entries": slang_hints,
        },
    )
    write_json(
        output_root / "kaikki-internet-hints.en.json",
        {
            "name": "Kaikki internet hints",
            "source": "Kaikki/Wiktionary",
            "entryCount": len(internet_hints),
            "entries": internet_hints,
        },
    )
    write_json(
        output_root / "index.json",
        {
            "sources": [
                "berezka-curated-runtime.en-ru.json",
                "kaikki-gaming-hints.en.json",
                "kaikki-slang-hints.en.json",
                "kaikki-internet-hints.en.json",
            ]
        },
    )

    print(f"curated={len(curated)} gaming={len(gaming_hints)} slang={len(slang_hints)} internet={len(internet_hints)}")


if __name__ == "__main__":
    main()
