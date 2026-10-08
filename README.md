# IM-InGameTests

Automated tests that run **inside Idol Manager** with your mods loaded. No clicking needed.

Unit tests outside the game can't cover everything: the Unity lifecycle, real game data and saves,
how mods behave when they're all loaded together, or a game update breaking a mod at runtime.
IM-InGameTests starts the real game, loads a save, runs tests as coroutines, writes the results
to a file and quits. A typical smoke run takes about 40 seconds.

```
python run_ingame_tests.py                  # smoke suite: load the save, run 4 in-game weeks
python run_ingame_tests.py --weeks 12       # longer run
python run_ingame_tests.py --suite all -v   # every suite, full stack traces
python run_ingame_tests.py --vanilla        # same tests with every mod disabled, as a baseline
python run_ingame_tests.py --only "My Mod"  # only this mod enabled (repeat --only for more)
```

```
  [PASS] bootstrap  (14.4s)
  [PASS] SmokeTests.EveryEnabledHarmonyModIsPatched  (0.0s)
  [PASS] SmokeTests.AdvanceWeeks  (21.0s)
         note: Dialogue choice taken: Feel free to talk to me about any problems you have.
         note: Auto-resumed clock 1x: closed popup (... popup=girl_birthday)
         newDayEvents = 28
         newWeekEvents = 4
PASSED
```

The exit code is 0 when everything passes, 1 when a test fails, and 2 when no results came back (crash, timeout or setup problem).
That makes it usable from scripts and pre-release checks.

## Requirements

