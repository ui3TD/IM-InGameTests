"""Turn an Idol Manager save into a shareable test fixture.

- Replaces the player's name, the player's group names and the save's name and timestamp.
- Makes every idol portrait use base-game parts only. A part from a Workshop or local
  portrait pack (which the reader may not have) keeps its ID without the mod name if the
  base game has that part, and otherwise becomes the first part of the same body;
  modded accessories are dropped. A part the game doesn't know would be swapped for
  another body's and drawn misaligned.
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

from savefile import BASE_GAME_PORTRAITS, in_base_game, load, portrait_part, walk, write

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
    # Longest first, so a name contained in another (e.g. "Stars" in "Shooting Stars") is handled by the longer one.
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

    # Portraits: every part must exist in the base game and belong to the portrait's body,
    # or the game substitutes another body's part and draws it misaligned.
    portrait_counts = {"stripped": 0, "replaced": 0, "accessories dropped": 0}

    def fix_portrait(assets):
        body_entry = next((a for a in assets if a.get("type") == "0"), None)
        body_part = portrait_part(body_entry["asset_id"]) if body_entry else None
        body = body_part[1] if body_part and body_part[1] in BASE_GAME_PORTRAITS else 0
        kept = []
        for asset in assets:
            old = asset.get("asset_id", "")
            part = portrait_part(old)
            if part is None:
                kept.append(asset)
                continue
            base_id = f"{part[0]} {part[1]} {part[2]}"
            if in_base_game(base_id) and part[1] == body:
                new = base_id
            elif asset.get("type") == "3":
                portrait_counts["accessories dropped"] += 1
                continue
            else:
                new = f"{asset.get('type')} {body} 0"  # every base-game body has a part 0 of each type
            if new != old:
                portrait_counts["stripped" if new == base_id else "replaced"] += 1
                asset["asset_id"] = new
            kept.append(asset)
        return kept

    def fix_portraits(node):
        if isinstance(node, dict):
            for key, value in node.items():
                if key == "textureAssets" and isinstance(value, list):
                    node[key] = fix_portrait(value)
                else:
                    fix_portraits(value)
        elif isinstance(node, list):
            for value in node:
                fix_portraits(value)

    fix_portraits(data)
    report.append("portrait parts: " + ", ".join(f"{n} {what}" for what, n in portrait_counts.items()))

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
