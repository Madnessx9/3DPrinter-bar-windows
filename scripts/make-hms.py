#!/usr/bin/env python3
"""Builds Resources/hms_en.json from ha-bambulab's hms_en.json.gz (MIT).
Keeps, for each code, the P2S-specific text or else the generic one.
Usage: scripts/make-hms.py <path/to/hms_en.json.gz> Resources/hms_en.json"""
import gzip, json, sys

src, dst = sys.argv[1], sys.argv[2]
data = json.load(gzip.open(src))
out = {}
for section in ("device_hms", "device_error"):
    table = {}
    for code, texts in data[section].items():
        chosen = None
        for text, models in texts.items():
            if "P2S" in models:
                chosen = text
                break
            if not models and chosen is None:
                chosen = text
        if chosen:
            table[code.upper()] = chosen.strip()
    out["hms" if section == "device_hms" else "error"] = table
json.dump(out, open(dst, "w"), separators=(",", ":"), ensure_ascii=False, sort_keys=True)
print(dst, len(out["hms"]), len(out["error"]))
