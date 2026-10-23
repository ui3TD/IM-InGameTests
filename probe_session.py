"""Probe sessions: keep Idol Manager running and drive it one command at a time.

A session is not a test: nothing passes or fails. It answers one-off questions, such as what a
value is on a given day, who calls a method, or what happens when the clock runs to a date.
It uses the test runner's setup: saves go to the session's own folder, settings and Steam
writes are blocked, input is blocked and the game is muted unless you ask otherwise.

    python probe_session.py start                 # launch and watch the game; leave this running
    python probe_session.py send "step 3d"        # one command; prints the reply
    python probe_session.py send help             # every command
    python probe_session.py cancel                # stop the command that is running
    python probe_session.py play examples/first-session.txt     # a script of commands
    python probe_session.py eval "ctx.Record(\\"money\\", resources.Money());"   # C# statements as a probe
    python probe_session.py run probes/my-probe Probes.Count   # build and run a probe project
    python probe_session.py restart               # rebuild the runner without losing the game
    python probe_session.py tail                  # the end of the transcript
    python probe_session.py stop                  # quit the game and check your saves
    python probe_session.py selftest              # one session that checks the session itself

Exit codes: 0 ok (a command that was stopped by a limit is ok too), 1 the command failed,
2 no session or no reply.
"""

import argparse
import datetime
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

import run_ingame_tests as runner

HERE = Path(__file__).resolve().parent
SESSIONS = runner.LOCALLOW / "InGameTests"
STATE_FILE = SESSIONS / "session.json"

# Watchdog limits, in seconds.
START_LIMIT = 60          # the plugin writes started.txt in Awake
STARTUP_HEARTBEAT = 90    # boot and save load can block the main thread for a while
HEARTBEAT_LIMIT = 30      # after that, a silent heartbeat means a frozen game
READY_LIMIT = 300         # boot to menu plus the save load
BACKUP_GRACE = 60         # the plugin quits on idle/max itself; the watchdog steps in this much later
QUIT_WAIT = 30            # stop: how long quit may take before the game is killed
SEND_GRACE = 30           # send: on top of the command limit


def watchdog_action(now: float, *, started_at: float, started: bool, ready: bool, heartbeat_at: float | None,
                    alive: bool, last_activity_at: float, idle_s: float, max_s: float) -> tuple[str, str] | None:
    """What the watchdog must do now: ("exited", reason), ("kill", reason) or None to keep watching."""
    if not alive:
        return "exited", "the game exited"
    if not started:
        if now - started_at > START_LIMIT:
            return "kill", f"the runner did not start within {START_LIMIT} s (is BepInEx loading plugins?)"
        return None
    if heartbeat_at is not None:
        silent = now - heartbeat_at
        limit = HEARTBEAT_LIMIT if ready else STARTUP_HEARTBEAT
        if silent > limit:
            return "kill", f"frozen: no heartbeat for {silent:.0f} s (limit {limit} s)"
    elif now - started_at > STARTUP_HEARTBEAT:
        return "kill", f"frozen: no heartbeat at all after {now - started_at:.0f} s"
    if not ready:
        if now - started_at > READY_LIMIT:
            return "kill", f"not ready after {READY_LIMIT} s"
        return None
    if now - last_activity_at > idle_s + BACKUP_GRACE:
        return "kill", f"idle for {(now - last_activity_at) / 60:.0f} min and the game didn't quit itself"
    if now - started_at > max_s + BACKUP_GRACE:
        return "kill", f"past the {max_s / 60:.0f} min maximum and the game didn't quit itself"
    return None


def reply_exit_code(status: str) -> int:
    """ok and stopped (a limit or a cancel, an answer in itself) are 0; error is 1."""
    return 1 if status == "error" else 0


def parse_reply(text: str) -> tuple[str, str]:
    status, _, body = text.partition("\n")
    return status.strip(), body


