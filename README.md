# IM-InGameTests

Automated tests that run **inside Idol Manager** with your mods loaded, covering what unit tests
can't: the Unity lifecycle, real game data, and all mods loaded together. One command starts the
game, loads a save, runs the tests, writes the results and quits, in about 40 seconds.

```
python run_ingame_tests.py                  # smoke suite: load the save, run 4 in-game weeks
python run_ingame_tests.py --vanilla        # same, with every mod disabled, as a baseline
python run_ingame_tests.py --only "My Mod"  # only this mod enabled (repeatable)
```

```
  [PASS] bootstrap  (14.4s)
  [PASS] SmokeTests.EveryEnabledHarmonyModIsPatched  (0.0s)
  [PASS] SmokeTests.AdvanceWeeks  (21.0s)
         newDayEvents = 28
PASSED
```

Exit code: 0 passed, 1 a test failed, 2 no results (crash, timeout or setup problem).

## Requirements

- Windows, Idol Manager from Steam, and Steam running.
- [BepInEx 5](https://github.com/BepInEx/BepInEx) in the game folder.
- [.NET SDK](https://dotnet.microsoft.com/download) and Python 3.10+.

The game folder is found from your Steam libraries (or pass `--game-dir`).

## How a run works

1. The script builds the plugin, copies it to `<game>\BepInEx\plugins\InGameTests\`, and launches
   the game with `-imtest`. Without that flag the plugin does nothing, so normal play is unaffected.
2. The plugin blocks all save, settings and Steam achievement writes, ignores keyboard and mouse
   input, and mutes the game (`--allow-input` and `--sound` turn the last two off).
3. It loads a copy of the test save, `fixtures/default.json` by default. [fixtures/README.md](fixtures/README.md)
   lists what's in it.
4. It runs every `[InGameTest]` in the chosen suite, one after another in the same session. While
   time advances it closes popups and takes the first choice in dialogues, like a player would.
5. Any Unity, BepInEx or Harmony error that isn't on the ignore list fails the test it happened in.
6. It writes `results.json` to `%USERPROFILE%\AppData\LocalLow\Glitch Pitch\Idol Manager\InGameTests\<run>\`
   and quits, and the script prints the summary.

## Included tests

| Suite | Test | Checks |
|---|---|---|
| `smoke` | `EveryEnabledHarmonyModIsPatched` | Every enabled Harmony mod has its DLL and at least one patch applied. |
| `smoke` | `EveryPatchMethodIsApplied` | Every Prefix, Postfix, Transpiler or Finalizer an enabled Harmony mod declares is applied under its HarmonyID. Harmony skips the patches in types that fail to load, and the loader only logs it. |
| `smoke` | `EveryTranspilerChangesIL` | Each enabled mod's transpiler changes its method's IL. A transpiler whose IL search finds nothing usually returns the code untouched, and the mod then does nothing, with no error. |
| `smoke` | `EveryModTextIsLoaded` | Every text in an enabled mod's `constants.json` is in the game's text table. Texts a later mod replaces are noted with the mod that wins. |
| `smoke` | `AdvanceWeeks` | N weeks pass with no errors. Fails fast if an exception stops the game clock, the usual way a broken mod shows in play. |
| `selftest` | `DialogueClickThroughFinishesDialogue` | The runner's dialogue clicking reaches the end of a dialogue. |
| `selftest` | `InputBlockedAndMuted` | Input blocking and muting are in place. |

## Writing tests

Put a static coroutine in the plugin, or in your own assembly named `InGameTests.*` deployed to the
same plugins folder. The runner loads those in test mode; they don't need a BepInEx plugin class.

```csharp
[InGameTest(Suite = "mymod", Order = 0)]
private static IEnumerator ClockMovesWhenUnpaused(TestContext ctx)
{
    mainScript main = Camera.main.GetComponent<mainScript>();
    DateTime before = staticVars.dateTime;
    main.Time_SetState(mainScript._time_state.fast);
    yield return new WaitForSecondsRealtime(2f);
    main.Time_SetState(mainScript._time_state.pause);

    ctx.Assert(staticVars.dateTime > before, "the clock did not move");
    ctx.Record("advancedBy", staticVars.dateTime - before);   // shown in the report
}
```

- `ctx.Assert`/`ctx.Fail` fail the test; `ctx.Note`/`ctx.Record` add to the report.
- `Game` has the runner's helpers:
  - `Game.AdvanceDays(ctx, n)` runs the clock like the smoke suite does, clicking through dialogues and popups.
    `Game.ClickDialogue`, `Game.Unstall` and `Game.DescribeClock` are the pieces it's built from.
  - `Game.WaitFor(ctx, condition, seconds, what)` waits or fails.
  - `Game.Girl(id)`, `Game.OpenProfile(girl, tab)` and `Game.CloseAllPopups(ctx)` reach the fixture's idols and screens.
  - `Game.SelectPolicy` and `Game.ClockSpeed` change game state until disposed.
  - `Game.TrainingTickAddParams(room)` runs one real training tick and returns the stamina and stat changes it asks for, without applying them.
- `TestTools.Spy(method, prefix, postfix)` patches a method ahead of every other patch until disposed, so a
  prefix sees the caller's arguments and a postfix sees the game's own result. `TestTools.Restore(action)`
  runs the action on dispose.
- `HarmonyMod.Enabled()` lists the enabled Harmony mods with their loaded assemblies.
- Use `WaitForSecondsRealtime`: `Time.timeScale` may be raised.
- Call game methods; `UnityEngine.Input` is blocked during runs.
- The game's Unity is stripped, so an API that compiles may be missing at runtime. Prefer APIs the game uses.
- Build your own assembly before each run; the runner tests whatever is deployed. `source/InGameTests.csproj`
  shows a build target that deploys.

## Saves, known errors and scope

- `tools/sanitize_save.py` makes a shareable fixture from your own save (renames, swaps modded portrait
  parts, can drop mod variables). Check it with `tools/check_save.py` and one `--vanilla` run.
- Errors that don't come from what you're testing can be ignored with one .NET regex per line, matched
  against `message\nstack trace`: in `ignore.txt` (committed) or `ignore.local.txt` (your own setup).
- The per-mod checks (`EveryEnabledHarmonyModIsPatched`, `EveryPatchMethodIsApplied`, `EveryTranspilerChangesIL`,
  `EveryModTextIsLoaded`) can be limited to the mods you maintain, so a mod you don't can't fail your runs.
  Scope rules use the same format, one .NET regex per line, matched against each mod's HarmonyID, title and
  folder name. Put them in `scope.local.txt` (your own setup), or in a file your project passes with
  `--scope-file`. With no rules every mod is checked. Every mod stays loaded either way; mods outside
  the scope are listed in a note. A rule that matches no installed mod fails the run, like an unknown `--only`.

## Options

| Option | Default | |
|---|---|---|
| `--suite` | `smoke` | Suite to run; `all` runs every suite. |
| `--save` | `fixtures/default.json` | Save to load. |
| `--weeks` | 4 | In-game weeks to advance. |
| `--timescale` | 20 | `Time.timeScale` while advancing. |
| `--timeout` | 300 | Seconds before the game is killed. |
| `--game-dir` | from Steam | Idol Manager folder. |
| `--vanilla` | | Disable every mod for this run. |
| `--only MOD` | | Enable only this mod (folder name, Workshop ID, title or HarmonyID); repeatable. |
| `--scope-file FILE` | | More scope rules for the per-mod checks; repeatable. |
| `--allow-input` | | Let keyboard and mouse reach the game. |
| `--sound` | | Don't mute the game. |
| `--skip-runner-build` | | Use the plugin already in the game instead of rebuilding it. |
| `-v` | | Full stack traces and per-mod patch counts. |

`--vanilla` and `--only` affect that run only; your mod list isn't changed.

## License

[MIT](LICENSE)
