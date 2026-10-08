"""Read and write Idol Manager save files in the format the game itself writes.

The game rewrites saves through SimpleJSON, so every value is a string and empty
strings are left out. Fixtures keep that format.
"""

import json
from pathlib import Path


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
