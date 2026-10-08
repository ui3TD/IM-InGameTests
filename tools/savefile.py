"""Read and write Idol Manager save files in the format the game itself writes.

The game rewrites saves through SimpleJSON, so every value is a string and empty
strings are left out. Fixtures keep that format.
"""

import json
from pathlib import Path

# The base game's idol portrait parts (game v1.0.6, data_girls_textures.DefaultAssets),
# as {body ID: (body variants, hairs, faces, accessories)}. A part's ID is
# "<type> <body> <index>" with type 0 body, 1 hair, 2 face, 3 accessory; a part from
# a mod has the mod's name appended. An ID the game doesn't know falls back to the
# first part of that type it has (body 0's), which draws misaligned on another body.
BASE_GAME_PORTRAITS = {
    0: (8, 9, 8, 17), 1: (8, 12, 8, 11), 2: (8, 28, 8, 6), 3: (8, 9, 8, 3),
    4: (8, 9, 6, 2), 5: (8, 8, 7, 3), 6: (8, 13, 6, 2), 7: (8, 11, 6, 3),
    8: (8, 13, 6, 3), 9: (8, 15, 6, 5), 10: (8, 25, 8, 1), 11: (8, 9, 7, 10),
}


def portrait_part(asset_id: str):
    """(type, body, index, mod) of a portrait part ID, or None if it isn't one."""
    parts = asset_id.split(" ")
    if len(parts) < 3 or not all(p.lstrip("-").isdigit() for p in parts[:3]):
        return None
    return int(parts[0]), int(parts[1]), int(parts[2]), " ".join(parts[3:])


def in_base_game(asset_id: str) -> bool:
    part = portrait_part(asset_id)
    if part is None or part[3]:
        return False
    ptype, body, index, _ = part
    counts = BASE_GAME_PORTRAITS.get(body)
    return counts is not None and 0 <= ptype < 4 and 0 <= index < counts[ptype]


def load(path: Path) -> dict:
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def write(data: dict, path: Path) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, separators=(", ", ":")), encoding="utf-8")


def walk(node, fn):
    """Apply fn(container, key, value) to every scalar string in the JSON tree."""
    if isinstance(node, dict):
        for key, value in node.items():
            if isinstance(value, (dict, list)):
                walk(value, fn)
            elif isinstance(value, str):
                fn(node, key, value)
    elif isinstance(node, list):
        for i, value in enumerate(node):
            if isinstance(value, (dict, list)):
                walk(value, fn)
            elif isinstance(value, str):
                fn(node, i, value)
