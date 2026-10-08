"""Run the in-game test suite: launch Idol Manager in test mode, wait for results.json, report.

The InGameTests BepInEx plugin does the work inside the game (see source/Runner.cs).
This script builds and deploys the plugin, stages the fixture save and ignore list,
launches IM.exe with -imtest, waits for the results (returning as soon as they
exist), kills the game on timeout, and prints a summary.
Exit code: 0 pass, 1 test failure, 2 no results (crash/timeout/setup).

    python run_ingame_tests.py                       # smoke suite, 4 in-game weeks
    python run_ingame_tests.py --weeks 12 -v
    python run_ingame_tests.py --save path\\to\\save.json
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

HERE = Path(__file__).resolve().parent
APP_ID = "821880"
GAME_FOLDER = "Idol Manager"
LOCALLOW = Path(os.environ["USERPROFILE"]) / "AppData" / "LocalLow" / "Glitch Pitch" / "Idol Manager"


def find_game_dir() -> Path | None:
    """Look for Idol Manager in every Steam library listed in libraryfolders.vdf."""
    steam_roots = []
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            steam_roots.append(Path(winreg.QueryValueEx(key, "SteamPath")[0]))
    except OSError:
        pass
    steam_roots.append(Path(r"C:\Program Files (x86)\Steam"))

    for root in steam_roots:
        libraries = [root]
        vdf = root / "steamapps" / "libraryfolders.vdf"
        if vdf.is_file():
            for match in re.finditer(r'"path"\s+"([^"]+)"', vdf.read_text(encoding="utf-8", errors="replace")):
                libraries.append(Path(match.group(1).replace("\\\\", "\\")))
        for library in libraries:
            candidate = library / "steamapps" / "common" / GAME_FOLDER
            if (candidate / "IM.exe").is_file():
                return candidate
    return None


def game_running() -> bool:
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq IM.exe", "/NH"],
                         capture_output=True, text=True).stdout
    return "IM.exe" in out


def build(project: Path, game_dir: Path) -> None:
    print(f"Building {project.name} ...")
    result = subprocess.run(["dotnet", "build", str(project), "-c", "Release", "-nologo", "-v", "q",
                             f"-p:GameDir={game_dir}"],
                            capture_output=True, text=True)
    if result.returncode != 0:
        print(result.stdout[-4000:], result.stderr[-2000:])
        sys.exit(2)


def stage_ignore_list(out_dir: Path) -> None:
    """ignore.txt ships with the runner; ignore.local.txt is for errors from your own setup."""
    parts = [p.read_text(encoding="utf-8") for p in (HERE / "ignore.txt", HERE / "ignore.local.txt") if p.is_file()]
    (out_dir / "ignore.txt").write_text("\n".join(parts), encoding="utf-8")


def print_summary(results: dict, verbose: bool) -> None:
    for test in results["tests"]:
        mark = "PASS" if test["passed"] else "FAIL"
        print(f"  [{mark}] {test['name']}  ({test['seconds']}s)")
        for failure in test["failures"]:
            lines = failure.splitlines()
            print("         - " + lines[0])
            for line in lines[1:] if verbose else lines[1:4]:
                print("           " + line)
        for note in test["notes"]:
            print("         note: " + note)
        if test["data"] and (verbose or test["name"] != "SmokeTests.EveryEnabledHarmonyModIsPatched"):
            for key, value in test["data"].items():
                print(f"         {key} = {value}")
    print("PASSED" if results["passed"] else "FAILED")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--suite", default="smoke", help="test suite to run ('all' runs every [InGameTest])")
    ap.add_argument("--save", type=Path, default=HERE / "fixtures" / "smoke.json", help="fixture save to load")
    ap.add_argument("--weeks", type=int, default=4, help="in-game weeks to advance")
    ap.add_argument("--timescale", type=float, default=20.0, help="Time.timeScale while advancing")
    ap.add_argument("--timeout", type=float, default=300.0, help="seconds before the game is killed")
    ap.add_argument("--game-dir", type=Path, help="Idol Manager install folder (default: found via Steam)")
    ap.add_argument("--no-build", action="store_true", help="skip building the runner plugin")
    ap.add_argument("-v", "--verbose", action="store_true", help="full stack traces and per-mod patch counts")
    args = ap.parse_args()

    game_dir = args.game_dir or find_game_dir()
    if game_dir is None or not (game_dir / "IM.exe").is_file():
        print("Idol Manager not found; pass --game-dir.")
        return 2
    if not (game_dir / "BepInEx").is_dir():
        print(f"BepInEx is not installed in {game_dir}.")
        return 2
    if game_running():
        print("Idol Manager is already running; close it first.")
        return 2
    if not args.save.is_file():
        print(f"Fixture save not found: {args.save}")
        print("Copy a freeplay save there, e.g. from " + str(LOCALLOW / "data" / "auto_save.json"))
        return 2

    if not args.no_build:
        build(HERE / "source" / "InGameTests.csproj", game_dir)

    run_id = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out_dir = LOCALLOW / "InGameTests" / run_id
    out_dir.mkdir(parents=True)
    shutil.copyfile(args.save, out_dir / "fixture.json")
    stage_ignore_list(out_dir)
    results_path = out_dir / "results.json"

    # Launch IM.exe directly (not via steam -applaunch) so the script owns the process
    # and can kill it. Steam must be running; these env vars tell Steamworks which app this is.
    env = dict(os.environ, SteamAppId=APP_ID, SteamGameId=APP_ID)
    cmd = [str(game_dir / "IM.exe"),
           "-imtest", args.suite, "-imtest-run", run_id,
           "-imtest-weeks", str(args.weeks), "-imtest-timescale", str(args.timescale)]
    print(f"Launching game (run {run_id}) ...")
    start = time.monotonic()
    proc = subprocess.Popen(cmd, cwd=str(game_dir), env=env)

    while not results_path.exists():
        if proc.poll() is not None:
            # Steam may relaunch the game itself; keep waiting for a fresh IM.exe briefly.
            time.sleep(5)
            if not game_running() and not results_path.exists():
                print(f"Game exited (code {proc.returncode}) after {time.monotonic() - start:.0f}s without results.")
                print(f"See {game_dir / 'BepInEx' / 'LogOutput.log'} and {LOCALLOW / 'Player.log'}")
                return 2
        if time.monotonic() - start > args.timeout:
            print(f"Timed out after {args.timeout:.0f}s; killing the game.")
            subprocess.run(["taskkill", "/IM", "IM.exe", "/F"], capture_output=True)
            return 2
        time.sleep(1)

    elapsed = time.monotonic() - start
    results = json.loads(results_path.read_text(encoding="utf-8"))
    try:
        proc.wait(timeout=30)
    except subprocess.TimeoutExpired:
        subprocess.run(["taskkill", "/IM", "IM.exe", "/F"], capture_output=True)

    print(f"Results after {elapsed:.0f}s: {results_path}")
    print_summary(results, args.verbose)
    return 0 if results["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