def allocate_command(cmd_dir: Path, line: str) -> int:
    """Writes the command as the next NNNN.txt without overwriting one another sender wrote."""
    cmd_dir.mkdir(parents=True, exist_ok=True)
    while True:
        numbers = [int(m.group(1)) for p in cmd_dir.iterdir() if (m := re.fullmatch(r"(\d{4,})\.(txt|tmp)", p.name))]
        n = max(numbers, default=0) + 1
        tmp = cmd_dir / f"{n:04d}.tmp"
        final = cmd_dir / f"{n:04d}.txt"
        try:
            fd = os.open(tmp, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except FileExistsError:
            continue
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(line)
        if final.exists():
            tmp.unlink()
            continue
        os.replace(tmp, final)
        return n


def read_state() -> dict | None:
    try:
        return json.loads(STATE_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def write_state(state: dict) -> None:
    tmp = STATE_FILE.with_suffix(".tmp")
    tmp.write_text(json.dumps(state, indent=2), encoding="utf-8")
    os.replace(tmp, STATE_FILE)


def mtime(path: Path) -> float | None:
    try:
        return path.stat().st_mtime
    except OSError:
        return None


def newest_mtime(folder: Path) -> float | None:
    times = [t for p in folder.glob("*.txt") if (t := mtime(p)) is not None] if folder.is_dir() else []
    return max(times, default=None)


def kill_game() -> None:
    subprocess.run(["taskkill", "/IM", "IM.exe", "/F"], capture_output=True)


def finish(out_dir: Path, reason: str) -> str:
    """Ends the session's record once: compares the player's saves with the start, writes final.txt."""
    final = out_dir / "final.txt"
    if final.exists():
        return final.read_text(encoding="utf-8")
    try:
        before = json.loads((out_dir / "data_snapshot.json").read_text(encoding="utf-8"))
        after = runner.snapshot(runner.SAVE_DIR)
        changes = [f"changed {p}" for p in sorted(before) if p in after and after[p] != before[p]]
        changes += [f"added {p}" for p in sorted(after) if p not in before]
        changes += [f"removed {p}" for p in sorted(before) if p not in after]
        saves = ("Your saves are unchanged." if not changes else
                 f"FAILED: the session touched your saves in {runner.SAVE_DIR}:\n  " + "\n  ".join(changes))
    except (OSError, ValueError) as ex:
        saves = f"Could not check your saves: {ex}"
    text = f"Session ended: {reason}\n{saves}\nTranscript: {out_dir / 'transcript.txt'}\n"
    final.write_text(text, encoding="utf-8")
    state = read_state()
    if state and state.get("out_dir") == str(out_dir):
        state.update(status="ended", ended_reason=reason)
        write_state(state)
    return text


def tail_lines(path: Path, n: int) -> list[str]:
    try:
        return path.read_text(encoding="utf-8", errors="replace").splitlines()[-n:]
    except OSError:
        return []


# ---------------------------------------------------------------- start (the watchdog)

def cmd_start(args) -> int:
    state = read_state()
    if state and state.get("status") != "ended" and runner.game_running():
        print(f"A session is already running ({state.get('out_dir')}); stop it first.")
        return 2
    # At once, so a send right after a start in the background waits for this session
    # instead of reading the last one's "ended".
    write_state({"out_dir": None, "status": "starting", "started_at": time.time(), "watchdog_pid": os.getpid()})
    try:
        return start_session(args)
    except BaseException as ex:
        if (read_state() or {}).get("out_dir") is None:
            write_state({"out_dir": None, "status": "ended", "ended_reason": f"start failed: {ex!r}"})
        raise


def start_failed(reason: str) -> int:
    print(reason)
    write_state({"out_dir": None, "status": "ended", "ended_reason": reason})
    return 2


def start_session(args) -> int:
    game_dir = args.game_dir or runner.find_game_dir()
    if game_dir is None or not (game_dir / "IM.exe").is_file():
        return start_failed("Idol Manager not found; pass --game-dir.")
    if runner.game_running():
        return start_failed("Idol Manager is already running; close it first.")
    if not args.no_save and not args.save.is_file():
        return start_failed(f"Save not found: {args.save}")
    if not args.skip_runner_build:
        runner.build(HERE / "source" / "InGameTests.csproj", game_dir)

    run_id = "session-" + datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out_dir = SESSIONS / run_id
    out_dir.mkdir(parents=True)
    if not args.no_save:
        shutil.copyfile(args.save, out_dir / "fixture.json")
    runner.stage_ignore_list(out_dir)
    runner.stage_scope_list(out_dir, [], [])
    (out_dir / "data_snapshot.json").write_text(json.dumps(runner.snapshot(runner.SAVE_DIR)), encoding="utf-8")

    cmd = [str(game_dir / "IM.exe"), "-imtest-session", "-imtest-run", run_id,
           "-imtest-timescale", str(args.timescale), "-imtest-idle", str(args.idle),
           "-imtest-max", str(args.max), "-imtest-command-limit", str(args.command_limit)]
    if args.no_save:
        cmd.append("-imtest-no-save")
    if args.load_vanilla:
        cmd.append("-imtest-load-vanilla")
    for name in args.load:
        cmd += ["-imtest-load", name]
    if args.allow_input:
        cmd.append("-imtest-allow-input")
    if args.sound:
        cmd.append("-imtest-sound")

    env = dict(os.environ, SteamAppId=runner.APP_ID, SteamGameId=runner.APP_ID)
    started_at = time.time()
    proc = subprocess.Popen(cmd, cwd=str(game_dir), env=env)
    write_state({"out_dir": str(out_dir), "status": "starting", "started_at": started_at, "watchdog_pid": os.getpid(),
                 "command_limit_s": args.command_limit * 60, "game_dir": str(game_dir),
                 # For restart: the same options again, the save aside.
                 "start_args": restart_args(sys.argv[2:])})
    print(f"Launching game for session {run_id} ...", flush=True)
    return watch(proc, out_dir, started_at, args.idle * 60, args.max * 60)


def watch(proc: subprocess.Popen, out_dir: Path, started_at: float, idle_s: float, max_s: float) -> int:
    ready = False
    while True:
        now = time.time()
        if not ready and (out_dir / "ready.txt").exists():
            ready = True
            status, body = parse_reply((out_dir / "ready.txt").read_text(encoding="utf-8"))
            if status == "ok":
                state = read_state() or {}
                state["status"] = "ready"
                write_state(state)
                print(f"Ready: {body}", flush=True)
            else:
                print(f"Startup failed:\n{body}", flush=True)
        alive = proc.poll() is None
        if not alive and runner.game_running():
            alive = True  # Steam relaunched the game itself; it is still the session's game
        activity = [t for t in (newest_mtime(out_dir / "cmd"), newest_mtime(out_dir / "reply"), mtime(out_dir / "ready.txt")) if t]
        action = watchdog_action(now, started_at=started_at, started=(out_dir / "started.txt").exists(), ready=ready,
                                 heartbeat_at=mtime(out_dir / "heartbeat.txt"), alive=alive,
                                 last_activity_at=max(activity, default=started_at), idle_s=idle_s, max_s=max_s)
        if action:
            kind, reason = action
            if kind == "kill":
                note = [f"Killed: {reason}"]
                running = (out_dir / "heartbeat.txt").read_text(encoding="utf-8", errors="replace") if (out_dir / "heartbeat.txt").exists() else ""
                note.append(f"Last heartbeat: {running}")
                note += ["Transcript tail:"] + tail_lines(out_dir / "transcript.txt", 10)
                (out_dir / "killed.txt").write_text("\n".join(note) + "\n", encoding="utf-8")
                kill_game()
                time.sleep(2)
            else:
                ended = out_dir / "ended.txt"
                reason = ended.read_text(encoding="utf-8").strip() if ended.exists() else \
                    "the game was closed or crashed without ending the session; after a crash, see BepInEx\\LogOutput.log and Player.log"
            print(finish(out_dir, reason), flush=True)
            return 0 if kind == "exited" and (out_dir / "ended.txt").exists() else 2
        time.sleep(1)


# ---------------------------------------------------------------- send / cancel / stop

def live_state() -> tuple[dict | None, str | None]:
    """The session state, or None and the reason there is no live session."""
    state = read_state()
    if not state:
        return None, "No session; start one with: python probe_session.py start"
    if state.get("status") == "ended":
        why = f"The session has ended: {state.get('ended_reason')}"
        if state.get("out_dir"):
            why += "\n" + finish(Path(state["out_dir"]), state.get("ended_reason", ""))
        return None, why
    return state, None


def session_state() -> tuple[dict | None, str | None]:
    """live_state, with a few seconds' grace for an ended one: a start just launched in the
    background may not have written its state yet."""
    state, why = live_state()
    deadline = time.time() + 5
    while not state and read_state() and time.time() < deadline:
        time.sleep(0.5)
        state, why = live_state()
    return state, why


def exchange(line: str, timeout: float | None = None) -> tuple[str, str]:
    """Sends one command and waits for its reply. Returns (status, text); status 'none' if there is none."""
    state, why = session_state()
    if not state:
        return "none", why
    deadline = time.time() + READY_LIMIT
    while not state.get("out_dir") or not (Path(state["out_dir"]) / "ready.txt").exists():
        if not watchdog_alive(state):
            return "none", "The session's start process is gone before the game was ready; run 'start' again."
        if time.time() > deadline:
            return "none", f"The session wasn't ready after {READY_LIMIT} s"
        time.sleep(1)
        state, why = live_state()
        if not state:
            return "none", why
    out_dir = Path(state["out_dir"])
    status, body = parse_reply((out_dir / "ready.txt").read_text(encoding="utf-8"))
    if status != "ok":
        return "none", "The session failed to start:\n" + body

    n = allocate_command(out_dir / "cmd", line)
    reply = out_dir / "reply" / f"{n:04d}.txt"
    limit = timeout if timeout is not None else state["command_limit_s"] + SEND_GRACE
    sent = time.time()
    last_check = sent
    while not reply.exists():
        now = time.time()
        if now - sent > limit:
            beat = mtime(out_dir / "heartbeat.txt")
            age = f"{now - beat:.0f} s ago" if beat else "never"
            return "none", (f"No reply to #{n} after {limit:.0f} s. Last heartbeat: {age}. "
                            f"Game running: {'yes' if runner.game_running() else 'no'}. "
                            "'cancel' stops a running command; 'stop' ends the session.")
        if now - last_check > 2:
            last_check = now
            state, why = live_state()
            if not state:
                return "none", why
            # With the watchdog gone, nobody else notices a dead or frozen game.
            if not watchdog_alive(state):
                beat = mtime(out_dir / "heartbeat.txt")
                if not runner.game_running():
                    return "none", "The game is no longer running and no watchdog is running; run 'stop' to clean up."
                if beat and now - beat > HEARTBEAT_LIMIT:
                    return "none", f"The game is frozen (no heartbeat for {now - beat:.0f} s) and no watchdog is running; run 'stop'."
        time.sleep(0.1)
    return parse_reply(reply.read_text(encoding="utf-8"))


def watchdog_alive(state: dict) -> bool:
    """Whether the start process that watches the game is still running."""
    pid = state.get("watchdog_pid")
    if not pid:
        return False
    out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH", "/FO", "CSV"], capture_output=True, text=True).stdout
    return f'"{pid}"' in out


def print_reply(status: str, text: str) -> int:
    if status == "none":
        print(text)
        return 2
    if status != "ok":
        print(status.upper())
    print(text)
    return reply_exit_code(status)


def cmd_send(args) -> int:
    return print_reply(*exchange(" ".join(args.command), args.timeout))


def cmd_cancel(_args) -> int:
    state, why = live_state()
    if not state:
        print(why)
        return 2
    if not state.get("out_dir"):
        print("The session is still starting; try again in a moment.")
        return 2
    (Path(state["out_dir"]) / "cancel").write_text("cancel", encoding="utf-8")
    print("Cancel sent; the running command stops within a second and replies 'stopped'.")
    return 0


def cmd_stop(_args) -> int:
    state, why = live_state()
    if not state:
        print(why)
        return 0
    if not state.get("out_dir"):
        print("The session is still starting; try again in a moment.")
        return 2
    out_dir = Path(state["out_dir"])
    if runner.game_running() and (out_dir / "ready.txt").exists():
        status, text = exchange("quit", timeout=QUIT_WAIT)
        if status == "none":
            print(text)
    deadline = time.time() + QUIT_WAIT
    while runner.game_running() and time.time() < deadline:
        time.sleep(1)
    if runner.game_running():
        print(f"The game didn't quit within {QUIT_WAIT} s; killing it.")
        kill_game()
        time.sleep(2)
    # The watchdog normally writes the final record; wait for it briefly, then write it here.
    deadline = time.time() + 10
    while not (out_dir / "final.txt").exists() and time.time() < deadline:
        time.sleep(1)
    ended = out_dir / "ended.txt"
    print(finish(out_dir, ended.read_text(encoding="utf-8").strip() if ended.exists() else "stopped"))
    return 0


def cmd_tail(args) -> int:
    state = read_state()
    if not state or not state.get("out_dir"):
        print("No session.")
        return 2
    print("\n".join(tail_lines(Path(state["out_dir"]) / "transcript.txt", args.lines)))
    return 0


def build_probe(probe: Path) -> tuple[Path | None, str]:
    """A probe project folder built in Release, or a DLL as it is. Returns the DLL, or None and why."""
    probe = probe.resolve()
    if not probe.is_dir():
        return (probe, "") if probe.is_file() else (None, f"No probe project or DLL at {probe}")
    projects = list(probe.glob("*.csproj"))
    if len(projects) != 1:
        return None, f"{probe} needs exactly one .csproj, found {len(projects)}"
    state = read_state() or {}
    game_dir = Path(state["game_dir"]) if state.get("game_dir") else runner.find_game_dir()
    result = subprocess.run(["dotnet", "build", str(projects[0]), "-c", "Release", "-nologo", "-v", "q",
                             f"-p:GameDir={game_dir}"], capture_output=True, text=True)
    if result.returncode != 0:
        return None, result.stdout[-4000:] + result.stderr[-2000:]
    dlls = sorted((probe / "bin" / "Release").rglob(projects[0].stem + ".dll"), key=lambda p: p.stat().st_mtime)
    if not dlls:
        return None, f"Built, but found no {projects[0].stem}.dll under {probe / 'bin' / 'Release'}"
    return dlls[-1], ""


def cmd_run(args) -> int:
    dll, why = build_probe(args.probe)
    if dll is None:
        print(why)
        return 2
    return print_reply(*exchange(f'run "{dll}" {args.method}'))


# ---------------------------------------------------------------- play / eval / restart

def parse_script(text: str) -> list[tuple[str, object]]:
    """A command script: ("send", line) per command and ("wait", seconds) per 'wait 2s' line. Blank lines
    and lines starting with # are skipped."""
    steps: list[tuple[str, object]] = []
    for number, raw in enumerate(text.splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        m = re.fullmatch(r"wait\s+(\d+(?:\.\d+)?)s?", line)
        if m:
            steps.append(("wait", float(m.group(1))))
        elif line.split()[0] == "wait":
            raise ValueError(f"line {number}: write a wait as 'wait 2s'")
        else:
            steps.append(("send", line))
    return steps


def play(text: str, show=print) -> int:
    """Runs a script's commands in order, showing each reply; stops at the first error or missing reply."""
    for kind, value in parse_script(text):
        if kind == "wait":
            time.sleep(value)
            continue
        status, reply = exchange(value)
        show(f"> {value}")
        show(reply if status == "ok" else f"{status.upper()}\n{reply}")
        show("")
        if status in ("error", "none"):
            return 2 if status == "none" else 1
    return 0


def cmd_play(args) -> int:
    try:
        return play(args.script.read_text(encoding="utf-8"))
    except (OSError, ValueError) as ex:
        print(ex)
        return 2


EVAL_DIR = HERE / "probes" / "_eval"


def eval_source(statements: str) -> str:
    """A probe file whose Eval.Run(ctx) runs the statements: ctx.Record and ctx.Note go into the reply."""
    return ("using InGameTests;\nusing System;\nusing System.Collections;\nusing System.Collections.Generic;\n"
            "using System.Linq;\nusing UnityEngine;\n\n"
            "internal static class Eval\n{\n    private static IEnumerator Run(TestContext ctx)\n    {\n"
            f"        {statements}\n        yield break;\n    }}\n}}\n")


def run_eval(statements: str) -> tuple[str, str]:
    """Builds the statements as a probe in probes/_eval and runs it. Returns (status, text) as exchange does."""
    EVAL_DIR.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(HERE / "tools" / "probe-template" / "Probe.csproj", EVAL_DIR / "Probe.csproj")
    (EVAL_DIR / "Eval.cs").write_text(eval_source(statements), encoding="utf-8")
    dll, why = build_probe(EVAL_DIR)
    if dll is None:
        return "none", "The statements didn't build:\n" + why
    return exchange(f'run "{dll}" Eval.Run')


def cmd_eval(args) -> int:
    return print_reply(*run_eval(" ".join(args.statements)))


def restart_args(start_args: list[str]) -> list[str]:
    """start's options without the save choice (--save X, --no-save) and without --skip-runner-build,
    which restart decides itself."""
    kept, skip = [], False
    for arg in start_args:
        if skip:
            skip = False
        elif arg == "--save":
            skip = True
        elif arg.startswith("--save=") or arg in ("--no-save", "--skip-runner-build"):
            continue
        else:
            kept.append(arg)
    return kept


def watched_methods(state_reply: str) -> list[str]:
    """The Type.Method names in a 'state' reply's 'Watching:' line, each once (one watch covers its overloads)."""
    m = re.search(r"^Watching: (.*)$", state_reply, re.MULTILINE)
    if not m or m.group(1) == "nothing":
        return []
    names = []
    for item in re.findall(r"([^,(]+)\([^)]*\)", m.group(1)):
        if item.strip() not in names:
            names.append(item.strip())
    return names


def cmd_restart(args) -> int:
    """Snap, stop, rebuild the runner, and start again from the snap with the same options and watches."""
    state, why = live_state()
    if not state or not state.get("out_dir"):
        print(why or "The session is still starting; try again in a moment.")
        return 2
    out_dir = Path(state["out_dir"])
    status, reply = exchange("state")
    if status != "ok":
        print(reply)
        return 2
    watches = watched_methods(reply)
    in_game = re.search(r"^Scene: main$", reply, re.MULTILINE) is not None
    snap = None
    if in_game:
        status, reply = exchange("snap _restart")
        if status != "ok":
            print("Couldn't snap the game:\n" + reply)
            return 2
        snap = SESSIONS / f"restart-{out_dir.name}.json"
        shutil.copyfile(out_dir / "snaps" / "_restart.json", snap)
    cmd_stop(None)

    start = [sys.executable, str(Path(__file__).resolve()), "start"] + state.get("start_args", [])
    start += ["--save", str(snap)] if snap else ["--no-save"]
    log = SESSIONS / "watchdog.log"
    # Detached, with its output in a file, so the new watchdog outlives this command and its terminal.
    with open(log, "w", encoding="utf-8") as out:
        subprocess.Popen(start, stdout=out, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                         creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP)
    print(f"Restarting {'from snap ' + str(snap) if snap else 'on the main menu (no game was loaded)'}; "
          f"the new watchdog writes to {log}")
    status, reply = exchange("state")
    if status != "ok":
        print(reply)
        return 2
    for method in watches:
        status, reply = exchange(f"watch {method}")
        print(("Watching again: " + method) if status == "ok" else f"Couldn't watch {method} again:\n{reply}")
    status, reply = exchange("state")
    print(reply)
    return 0


# ---------------------------------------------------------------- selftest

def cmd_selftest(args) -> int:
    """One session that checks what only the game can show: the command loop runs while the clock is
    paused, hooks see calls and results, a step and a restore move the date, a game-time limit stops
    a command, and the player's saves are untouched."""
    state = read_state()
    if state and state.get("status") != "ended" and runner.game_running():
        print("A session is running; stop it first.")
        return 2
    start = [sys.executable, str(Path(__file__).resolve()), "start"] + (["--skip-runner-build"] if args.skip_runner_build else [])
    watchdog = subprocess.Popen(start)
    failures: list[str] = []
    first_out = None

    def check(line: str, want_status: str, pattern: str | None = None) -> str:
        status, text = exchange(line)
        ok = status == want_status and (pattern is None or re.search(pattern, text, re.MULTILINE))
        print(f"  [{'PASS' if ok else 'FAIL'}] {line}")
        if not ok:
            failures.append(line)
            print("         got " + status + ": " + text.replace("\n", "\n             "))
        return text

    def date_of(text: str) -> datetime.datetime | None:
        m = re.search(r"(\d{4}-\d{2}-\d{2})( \d{2}:\d{2})?", text)
        return datetime.datetime.strptime(m.group(1) + (m.group(2) or " 00:00"), "%Y-%m-%d %H:%M") if m else None

    try:
        # Wait for the new session's state file before the first send.
        deadline = time.time() + 180
        while (not (s := read_state()) or s.get("started_at", 0) < time.time() - 60 or s.get("status") == "ended") \
                and time.time() < deadline:
            time.sleep(0.5)
        check("state", "ok", r"^Date: ")
        d0 = date_of(check("get staticVars.dateTime", "ok", r"^\d{4}-\d{2}-\d{2}"))
        check("snap a", "ok", r"^Saved snap a")
        check("watch mainScript.Time_SetState", "ok", r"^Watching mainScript\.Time_SetState")
        # AdvanceDays sets the fast speed and pauses again: two watched calls with their argument.
        # ... each with its caller, named as in source, not as the coroutine's compiler-made class.
        check("step 1d", "ok", r"Time_SetState\(fast\)   from Game\.Advance$(.|\n)*Time_SetState\(pause\)   from Game\.Advance$")
        d1 = date_of(check("get staticVars.dateTime", "ok"))
        if not (d0 and d1 and datetime.timedelta(days=1) <= d1 - d0 < datetime.timedelta(days=1, hours=2)):
            failures.append("step 1d date")
            print(f"  [FAIL] step 1d moved the date from {d0} to {d1}")
        check("until error within 1d", "stopped", r"^Game-time limit \(within 1d\) reached")
        check("watch PopupManager.IsThereAnOpenPopup_", "ok")
        check("restore a", "ok", r"^Restored snap a")
        d2 = date_of(check("get staticVars.dateTime", "ok"))
        if d2 != d0:
            failures.append("restore date")
            print(f"  [FAIL] restore a gave {d2}, expected {d0}")
        # A value-type result, boxed for the finalizer.
        check("calls 200", "ok", r"IsThereAnOpenPopup_\(\) -> (True|False)")
        check("get data_girls.girl[*].id", "ok", r"^\d+ values\n\[0\] = \d+")
        check("patches mainScript.Time_SetState", "ok", r"im\.ingametests\.session")
        check("unwatch", "ok", r"^Stopped watching 2 methods")
        # A probe loaded from its bytes, finding the runner and the game among the loaded assemblies.
        dll, why = build_probe(HERE / "tools" / "probe-template")
        if dll is None:
            failures.append("probe build")
            print("  [FAIL] building the probe template: " + why)
        else:
            check(f'run "{dll}" Probes.Example', "ok", r"^idols = \d+")

        # status reads the game's own methods; eval builds and runs a one-line probe.
        money = re.search(r"^Money:\s+([\d,]+)", check("status", "ok", r"^Money: "), re.MULTILINE)
        status, text = run_eval('ctx.Record("money", resources.Money());')
        evaluated = re.search(r"^money = (\d+)$", text, re.MULTILINE)
        ok = status == "ok" and money and evaluated and int(money.group(1).replace(",", "")) == int(evaluated.group(1))
        print(f"  [{'PASS' if ok else 'FAIL'}] status money matches eval resources.Money()")
        if not ok:
            failures.append("status/eval")
            print(f"         status: {money and money.group(1)}, eval {status}: {text}")

        # The virtual mouse through Unity's input module, and a key the game's own code reads.
        check("click Staff", "ok", r"^Clicked Staff")
        check("buttons Hire", "ok", r"^#\d+  Hire ")
        saves = Path((read_state() or {}).get("out_dir", ".")) / "saves"
        before = max((p.stat().st_mtime for p in saves.glob("*.json")), default=0)
        check("key F5", "ok", r"^Pressed F5")
        deadline = time.time() + 15
        while max((p.stat().st_mtime for p in saves.glob("*.json")), default=0) <= before and time.time() < deadline:
            time.sleep(0.5)
        ok = max((p.stat().st_mtime for p in saves.glob("*.json")), default=0) > before
        print(f"  [{'PASS' if ok else 'FAIL'}] key F5 made the game quicksave")
        if not ok:
            failures.append("key F5")

        # The example in the README must keep working.
        lines: list[str] = []
        code = play((HERE / "examples" / "first-session.txt").read_text(encoding="utf-8"), show=lines.append)
        print(f"  [{'PASS' if code == 0 else 'FAIL'}] examples/first-session.txt")
        if code != 0:
            failures.append("example")
            print("         " + "\n         ".join(lines[-12:]))

        check("no-such-command", "error", r"^Unknown command")

        # restart: same game date and the same watches in a new game process.
        check("watch mainScript.Time_SetState", "ok")
        d3 = date_of(check("get staticVars.dateTime", "ok"))
        first_out = (read_state() or {}).get("out_dir")
        restarted = cmd_restart(None) == 0
        print(f"  [{'PASS' if restarted else 'FAIL'}] restart")
        if not restarted:
            failures.append("restart")
        else:
            d4 = date_of(check("get staticVars.dateTime", "ok"))
            if d4 != d3:
                failures.append("restart date")
                print(f"  [FAIL] restart gave {d4}, expected {d3}")
            check("state", "ok", r"^Watching: mainScript\.Time_SetState")
    finally:
        print(cmd_stop_quiet())
        try:
            watchdog.wait(timeout=60)
        except subprocess.TimeoutExpired:
            watchdog.kill()
    # Both game processes (before and after the restart) must have left the player's saves alone.
    for out_dir in {first_out, (read_state() or {}).get("out_dir")} - {None}:
        final = Path(out_dir) / "final.txt"
        if "Your saves are unchanged." not in (final.read_text(encoding="utf-8") if final.exists() else ""):
            failures.append("saves in " + Path(out_dir).name)
    print("PASSED" if not failures else f"FAILED: {', '.join(failures)}")
    return 0 if not failures else 1


def cmd_stop_quiet() -> str:
    from io import StringIO
    import contextlib
    buffer = StringIO()
    with contextlib.redirect_stdout(buffer):
        cmd_stop(None)
    return buffer.getvalue().strip()


# ----------------------------------------------------------------

def main() -> int:
    sys.stdout.reconfigure(errors="backslashreplace")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="action", required=True)

    p = sub.add_parser("start", help="launch the game in a session and watch it (leave it running)")
    p.add_argument("--save", type=Path, default=HERE / "fixtures" / "default.json", help="save to load (a copy is loaded)")
    p.add_argument("--no-save", action="store_true", help="stay on the main menu; load no save")
    p.add_argument("--load", action="append", default=[], metavar="MOD", help="load only this mod; repeatable")
    p.add_argument("--load-vanilla", action="store_true", help="load no mods")
    p.add_argument("--allow-input", action="store_true", help="let keyboard and mouse reach the game")
    p.add_argument("--sound", action="store_true", help="don't mute the game")
    p.add_argument("--timescale", type=float, default=20.0, help="Time.timeScale while the clock runs")
    p.add_argument("--idle", type=float, default=30.0, help="minutes without a command before the game quits")
    p.add_argument("--max", type=float, default=120.0, help="minutes the session may last")
    p.add_argument("--command-limit", type=float, default=10.0, help="minutes one command may run")
    p.add_argument("--game-dir", type=Path, help="Idol Manager folder (default: found via Steam)")
    p.add_argument("--skip-runner-build", action="store_true", help="use the plugin already in the game")
    p.set_defaults(func=cmd_start)

    p = sub.add_parser("send", help="send one command and print the reply ('send help' lists them)")
    p.add_argument("command", nargs="+")
    p.add_argument("--timeout", type=float, help="seconds to wait for the reply (default: the command limit + 30)")
    p.set_defaults(func=cmd_send)

    sub.add_parser("cancel", help="stop the running command").set_defaults(func=cmd_cancel)
    sub.add_parser("stop", help="quit the game and check your saves").set_defaults(func=cmd_stop)

    p = sub.add_parser("tail", help="print the end of the transcript")
    p.add_argument("lines", type=int, nargs="?", default=40)
    p.set_defaults(func=cmd_tail)

    p = sub.add_parser("run", help="build a probe project (or take a DLL) and run one of its methods")
    p.add_argument("probe", type=Path, help="probe project folder, or a built DLL")
    p.add_argument("method", help="Class.Method: static IEnumerator Method(TestContext ctx)")
    p.set_defaults(func=cmd_run)

    p = sub.add_parser("play", help="send the commands of a script file in order (# comments, 'wait 2s' lines)")
    p.add_argument("script", type=Path)
    p.set_defaults(func=cmd_play)

    p = sub.add_parser("eval", help="run C# statements as a one-off probe (ctx.Record/ctx.Note go into the reply)")
    p.add_argument("statements", nargs="+")
    p.set_defaults(func=cmd_eval)

    sub.add_parser("restart", help="snap the game, rebuild the runner, start again from the snap with the same watches") \
        .set_defaults(func=cmd_restart)

    p = sub.add_parser("selftest", help="start a session, check the session works, stop it")
    p.add_argument("--skip-runner-build", action="store_true", help="use the plugin already in the game")
    p.set_defaults(func=cmd_selftest)

    args = ap.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
