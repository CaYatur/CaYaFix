# -*- coding: utf-8 -*-
"""Normalize mojibake and exotic hyphens in Strings*.resx for clean font rendering."""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def fix_text(text: str) -> str:
    replacements = {
        "Wiâ€‘Fi": "Wi-Fi",
        "Wiâ€“Fi": "Wi-Fi",
        "Wiâ€”Fi": "Wi-Fi",
        "Wiâ€\x91Fi": "Wi-Fi",
        "Wiâ€™Fi": "Wi-Fi",
        "Wiâ€˜Fi": "Wi-Fi",
        "Wi‑Fi": "Wi-Fi",  # U+2011
        "Wi–Fi": "Wi-Fi",  # en dash
        "Wi—Fi": "Wi-Fi",  # em dash
        "â€¦": "...",
        "…": "...",
        "â€“": "-",
        "â€”": "-",
        "â€‘": "-",
        "â€œ": '"',
        "â€\x9d": '"',
        "â€\x9c": '"',
        "â€™": "'",
        "â€˜": "'",
        "â€\x94": "-",
        "â€\x93": "-",
        "â€\x92": "'",
        "â€\x91": "-",
        "—": "-",
        "–": "-",
        "‑": "-",
        "\u2011": "-",
        "\u2013": "-",
        "\u2014": "-",
        "\u2026": "...",
    }
    for old, new in replacements.items():
        text = text.replace(old, new)

    # Any residual Wi + non-ascii junk + Fi
    text = re.sub(r"Wi[^\x00-\x7F\w\s]{1,8}Fi", "Wi-Fi", text)
    return text


def main() -> None:
    for rel in (
        "CaYaFix.App/Properties/Strings.resx",
        "CaYaFix.App/Properties/Strings.tr.resx",
    ):
        path = ROOT / rel
        original = path.read_text(encoding="utf-8-sig")
        fixed = fix_text(original)
        path.write_text(fixed, encoding="utf-8-sig")
        left = "â€" in fixed
        sample = re.search(
            r'name="Module_Network_Description"[^>]*>\s*<value>(.*?)</value>',
            fixed,
            re.S,
        )
        print(f"{rel}: changed={original != fixed} residual_mojibake={left}")
        if sample:
            print(f"  Module_Network_Description={sample.group(1)!r}")


if __name__ == "__main__":
    main()
