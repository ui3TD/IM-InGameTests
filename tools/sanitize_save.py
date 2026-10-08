"""Turn an Idol Manager save into a shareable test fixture.

- Replaces the player's name, the player's group names and the save's name and timestamp.
- Strips the mod part of portrait asset IDs (a Workshop or local portrait pack the
  reader may not have). The game falls back to a vanilla asset of the same type.
- Optionally drops idol variables written by mods (--drop-girl-variable REGEX).

Prints what it changed and a list of any strings that still look mod-specific, so you
can review them. Check the result loads with mods disabled:

    python tools/sanitize_save.py my_save.json fixtures/default.json
    python run_ingame_tests.py --save fixtures/default.json --vanilla
"""

import argparse
import json
import re
from pathlib import Path

from savefile import load, walk, write

GENERATED_LAST_SAVE = "2000-01-01 00:00:00"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input", type=Path)
    ap.add_argument("output", type=Path)
    ap.add_argument("--first-name", default="Test")
    ap.add_argument("--last-name", default="Producer")
    ap.add_argument("--group-name", default="Test Group")
    ap.add_argument("--drop-girl-variable", action="append", default=[], metavar="REGEX",
                    help="remove idol variables fully matching REGEX (repeatable)")
    args = ap.parse_args()

    data = load(args.input)
    report = []

    # Player identity and save metadata.
    player = data["staticVars__PlayerData"]
    old_first, old_last = player.get("FirstName", ""), player.get("LastName", "")
    player["FirstName"], player["LastName"] = args.first_name, args.last_name
    player["SaveFileName"] = "fixture"
    player["LastSave"] = GENERATED_LAST_SAVE
    report.append(f"player name {old_first!r} {old_last!r} -> {args.first_name!r} {args.last_name!r}")

    # The player's groups: the main group, then sister groups in ID order.
    renames = {}
    main_group = player.get("GroupName", "")
    if main_group:
        renames[main_group] = args.group_name
    sister = 1
    for group in data.get("Groups__Groups", []):
        title = group.get("Title", "")
        if title and title not in renames:
            renames[title] = f"{args.group_name} Sister {sister}"
            sister += 1
    player["GroupName"] = args.group_name

    counts = {old: 0 for old in renames}
    # Longest first, so a name contained in another (e.g. "Pigs" in "SubPigs") is handled by the longer one.
    ordered = sorted(renames, key=len, reverse=True)

    def rename(container, key, value):
        new = value
        for old in ordered:
            if old in new:
                counts[old] += new.count(old)
                new = new.replace(old, renames[old])
        if new != value:
            container[key] = new

    walk(data, rename)
    for old, n in counts.items():
        report.append(f"group {old!r} -> {renames[old]!r} ({n} occurrences)")

    # Portrait asset IDs: "<type> <body> <part> <mod>" -> "<type> <body> <part>".
    stripped = 0

    def strip_assets(node):
        nonlocal stripped
        if isinstance(node, dict):
            for key, value in node.items():
                if key == "textureAssets" and isinstance(value, list):
                    for asset in value:
                        parts = str(asset.get("asset_id", "")).split(" ")
                        if len(parts) > 3:
                            asset["asset_id"] = " ".join(parts[:3])
                            stripped += 1
                else:
                    strip_assets(value)
        elif isinstance(node, list):
            for value in node:
                strip_assets(value)

    strip_assets(data)
    report.append(f"modded portrait asset IDs stripped: {stripped}")

    # Mod-written idol variables.
    if args.drop_girl_variable:
        patterns = [re.compile(p) for p in args.drop_girl_variable]
        dropped = 0
        for girl in data.get("data_girls__Girls", []):
            before = girl.get("Variables") or []
            kept = [v for v in before if not any(p.fullmatch(str(v)) for p in patterns)]
            dropped += len(before) - len(kept)
            girl["Variables"] = kept
        report.append(f"idol variables dropped: {dropped}")

    # Leftovers worth a manual look.
    suspicious = set()
    looks_modded = re.compile(r"(?i)(workshop|steamapps|[a-z]:[\\/]|users[\\/]|\.dll)")

    def scan(container, key, value):
        if looks_modded.search(value):
            suspicious.add(value[:120])

    walk(data, scan)
    for name in (old_first, old_last):
        if len(name) >= 3 and name in json.dumps(data, ensure_ascii=False):
            suspicious.add(f"old player name still present: {name}")

    write(data, args.output)

    print("\n".join(report))
    if suspicious:
        print("Review these strings (may be mod-specific or personal):")
        for s in sorted(suspicious):
            print("  " + s)
    print(f"Wrote {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
