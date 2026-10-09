# IM-InGameTests

Automated tests that run **inside Idol Manager** with your mods loaded, covering what unit tests
can't: the Unity lifecycle, real game data, and all mods loaded together. One command starts the
game, loads a save, runs the tests, writes the results and quits, in about 40 seconds.

```
python run_ingame_tests.py                   # smoke suite: load the save, run 4 in-game weeks
python run_ingame_tests.py --load-vanilla    # same, with no mods loaded, as a baseline
python run_ingame_tests.py --load "My Mod"   # only this mod loaded (repeatable)
python run_ingame_tests.py --scope "My Mod"  # every suite's tests for this mod, all mods loaded
python run_ingame_tests.py --suite menu      # through the main menu: load a save there, start a new game (not in --scope runs)
```

```
  [PASS] bootstrap  (14.4s)
  [PASS] HarmonyModTests.EveryPatchMethodIsApplied  (0.0s)
  [PASS] SmokeTests.AdvanceWeeks  (21.0s)
         newDayEvents = 28
  [PASS] SaveLoadTests.QuicksaveQuickloadRoundTrip  (3.9s)
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
2. The plugin moves every save into the run folder (`saves\`), so autosaves, quicksaves and your
   mod's save code run for real without touching your own saves. It blocks settings and Steam
   achievement writes, ignores keyboard and mouse input, and mutes the game (`--allow-input` and
   `--sound` turn the last two off). After the run, the script checks that nothing in your save
   folder (`...\Idol Manager\data\`) changed, and fails the run if anything did.
3. It loads a copy of the test save, `fixtures/default.json` by default. [fixtures/README.md](fixtures/README.md)
   lists what's in it.
4. It runs the chosen tests (see [Choosing what runs](#choosing-what-runs)) one after another in the
   same session. While time advances it closes popups and takes the first choice in dialogues, like a
   player would.
5. Any Unity, BepInEx or Harmony error that isn't on the ignore list fails the test it happened in.
6. It writes `results.json` to `%USERPROFILE%\AppData\LocalLow\Glitch Pitch\Idol Manager\InGameTests\<run>\`
   and quits, and the script prints the summary.

## Included tests

| Suite | Test | Checks |
|---|---|---|
| `smoke` | `EveryPatchMethodIsApplied` | Every enabled Harmony mod's DLL is loaded with at least one patch applied, and every Prefix, Postfix, Transpiler or Finalizer it declares is applied under its HarmonyID. Harmony skips the patches in types that fail to load, and the loader only logs it. |
| `smoke` | `EveryTranspilerChangesIL` | Each enabled mod's transpiler changes its method's IL. A transpiler whose IL search finds nothing usually returns the code untouched, and the mod then does nothing, with no error. |
| `smoke` | `AdvanceWeeks` | N weeks pass with no errors. Fails fast if an exception stops the game clock, the usual way a broken mod shows in play. |
| `smoke` | `QuicksaveQuickloadRoundTrip` | After those weeks, a quicksave (F5), quickload (F9) and second quicksave give the same save file, and a day then runs on the reloaded game. Fails with the save values that changed, such as a mod's data that isn't saved or a load hook that changes state. The few values the base game itself changes on a load are allowed, each only in the way the game changes it. |
| `menu` | `MainMenuRoundTrip` | The same round trip through the main menu: quicksave, leave for the menu, load that file from the menu, quicksave again, then a day. A load from the menu starts a new game scene, so a mod that keeps state in a static, or doesn't set it up again with the scene, shows here and not in the quickload. Two more base-game changes are allowed: the agency's fan totals worked out again from the idols' fans, and, on a load from the menu, the dates dialogues were last triggered (the game forgets them all). |
| `menu` | `NewGameRunsToItsFirstWeek` | From the menu after the fixture was played, a new free-play game starts on the game's start date and runs to its first Monday (4 days, one weekly event) with no errors. Mods run their new-game setup instead of their load code. Last in any run, because it replaces the fixture's game. |
| `selftest` | `DialogueClickThroughFinishesDialogue` | The runner's dialogue clicking reaches the end of a dialogue. |
| `selftest` | `InputBlockedAndMuted` | Input blocking and muting are in place. |

## Choosing what runs

Each option answers one question:

| Question | Options |
|---|---|
| Which mods does the game load? | `--load MOD` (repeatable), `--load-vanilla`. Default: the mods enabled in game. |
| Whose tests run? | `--scope RULE`, `--scope-list FILE` (repeatable). Default: `scope.txt` and `scope.local.txt`. |
| Which sequence of tests? | `--suite NAME`. Default: `affected` when a scope is given on the command line, otherwise `smoke`. |

- **Suites.** Each test names one suite, `[InGameTest(Suite = ...)]`, and a suite is the sequence of
  tests one run goes through. Name a suite after its scenario and cost (`smoke`, `auditions`), never
  after a mod: a mod can have tests in several suites, and a suite holds many mods' tests. `all` runs
  every suite, including `selftest`.
- **Mods under test.** `[ModUnderTest("<HarmonyID>")]` on a test class names the one mod its tests
  exercise. Those tests run only while that mod is in scope. `[ModUnderTest(ModUnderTestAttribute.EveryMod)]`
  marks checks that judge every mod in scope, like the first two in `smoke` below. Tests in an untagged
  class are general and run whenever their suite does.
- **Scope.** One .NET regex per line, matched against each mod's HarmonyID, title and folder name (a
  Workshop ID for Workshop mods). Put rules for your own setup in `scope.local.txt` (not committed).
  `--scope` and `--scope-list` replace both files for one run. With no rules every mod is in scope.
  Every mod stays loaded either way; tests and mods outside the scope are listed in a note. A rule that
  matches no installed mod fails the run, like an unknown `--load`.
- **`affected`.** Runs every suite's tests for the mods in scope, and no general tests. After changing a
  mod, `--scope "<Mod>"` runs everything that tests it, in one boot.

## Writing tests

Put a static coroutine in the plugin, or in your own assembly named `InGameTests.*` deployed to the
same plugins folder. The runner loads those in test mode; they don't need a BepInEx plugin class.

```csharp
[ModUnderTest("com.me.mymod")]
internal static class MyModTests
{
    [InGameTest(Suite = "mods")]
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
}
```

- `ctx.Assert`/`ctx.Fail` fail the test; `ctx.Note`/`ctx.Record` add to the report.
- `ModScope.Includes(harmonyId)` tells a check that judges every mod which ones are in scope.
- Use `WaitForSecondsRealtime`: `Time.timeScale` may be raised.
- Call game methods; `UnityEngine.Input` is blocked during runs.
- The game's Unity is stripped, so an API that compiles may be missing at runtime. Prefer APIs the game uses.
- Build your own assembly before each run; the runner tests whatever is deployed. `source/InGameTests.csproj`
  shows a build target that deploys.

### Helpers

| Kind | Helpers |
|---|---|
| Player action | `Game.AdvanceDays(ctx, n)` runs the clock, clicking through dialogues and popups (`Game.ClickDialogue`, `Game.Unstall`); it keeps a faster speed already running in the fast state. `Game.OpenProfile(girl, tab)`. `Game.CloseAllPopups(ctx)` also waits for closing popups to finish hiding, so the next one can open. `Game.OpenAudition(ctx, type)` holds a free audition and waits for its cards. `Game.NewElection()` starts an election with the new-election popup's choices; `Game.ClickThroughElection(ctx)` clicks through its results popup. `Game.Quicksave(ctx)` saves as F5 does and waits for the file; `Game.Quickload(ctx)` loads it as F9 does, during play. `Game.ToMainMenu(ctx)` leaves for the main menu as the Settings tab's button does (it autosaves first). From the menu, `Game.LoadFromMainMenu(ctx, path)` loads a save as the Load popup does, and `Game.NewGame(ctx)` starts a free-play game with default options. After each scene change they wait until no popup is open and no tween is playing. |
| Lookup | `Game.Main`, `Game.Girl(id)`, `Game.RoomOf(girl)`, `Game.ProfilePopup`, `Game.AuditionPopup`, `Game.TimeControl(state)`, `Game.Saves` (the save manager), `Game.QuicksaveFile` (in the run's `saves\` folder) |
| Scoped setting | `Game.SelectPolicy(type, value)`, `Game.ClockSpeed(minutesPerSecond)`, `Game.Variable(name, value)` (a save variable, where mod settings live), `Game.Option(option, on)` (such as random events): undone on dispose |
| Instrument | `TestTools.WaitFor(ctx, condition, seconds, what)` waits or fails. `TestTools.Spy(method, prefix, postfix)` patches a method ahead of every other patch until disposed, so a prefix sees the caller's arguments and a postfix sees the game's own result. `TestTools.Restore(action)` runs the action on dispose. |

### Adding a helper to the runner

A helper belongs in the runner only if it's exactly one of these kinds:

| Kind | Definition | Check |
|---|---|---|
| Player action | Does what one UI control does | Calls the same game method that control's handler calls |
| Lookup | Returns a game object by the game's own key | Takes a game key (an ID or a type), throws a clear error if it's missing, and changes nothing |
| Scoped setting | Changes one game value for the length of a check | Returns an `IDisposable` that puts the old value back |
| Instrument | Waits on, observes or wraps game code without changing what it computes | Takes its target as a parameter and names no specific game method |

And it passes all of these:
- It names only base-game, Unity and Harmony types: no mod types, no fixture IDs.
- It checks no results: it fails a test only when it can't do its own job (a timeout, a stalled clock).
- It doesn't copy a game formula. A copy belongs in the test that relies on it, where a mismatch shows as a failure.

Anything else stays in the test project that uses it. Player actions, lookups and scoped settings go in
`Game`, instruments in `TestTools`. Runner code that test projects don't call is `internal`.

## Saves and known errors

- `tools/sanitize_save.py` makes a shareable fixture from your own save (renames, swaps modded portrait
  parts, can drop mod variables). Check it with `tools/check_save.py` and one `--load-vanilla` run.
- Errors that don't come from what you're testing can be ignored with one .NET regex per line, matched
  against `message\nstack trace`: in `ignore.txt` (committed) or `ignore.local.txt` (your own setup).

## Options

| Option | Default | |
|---|---|---|
| `--suite` | `smoke`, or `affected` with a command-line scope | Suite to run; `all` runs every suite, `affected` every suite's tests for the mods in scope. |
| `--save` | `fixtures/default.json` | Save to load. |
| `--weeks` | 4 | In-game weeks to advance. |
| `--timescale` | 20 | `Time.timeScale` while advancing. |
| `--timeout` | 300 | Seconds before the game is killed. |
| `--game-dir` | from Steam | Idol Manager folder. |
| `--load-vanilla` | | Load no mods for this run. |
| `--load MOD` | | Load only this mod (folder name, Workshop ID, title or HarmonyID); repeatable. |
| `--scope RULE` | `scope.txt`, `scope.local.txt` | Test only the mods this rule matches; repeatable. Replaces the scope files for the run. |
| `--scope-list FILE` | | Like `--scope`, with the rules read from a file; repeatable. |
| `--allow-input` | | Let keyboard and mouse reach the game. |
| `--sound` | | Don't mute the game. |
| `--skip-runner-build` | | Use the plugin already in the game instead of rebuilding it. |
| `-v` | | Full stack traces and per-mod patch counts. |

`--load-vanilla` and `--load` affect that run only; your mod list isn't changed.

## License

[MIT](LICENSE)
