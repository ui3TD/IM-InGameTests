`default.json` is the save the tests load unless you pass `--save <path>`. It's a freeplay save
from game v1.0.6, dated 2024-03-24 in game, with 12 idols, a sister group, a theater and singles released.
It was cleaned with `tools/sanitize_save.py` and checked with every mod disabled (`--vanilla`).

To test with your own save (e.g. one where your mod's feature is in use), clean it the same way:

```
python tools/sanitize_save.py "%USERPROFILE%\AppData\LocalLow\Glitch Pitch\Idol Manager\data\auto_save.json" fixtures/mine.json
python run_ingame_tests.py --save fixtures/mine.json
```

Other `.json` files in this folder are ignored by git, so your personal saves stay local.
