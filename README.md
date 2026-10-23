# IM-InGameTests

Automated tests that run **inside Idol Manager** with your mods loaded, covering what unit tests
can't: the Unity lifecycle, real game data, and all mods loaded together. One command starts the
game, loads a save, runs the tests, writes the results and quits, in about 40 seconds.
For one-off questions, a [probe session](#probe-sessions) keeps the game running and takes one
command at a time: run the clock to a day, read a value, run until a method is called.

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
3. Tests marked `NeedsSave = false` run first, on the main menu the game boots to.
4. It loads a copy of the test save, `fixtures/default.json` by default. [fixtures/README.md](fixtures/README.md)
   lists what's in it. When no test in the run needs the save, it isn't loaded, and the run ends here.
5. It runs the other chosen tests (see [Choosing what runs](#choosing-what-runs)) one after another in the
   same session. While time advances it closes popups and takes the first choice in dialogues, like a
   player would.
6. Any Unity, BepInEx or Harmony error that isn't on the ignore list fails the test it happened in.
7. It writes `results.json` to `%USERPROFILE%\AppData\LocalLow\Glitch Pitch\Idol Manager\InGameTests\<run>\`
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
| `selftest` | `NoSaveTestRunsOnBootMenu` | A test marked `NeedsSave = false` runs on the boot menu, before any save is loaded. |

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

- A test of the main menu itself takes `[InGameTest(Suite = ..., NeedsSave = false)]`. It runs on the boot
  menu before the save loads, and must leave the game on the main menu. A run where no test needs the
  save skips loading it, which saves about 10 s.
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

## Probe sessions

A probe session keeps the game running and takes one command at a time, so you can look at the
game the way you would with a debugger: run the clock to a day, read a value, run one more day,
read it again, or run until a method is called. A session isn't a test. Nothing passes or fails,
and nothing from it is committed. It answers one-off questions without a new test and a new boot
for each question. When a finding has to stay true, write a test for it.

### Try it

[examples/first-session.txt](examples/first-session.txt) is a short session on the shipped test save,
with a comment on each command. Run it in three steps:

```
python probe_session.py start                              # launches the game; leave this terminal open (it watches the game)
python probe_session.py play examples/first-session.txt    # in a second terminal: each command and its reply
python probe_session.py stop                               # quits the game and checks your saves
```

Part of what `play` prints. Your numbers and dialogue choices will differ: random events and your mods change them.

```
> step 1w
Now 2024-03-31 04:10 (was 2024-03-24 03:50)
2 watched calls:
  2024-03-24 03:50  mainScript.Time_SetState(fast)   from Game.Advance
  2024-03-31 04:10  mainScript.Time_SetState(pause)   from Game.Advance
note: Dialogue choice taken: What is it?
note: Dialogue choice taken: I think I'd like to see that
...

> status
Date:            2024-03-31 04:10   (staticVars.dateTime)
Money:           373,731,306   (resources.Money())
Fans:            675,002   (resources.GetFansTotal())
Fans in groups:  675,002   (the people of every active idol's fan groups, added up)
Fame:            level 10, 731751 points   (resources.GetFameLevel(), resources.Get(fame))
...
```

`step` clicks through dialogues and popups as a test run does, and lists each choice it took.

Then send your own commands one at a time with `python probe_session.py send "<command>"`;
`send help` lists them. [tools/probe-template](tools/probe-template) is the example for a probe, the
C# you run when a command isn't enough (see below).

### Starting and the commands

`start` takes `--save`, `--load`, `--load-vanilla`, `--allow-input`, `--sound` and `--timescale`,
which work as in a test run. With `--no-save` the session stays on the main menu.

The session has the same safety setup as a test run. Saves go to the session's folder, settings and
Steam writes are blocked, input is blocked and the game is muted unless you ask otherwise, and
`stop` checks that your save folder didn't change. The clock is paused between commands.

| Command | What it does |
|---|---|
| `step 3d`, `step 2w`, `step 6h`, `step 1m`, `step to 2024-05-01 [09:00]` | Runs the clock by hours, days, weeks or months, or to a date, then pauses it. Dialogues and popups are clicked through as in a test run. |
| `until <Type.Method> [n] [within 3m]` | Runs the clock until the method has been called n times (default once), and lists the first calls with their arguments and results. It gives up after the game time `within` names (default one year). |
| `until error [within 3m]` | Runs the clock until an error that isn't on the ignore list is logged. |
| `get <path>` | Reads a value: a type, then fields and properties. `[3]` takes a list index or a dictionary key, `[id=3]` takes the items whose `id` reads `3`, and `[*]` takes every item. Examples: `staticVars.dateTime`, `data_girls.girl[id=3]`, `data_girls.girl[*].status`. It never calls methods, so it can't change the game. An object shows its fields, one level deep. |
| `watch <Type.Method>`, `unwatch [<Type.Method>]`, `calls [n]` | Records every call of a method, with its arguments, its result or exception, the game date and the method that called it, in `watch.log`. `calls` shows the last ones. Replies to `step` and `until` list the calls made during them. |
| `status` | The agency's numbers through the game's own methods, each line naming its source: date, money, fans (the game's total, and the idols' fan groups added up), fame, buzz, scandal points and active idols. |
| `patches <Type.Method>` | Lists every Harmony patch on a method: its kind, owner, priority and the order it was applied in. |
| `snap <name>`, `restore <name>`, `diff <a> <b> [regex]` | Quicksaves to a named snap, quickloads one, or lists the save values that differ between two snaps (`now` makes a fresh quicksave). A regex limits the diff to matching paths. A load changes a few values on its own; `SaveLoadTests` lists them. |
| `run <probe.dll> <Class.Method>` | Runs a probe (see below). |
| `state` | Shows the date, the clock, the open popup, what's watched, the snaps and the errors logged so far. |
| `help`, `quit` | Lists the commands; quits the game. |

`Type.Method` names every overload of that method declared on that type. A nested type is written
with dots (`data_girls.girls`), and a property getter is `get_Name`. A watch only records; it
changes no argument or result. Its prefix runs before every other prefix, so it sees the arguments
as the caller passed them, and its finalizer runs after every postfix, so it sees the final result.
A very small method, such as a plain getter, can be inlined into code the game compiled before the
watch, and those calls then don't show. Watch its caller instead.

Besides `send`, the launcher has these:

| Command | What it does |
|---|---|
| `play <script>` | Sends a file's commands in order and prints each reply. Lines starting with `#` are comments, and `wait 2s` waits. It stops at the first error. |
| `eval "<C# statements>"` | Runs the statements as a one-off probe, for anything that needs a method call, such as `eval "ctx.Record(\"money\", resources.Money());"`. It's built in `probes/_eval/`. |
| `restart` | Snaps the game, rebuilds the runner, and starts again from the snap with the same options and watches, so a change to the runner doesn't cost the game in progress. The new watchdog writes to `InGameTests\watchdog.log`. |
| `cancel`, `tail [n]` | Stops the running command; prints the end of the transcript. |

**Probes** are for anything a command can't do. A probe is a method with a test's signature,
`static IEnumerator Name(TestContext ctx)`, in a small project of its own. Copy `tools/probe-template`
to `probes/<name>/`, which isn't committed, and run it with
`python probe_session.py run probes/<name> Probes.Example`. That builds the project and runs the method.
The helpers in [Helpers](#helpers) work in a probe. `ctx.Record` and `ctx.Note` go into the reply, and
`ctx.Fail` shows as an error. A rebuilt probe loads again under the same name. A probe that should
stay becomes a test when you add `[InGameTest]` and move it to a test project.

**Limits.** A frozen game can't stop itself, so the `start` process watches it from outside.
`send` and `stop` work without it and fail with a message instead of waiting forever.

| If | Then |
|---|---|
| A running command passes its real-time limit (`--command-limit`, default 10 min), or you run `probe_session.py cancel` | It stops wherever it is, nested helpers included, runs their cleanup and replies `stopped`. |
| `until` never sees its calls or its error | It stops at its game-time limit and replies `stopped`. |
| The clock stops, or a dialogue never ends | The command reports it, as `AdvanceDays` does in a test. |
| The game stops writing `heartbeat.txt` for 30 s (90 s while starting) | The watchdog kills the game and writes `killed.txt` with the last command and the end of the transcript. |
| No command arrives for `--idle` minutes (default 30), or the session reaches `--max` minutes (default 120) | The game quits. The watchdog kills it a minute later if it doesn't. |
| `send` gets no reply within the command limit plus 30 s | It prints how old the heartbeat is and whether the game runs, and exits. |
| `quit` doesn't end the game within 30 s | `stop` kills it. The save check runs either way. |

Exit codes: 0 when the reply is `ok` or `stopped`, 1 for `error`, 2 when there is no session or no reply.
Each session has a folder in `...\Idol Manager\InGameTests\session-<time>\`. It holds `transcript.txt`
(every command and reply), `watch.log`, the snaps and the session's saves.

### Player input, for exceptional cases

The commands above work on the game's code directly, and that's what a session is for. When the
question is the input path itself, such as a bug that only happens when a person clicks, or a mod's
screen that misbehaves, a session can also play like a person. Nothing uses this unless you send
these commands:

| Command | What it does |
|---|---|
| `buttons [regex]` | Lists what a click could reach right now: each control whose centre isn't covered, with its label, kind and place. A regex filters the list; the numbers stay those of the whole list. |
| `click #n`, `click <label>` | Moves the virtual mouse to the control and clicks it. |
| `drag <from> to <to>` | Presses on one control, holds, moves to the other and releases. |
| `key <key>` | Presses a key for one frame, such as `key F5` or `key Space`. |
| `type <text>` | Types into the text field that has focus (click it first), then presses Enter. |

The mouse and keys are virtual, at the level of Unity's `Input`, which the input block patches
anyway. So Unity's own input module makes the hover, press, click and drag events, and game code that
reads the mouse itself (context menus open at the mouse, drags follow it) sees the same as for a
person. The mouse stays where it last clicked. During a session the game window counts as focused,
because Unity ignores the mouse in an unfocused window. These commands need input blocked, so they
don't work with `--allow-input`.

Things to know:
- A control isn't always where it looks: a staffer is dragged by its drag handle, not by its name.
  When an input does nothing, `watch` the method you expect and try again.
- A control's label is the text on it, or its object's name. The text is not a meaning: read the
  value with `get` or `status` before saying what a number is.
- Right after a scene loads, a fade can cover everything; `buttons` then says what covers the screen.

The clock is paused again after every command, so a speed button or the Space key doesn't keep it
running; use `step`.

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

## Testing the runner

`dotnet test tests/UnitTests` covers the session's command parsing, path reading, labels and
coroutine stepping. `python -m unittest discover tests` covers the launcher: the watchdog, scripts,
`eval` and `restart`. Neither needs the game.

`python probe_session.py selftest` runs a session and checks what only the game shows:
- commands are picked up while the clock is paused;
- watches see arguments, results and callers;
- a step and a restore move the date;
- a game-time limit stops `until`;
- a probe and `eval` load;
- `status` matches the game's own money;
- a virtual click opens a panel, and `key F5` makes the game quicksave;
- the example script runs;
- `restart` keeps the date and the watches;
- your saves are unchanged.

## License

[MIT](LICENSE)