- Windows, with Idol Manager installed through Steam. Steam must be running.
- [BepInEx 5](https://github.com/BepInEx/BepInEx) installed in the game folder.
- [.NET SDK](https://dotnet.microsoft.com/download) to build the plugin. It targets .NET Framework 4.6, and reference assemblies come from NuGet.
- Python 3.10 or later.

The game folder is found automatically from your Steam libraries. If that fails, pass `--game-dir`.
The build compiles against the game's own DLLs in `IM_Data\Managed`.

## Setup

Close the game, then run `python run_ingame_tests.py`. A mid-game test save comes included
(`fixtures/default.json`). To use your own save instead, see [fixtures/README.md](fixtures/README.md).

The script builds the plugin and copies it to `<game>\BepInEx\plugins\InGameTests\`. Next it launches
`IM.exe -imtest <suite> ...` and waits for `results.json`. The game window opens and closes on its own.

The plugin stays installed, but **it does nothing unless the game is launched with `-imtest`**, so normal play is unaffected.

## What happens during a run

1. **Safety first.** In test mode the plugin blocks every save write (`SaveData`, `SaveGlobalData`, `SaveChapter`,
   `FixSaveFile`) and every Steam achievement and stat write. A test run never touches your saves or achievements.
2. **Hands off, sound off.** Keyboard and mouse input to the game is ignored, so a stray click or keypress
   can't change a run. For example, Space pauses and Escape opens the menu. You can keep working while
   it runs. The game is also muted through its own volume settings, and audio still plays at zero volume, so timing is
   unchanged. Use `--allow-input` or `--sound` to turn either off.
3. **Error capture.** Every Unity error and uncaught exception is recorded with its stack trace, starting from boot.
   BepInEx errors are recorded too, including Harmony's. Any error that isn't on the ignore list fails the test it happened in.
4. **Load.** Once the main menu is up and mods have loaded, the plugin loads a copy of the fixture save.
5. **Tests.** Every `[InGameTest]` method in the chosen suite runs, all in one game session.
6. **Report.** `results.json` is written to `%USERPROFILE%\AppData\LocalLow\Glitch Pitch\Idol Manager\InGameTests\<run>\`
   along with the staged save. Then the game quits.

While time is advancing, the runner acts the way a player would:
- closes popups,
- clicks through dialogues, always taking the first choice,
- raises `Time.timeScale` (default 20) so weeks pass in seconds.

Random events differ from run to run, so a longer run covers more situations.

## Included tests

| Suite | Test | Checks |
|---|---|---|
| `smoke` | `EveryEnabledHarmonyModIsPatched` | Every enabled mod with a `HarmonyID` in its `info.json` has its `<HarmonyID>.dll` and at least one Harmony patch applied. |
| `smoke` | `AdvanceWeeks` | N in-game weeks pass with no errors, and the date, `onNewDay` and `onNewWeek` counts add up. Fails within seconds if an exception kills the game clock. |
| `selftest` | `DialogueClickThroughFinishesDialogue` | Starts a dialogue that has a choice and checks that the runner's click-through gets it to the end. |
| `selftest` | `InputBlockedAndMuted` | The input block and mute are in place. |

`AdvanceWeeks` also catches the most common way a broken mod shows up in play. An exception in a
day or tick handler stops the game's `TimeProgress` coroutine, and the clock freezes for good.

## Writing tests

Add a static coroutine anywhere in the plugin, or in your own assembly named `InGameTests.*`
placed in the same plugins folder:

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
    ctx.Record("advancedBy", staticVars.dateTime - before);   // shown under the test in the report
}
```

- `ctx.Assert`, `ctx.Fail`: mark the test failed with a message.
- `ctx.Note`, `ctx.Record`: add information to the report.
- `ctx.Weeks`, `ctx.TimeScale`: the values passed on the command line.
- Use `WaitForSecondsRealtime`, not `WaitForSeconds`, because `Time.timeScale` may be raised.
- Drive the game by calling its methods, not through `UnityEngine.Input`, which is blocked during runs.
- The game ships a stripped Unity, so a Unity API that compiles can still be missing at runtime
  (`MissingMethodException`). For example, `AudioListener.volume` can't be set. Prefer APIs the game itself uses.
- Run a suite with `--suite mymod`, or every suite with `--suite all`.

## Choosing mods (`--vanilla`, `--only`)

By default a run uses the mods enabled in the game. Both options below apply to that run only. Your mod
list isn't changed, because settings writes are blocked in test mode.

- `--vanilla` disables every mod. Use it to tell whether a failure comes from the game itself or from a
  mod, and to check that a save works without any mods installed.
- `--only MOD` enables just that mod, even if it's disabled in the game. Repeat it to test mods
  together, e.g. a mod and its dependency. `MOD` can be the folder name, Workshop ID, title or
  HarmonyID, ignoring case. A name that matches no installed mod fails the run, so a typo can't
  silently test nothing. The report lists the mods that were enabled.

## Sharing saves

`tools/sanitize_save.py` turns a save into a fixture you can share. It:
- replaces the player's name, group names and save timestamp,
- swaps portrait parts from mods for base-game parts of the same body,
- can drop idol variables written by mods (`--drop-girl-variable REGEX`),
- prints anything that still looks like a file path or Workshop reference.

Then check the result with `tools/check_save.py`, which finds broken references and other problems
that crash or quietly break a load, and run it once with `--vanilla`.

## Ignoring known errors

Some errors don't come from the mod you're testing. Errors the unmodded game always logs are built in
(`LogCapture.Ignored`). For anything else, add one .NET regex per line. Each pattern is matched against
`message\nstack trace`.

- `ignore.txt` is committed. Use it for patterns that apply to everyone.
- `ignore.local.txt` isn't committed. Use it for errors from your own setup, such as a broken mod
  you have installed from the Workshop.

## Command-line options

| Option | Default | |
|---|---|---|
| `--suite` | `smoke` | Suite to run. `all` runs every suite. |
| `--save` | `fixtures/default.json` | Save to load. |
| `--weeks` | 4 | In-game weeks to advance. |
| `--timescale` | 20 | `Time.timeScale` while advancing. |
| `--timeout` | 300 | Seconds before the game is killed. |
| `--game-dir` | from Steam | Idol Manager install folder. |
| `--vanilla` | | Run with every mod disabled. |
| `--only` | | Enable only this mod (repeatable). |
| `--allow-input` | | Let keyboard and mouse input reach the game. |
| `--sound` | | Don't mute the game. |
| `--no-build` | | Skip building the plugin. |
| `-v` | | Full stack traces and per-mod patch counts. |

## Layout

```
run_ingame_tests.py      host script: build, stage, launch, wait, report
ignore.txt               shared error-ignore patterns
fixtures/default.json    included test save (sanitized; other saves here are not committed)
tools/sanitize_save.py   make a shareable fixture from your own save
tools/check_save.py      check a save for broken references before using it
tools/savefile.py        shared save reading and writing
source/
  Plugin.cs              BepInEx entry point; inert unless -imtest
  Runner.cs              bootstrap (menu -> load save), test discovery, [InGameTest], TestContext
  SafetyPatches.cs       blocks saves and achievements in test mode
  ModFilter.cs           --vanilla / --only mod selection
  InputBlock.cs          ignores keyboard and mouse during runs
  Mute.cs                zero game volume during runs
  LogCapture.cs          Unity + BepInEx error capture, ignore list
  ResultsWriter.cs       results.json
  Tests/SmokeTests.cs    smoke suite and the player stand-ins (popups, dialogue clicks)
  Tests/SelfTests.cs     checks of the runner itself
```

## License

[MIT](LICENSE)
