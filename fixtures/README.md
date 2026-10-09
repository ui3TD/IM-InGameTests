`default.json` is the save the tests load unless you pass `--save <path>`. It's a freeplay save
from game v1.0.6 on hard difficulty, dated 2024-03-24 03:50 in game. It holds the states below,
so a test can start from them instead of building them in code. It passes every suite with every
mod disabled (`--load-vanilla`), including a 12-week run.

## What's in it

The IDs are stable. A test can rely on them.

| Area | State | IDs |
|---|---|---|
| Idols | normal | 1, 3, 9, 11, 12, 71, 222, 223, 224, 225 |
| | training in the dance studio (practice status) | 167 |
| | injured, being treated by the physician (5 days) | 13 |
| | depressed, untreated (the psychiatrist is free) | 6 |
| | on hiatus until 2024-05-26 | 8 |
| | announced graduation, graduates 2024-05-26 | 73 |
| | graduated | 4 |
| | newly hired in March (fresh stats, no singles yet) | 222, 223, 224, 225 |
| | active wish: a single / a show | 1 / 9 |
| Groups | main group 0 | 1, 3, 6, 8, 11, 13, 71, 73, 167, 222, 223 |
| | sister group 1 | 4, 9, 12, 224, 225 |
| Relationships | idols dating each other | 71 + 222 |
| | dating the player (fun route, casual) | 12 |
| | outside boyfriend the player knows about | 9 (unknown: 4, 8, 167) |
| | hate each other | 3 + 13 |
| | clique led by 11 bullies 224, and the player knows | |
| | mentor (senpai → kohai) | 11 → 223 |
| | pushed idols (days pushed) | 1 (0), 12 (15), 222 (28) |
| Staff | player 1, sales managers 16 and 47 (47 has a proposal waiting), music producer 67, choreographer 76, physician 104, psychiatrist 107, stylist 108, production manager 109 | |
| Agency | 9 floors with every kind of room, each staffable room staffed | |
| | theater 0 for group 0: every kind of day, subscribers, streaming, equipment | |
| | theater 1 for group 1: performances and manzai, no subscribers yet | |
| | cafe 0 for group 0: 3 dishes on the menu, 6 idols on wait staff | |
| Singles | ready to release | 32 |
| | in production in the recording studio (finishes 2024-03-25) | 33 |
| | just started, no progress | 34 |
| | 25 released, the latest on 2023-11-04 | |
| Shows | TV, airing | 5 |
| | internet, airing | 2 |
| | radio, relaunching | 1 |
| | in development | 6 |
| | cancelled (TV, 24 episodes each) | 3, 4 |
| Special events | world tour, fully produced, ready to launch | tour 3 |
| | stadium concert, fully produced, ready to launch | concert 8 |
| | finished election (July 2023): single 27, concert 6, 9 ranked idols | election 1 |
| | 2 finished tours, 4 finished concerts; Coliseum unlocked | |
| Money | ¥382M; a 3-month ¥10M bank loan paid weekly until 2024-06-04 | loan 1 |
| Business | idol 3 has a TV drama contract until 2024-06-24 | |
| Options | substories, random events and dating scandals on, as in a new game | |

### How the states behave during a run
- The tour and concert sit **in production but idle**, so they stay put while time passes.
  A test launches them.
- The **training, treatment and single production** finish within the default 4-week run.
  That covers the daily work and the completion code.
- The **hiatus, the announced graduation and the loan** last past 4 weeks. A `--weeks 12` run
  reaches all three.
- **Random events and substories** make runs vary. A test that needs a steady run can turn them off:

  ```csharp
  staticVars.PlayerData.GetOption(staticVars._playerData._options.randomEvents).Val = false;
  staticVars.PlayerData.GetOption(staticVars._playerData._options.substories).Val = false;
  staticVars.PlayerData.GetOption(staticVars._playerData._options.datingScandals).Val = false;
  ```

### Left out on purpose
- **An election in production.** It links a single, a concert and the election; a missing link
  throws during load. It would also take the concert slot.
- **A running audition.** Candidates aren't saved; call `Auditions.GenerateAudition` instead.
- **A concert in the middle of its minigame.** The game can't save that.
- **Story mode, active events and queued substories.** These fire dialogues at load.
- **Awards for 2024.** The ceremony is on 15 July.

## Changing it

The script `tools/build_fixture.py` adds these states. Each of its steps sets absolute values and
inserts or replaces records by ID, so it can be re-run on its own output (`--list` shows the steps):

```
python tools/build_fixture.py fixtures/default.json fixtures/default.json
```

To add a state, add a step. Clone an existing record of the same kind, and set the fields the way
the game's own code would.

`tools/check_save.py` checks a save for problems that crash or quietly break a load. These include:
- IDs that point at nothing;
- rooms that don't match the idol or staffer in them;
- floors of the wrong width;
- counters below the IDs in use;
- portrait parts the base game doesn't have.

The build script runs the check before writing. Run it on your own fixtures too.

## Using your own save

To test with your own save (e.g. one where your mod's feature is in use), clean and check it the same way:

```
python tools/sanitize_save.py "%USERPROFILE%\AppData\LocalLow\Glitch Pitch\Idol Manager\data\auto_save.json" fixtures/mine.json
python tools/check_save.py fixtures/mine.json
python run_ingame_tests.py --save fixtures/mine.json
```

Other `.json` files in this folder are ignored by git, so your personal saves stay local.
