# IM-InGameTests

Tests that run **inside Idol Manager** with your mods loaded, for what unit tests can't reach: the Unity
lifecycle, real game data and all mods together. One command boots the game, loads a save, runs the
tests and quits, in about 40 seconds. For one-off questions, a [probe session](#probe-sessions) keeps
the game running and takes commands one at a time.

```
python run_ingame_tests.py                   # smoke suite: load the save, run 4 in-game weeks
python run_ingame_tests.py --load-vanilla    # the same with no mods, as a baseline
python run_ingame_tests.py --load "My Mod"   # only this mod loaded (repeatable)
python run_ingame_tests.py --scope "My Mod"  # every suite's tests for this mod, all mods loaded
python run_ingame_tests.py --suite menu      # through the main menu (not in --scope runs)
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

The game folder is found from your Steam libraries, or pass `--game-dir`.

## How a run works

1. The script builds the plugin into `<game>\BepInEx\plugins\InGameTests\` and launches the game with
   `-imtest`. Without that flag the plugin does nothing.
2. Saves go to the run's own folder, so save code runs for real without touching your saves. Settings and
   achievement writes are blocked, input is blocked and the game is muted (`--allow-input` and `--sound`
   undo the last two). Afterwards the script fails the run if anything in `...\Idol Manager\data\` changed.
3. Tests marked `NeedsSave = false` run first, on the boot menu.
4. A copy of the test save loads (`fixtures/default.json`; [fixtures/README.md](fixtures/README.md) lists
   what's in it), unless no test needs it.
5. The other tests run in the same game. While time advances, popups are closed and dialogues take their
   first choice, as a player clicking through would.
6. Any Unity, BepInEx or Harmony error that isn't on the ignore list fails the test it happened in.
7. The plugin writes `results.json` to `%USERPROFILE%\AppData\LocalLow\Glitch Pitch\Idol Manager\InGameTests\<run>\`
   and quits.

## Included tests

| Suite | Test | Checks |
|---|---|---|
| `smoke` | `EveryPatchMethodIsApplied` | Every enabled mod's DLL is loaded and each patch it declares is applied under its HarmonyID. Harmony skips types that fail to load and only logs it. |
| `smoke` | `EveryTranspilerChangesIL` | Each mod's transpilers change their method's IL. A transpiler whose search finds nothing usually leaves the code as it was, with no error. |
| `smoke` | `AdvanceWeeks` | N weeks pass with no errors, and the clock keeps running. |
| `smoke` | `QuicksaveQuickloadRoundTrip` | A quicksave, a quickload and a second quicksave give the same save, apart from what the base game itself changes on a load; then a day runs. This catches mod data that isn't saved and load hooks that change state. |
| `menu` | `MainMenuRoundTrip` | The same round trip through the main menu, which starts a new scene, so it catches mod state kept in statics. |
| `menu` | `NewGameRunsToItsFirstWeek` | A new free-play game runs to its first Monday with no errors, so mods' new-game setup runs. It runs last, because it replaces the test save's game. |
| `selftest` | `DialogueClickThroughFinishesDialogue`, `InputBlockedAndMuted`, `NoSaveTestRunsOnBootMenu` | The runner's own dialogue clicking, input blocking and boot-menu tests work. |

## Choosing what runs

| Question | Options |
|---|---|
| Which mods does the game load? | `--load MOD` (repeatable) or `--load-vanilla`. Default: the mods enabled in game. |
| Whose tests run? | `--scope RULE` or `--scope-list FILE` (repeatable). Default: `scope.txt` and `scope.local.txt`. |
| Which tests? | `--suite NAME`. Default: `affected` with a scope on the command line, otherwise `smoke`. |

- **Suites.** Each test names one suite with `[InGameTest(Suite = ...)]`. Name a suite after its scenario
  (`smoke`, `auditions`), never after a mod. `all` runs every suite.
- **Mods under test.** `[ModUnderTest("<HarmonyID>")]` on a test class runs its tests only while that mod is
  in scope. `[ModUnderTest(ModUnderTestAttribute.EveryMod)]` marks checks that judge every mod in scope.
  Tests in an untagged class run whenever their suite does.
- **Scope.** One .NET regex per line, matched against each mod's HarmonyID, title and folder name (a
  Workshop ID for Workshop mods). `scope.local.txt` is for your own setup and isn't committed; `--scope`
  and `--scope-list` replace both files for one run. Every mod stays loaded either way. A rule that
  matches no installed mod fails the run.
- **`affected`** runs every suite's tests for the mods in scope and no general tests, so after changing a
  mod, `--scope "<Mod>"` runs everything that tests it in one boot.

## Writing tests

Write a static coroutine in the plugin, or in your own assembly named `InGameTests.*` in the same
plugins folder. The runner loads those in test mode; they need no BepInEx plugin class.

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

- `ctx.Assert` and `ctx.Fail` fail the test; `ctx.Note` and `ctx.Record` add to the report.
- A test of the main menu takes `NeedsSave = false`. It runs before the save loads and must leave the game
  on the main menu. A run where no test needs the save skips loading it.
- `ModScope.Includes(harmonyId)` tells a check that judges every mod which ones are in scope.
- Use `WaitForSecondsRealtime`, since `Time.timeScale` may be raised. Call game methods, since
  `UnityEngine.Input` is blocked.
- The game's Unity is stripped, so an API that compiles may be missing at runtime. Prefer APIs the game uses.
- The runner tests whatever is deployed, so build your assembly before each run. `source/InGameTests.csproj`
  shows a build target that deploys.

### Helpers

| Kind | Helpers |
|---|---|
| Player action | `Game.AdvanceDays(ctx, n)` runs the clock, clicking through dialogues and closing popups. `Game.OpenProfile`, `Game.CloseAllPopups`, `Game.OpenAudition` (a free audition), `Game.NewElection`, `Game.ClickThroughElection`, `Game.Quicksave` and `Game.Quickload` (F5 and F9), `Game.ToMainMenu`, `Game.LoadFromMainMenu` and `Game.NewGame`. Each waits until the game has finished reacting. |
| Lookup | `Game.Main`, `Game.Girl(id)`, `Game.RoomOf(girl)`, `Game.ProfilePopup`, `Game.AuditionPopup`, `Game.TimeControl(state)`, `Game.Saves`, `Game.QuicksaveFile` |
| Scoped setting | `Game.SelectPolicy`, `Game.ClockSpeed`, `Game.Variable` (a save variable, where mod settings live) and `Game.Option`; each is undone on dispose. |
| Instrument | `TestTools.WaitFor` waits or fails. `TestTools.Spy(method, prefix, postfix)` patches a method ahead of every other patch until disposed. `TestTools.Restore(action)` runs the action on dispose. |

### Adding a helper to the runner

A runner helper is exactly one of these kinds:

| Kind | It must |
|---|---|
| Player action | Call the same game method as the UI control it stands for |
| Lookup | Take a game key (an ID or a type), throw a clear error if it's missing, and change nothing |
| Scoped setting | Return an `IDisposable` that puts the old value back |
| Instrument | Wait on, observe or wrap game code without changing what it computes, with its target as a parameter |

It also names only base-game, Unity and Harmony types, fails a test only when it can't do its own job,
and copies no game formula. Anything else stays in the test project that uses it. Player actions,
lookups and scoped settings go in `Game`, instruments in `TestTools`.

## Probe sessions

A probe session keeps the game running and takes one command at a time, as a debugger would: run the
clock, read a value, run until a method is called. It isn't a test: nothing passes or fails. When a
finding has to stay true, write a test for it.

### Try it

```
python probe_session.py start                              # launches the game; keep this terminal open
python probe_session.py play examples/first-session.txt    # in a second terminal
python probe_session.py stop                               # quits the game and checks your saves
```

[examples/first-session.txt](examples/first-session.txt) explains each of its commands. Part of the
output (your numbers will differ, since random events and your mods change them):

```
> step 1w
Now 2024-03-31 04:10 (was 2024-03-24 03:50)
2 watched calls:
  2024-03-24 03:50  mainScript.Time_SetState(fast)   from Game.Advance
  2024-03-31 04:10  mainScript.Time_SetState(pause)   from Game.Advance
...
> status
Money:           373,731,306   (resources.Money())
Fans:            675,002   (resources.GetFansTotal())
...
```

To send one command, use `python probe_session.py send "<command>"`; `send help` lists them all.

### Commands

`start` takes the test run's `--save`, `--load`, `--load-vanilla`, `--allow-input`, `--sound` and
`--timescale`, and `--no-save` stays on the main menu. The safety setup is the same as a test run's,
and the clock is paused between commands.

| Command | What it does |
|---|---|
| `step 3d`, `2w`, `6h`, `1m`, or `step to 2024-05-01 [09:00]` | Runs the clock, then pauses it. Popups and dialogues are handled as in a test, and the reply lists each choice taken. |
| `until <Type.Method> [n] [within 3m]`, `until error [within 3m]` | Runs the clock until the method has been called n times or an error is logged. It gives up after the game time `within` gives (default one year). |
| `get <path>` | Reads a value, such as `staticVars.dateTime`, `data_girls.girl[id=3]` or `data_girls.girl[*].status`. It never calls methods. |
| `status` | Shows date, money, fans, fame, buzz, scandal points and idols, each with the game method it comes from. |
| `watch <Type.Method>`, `unwatch`, `calls [n]` | Logs every call with its arguments, result, date and caller to `watch.log`; `calls` shows the last ones. |
| `patches <Type.Method>` | Lists every Harmony patch on a method, with its owner and priority. |
| `snap <name>`, `restore <name>`, `diff <a> <b> [regex]` | Saves a named quicksave, loads one, or lists the save values that differ (`now` is a fresh quicksave). |
| `run <probe.dll> <Class.Method>` | Runs a probe. |
| `state`, `help`, `quit` | Shows the clock, watches, snaps and errors; lists the commands; quits. |

`Type.Method` covers every overload, and nested types use dots (`data_girls.girls`). A watch only
records. A very small method can be inlined into code compiled before the watch; watch its caller then.

The launcher also has:

| Command | What it does |
|---|---|
| `play <script>` | Sends a file's commands in order, with `#` comments and `wait 2s` lines, and stops at the first error. |
| `eval "<C# statements>"` | Runs statements as a one-off probe, such as `eval "ctx.Record(\"money\", resources.Money());"`. |
| `restart` | Snaps the game, rebuilds the runner, and starts again from the snap with the same watches. |
| `cancel`, `tail [n]` | Stops the running command; shows the end of the transcript. |

**Probes** run C# for what a command can't do. A probe is a method `static IEnumerator Name(TestContext ctx)`
in a project copied from `tools/probe-template` to `probes/<name>/`, which isn't committed. Run it with
`python probe_session.py run probes/<name> Probes.Example`. The test helpers work in probes, and
`ctx.Record` and `ctx.Note` go into the reply. Adding `[InGameTest]` turns a probe into a test.

**Limits.** The `start` process watches the game from outside, so nothing can hang forever:
- A command stops at its real-time limit (`--command-limit`, default 10 min) or on `cancel`, and cleans up.
- The watchdog kills a game whose heartbeat stops for 30 s (90 s while starting) and writes `killed.txt`.
- The game quits after `--idle` minutes without a command (default 30) or after `--max` minutes (default 120).
- `send` and `stop` fail with a message instead of waiting.

Exit codes: 0 for `ok` or `stopped`, 1 for `error`, 2 for no session or no reply. Each session's folder,
`...\Idol Manager\InGameTests\session-<time>\`, holds the transcript, `watch.log`, the snaps and the saves.

### Player input, for exceptional cases

Use these only when the input path is the question, such as a bug that needs a real click or a mod screen
that misbehaves.

| Command | What it does |
|---|---|
| `buttons [regex]` | Lists, numbered, what a click could reach right now. |
| `click #n`, `click <label>` | Clicks it. |
| `drag <from> to <to>` | Presses on one, moves to the other and releases. |
| `key <key>` | Presses a key for one frame, such as `F5` or `Space`. |
| `type <text>` | Types into the text field that has focus, then presses Enter. |

The mouse and keys are virtual, at the level of Unity's `Input`, so the game sees them as a player's:
context menus open at the mouse, and drags follow it. The game window counts as focused during a
session. These commands need input blocked, so they don't work with `--allow-input`. A control's drag
handle can be a separate control (staff are dragged by theirs), and a label is only text: check a value
with `get` or `status` before saying what it means.

## Saves and known errors

- `tools/sanitize_save.py` makes a shareable test save from your own (renames, swaps modded portrait parts,
  can drop mod variables). Check it with `tools/check_save.py` and one `--load-vanilla` run.
- To ignore errors that don't come from what you test, add one .NET regex per line, matched against
  `message\nstack trace`, to `ignore.txt` (committed) or `ignore.local.txt` (your own setup).

## Options

| Option | Default | |
|---|---|---|
| `--suite` | `smoke`, or `affected` with a scope | `all` runs every suite; `affected` every suite's tests for the mods in scope. |
| `--save` | `fixtures/default.json` | Save to load. |
| `--weeks` | 4 | In-game weeks to advance. |
| `--timescale` | 20 | `Time.timeScale` while advancing. |
| `--timeout` | 300 | Seconds before the game is killed. |
| `--game-dir` | from Steam | Idol Manager folder. |
| `--load-vanilla` | | Load no mods for this run. |
| `--load MOD` | | Load only this mod (folder name, Workshop ID, title or HarmonyID); repeatable. |
| `--scope RULE` | `scope.txt`, `scope.local.txt` | Test only the mods this rule matches; repeatable. |
| `--scope-list FILE` | | Like `--scope`, with the rules read from a file; repeatable. |
| `--allow-input` | | Let keyboard and mouse reach the game. |
| `--sound` | | Don't mute the game. |
| `--skip-runner-build` | | Use the plugin already in the game. |
| `-v` | | Full stack traces and per-mod patch counts. |

`--load-vanilla` and `--load` affect that run only; your mod list isn't changed.

## Testing the runner

`dotnet test tests/UnitTests` and `python -m unittest discover tests` need no game.
`python probe_session.py selftest` checks a session in the game: commands, watches, stepping and
restoring, limits, probes and `eval`, `status`, virtual input, the example script and `restart`, and
that your saves are unchanged.

## License

[MIT](LICENSE)
