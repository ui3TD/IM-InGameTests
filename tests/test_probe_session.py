"""Unit tests for probe_session.py's decisions, with no game: python -m unittest discover tests"""

import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import probe_session as ps  # noqa: E402

T0 = 1_000_000.0
IDLE = 30 * 60
MAX = 120 * 60


def action(now, **overrides):
    """The watchdog's decision for a ready session with a fresh heartbeat, changed by overrides."""
    state = dict(started_at=T0, started=True, ready=True, heartbeat_at=now - 1, alive=True,
                 last_activity_at=now - 10, idle_s=IDLE, max_s=MAX)
    state.update(overrides)
    return ps.watchdog_action(now, **state)


class WatchdogTests(unittest.TestCase):
    def test_a_healthy_session_is_left_alone(self):
        self.assertIsNone(action(T0 + 600))

    def test_a_game_that_exited_ends_the_session(self):
        self.assertEqual("exited", action(T0 + 600, alive=False)[0])

    def test_a_runner_that_never_starts_is_killed_after_the_start_limit(self):
        self.assertIsNone(action(T0 + ps.START_LIMIT - 1, started=False, ready=False, heartbeat_at=None))
        kind, reason = action(T0 + ps.START_LIMIT + 1, started=False, ready=False, heartbeat_at=None)
        self.assertEqual("kill", kind)
        self.assertIn("did not start", reason)

    def test_a_silent_heartbeat_after_ready_means_frozen(self):
        now = T0 + 600
        self.assertIsNone(action(now, heartbeat_at=now - ps.HEARTBEAT_LIMIT + 1))
        kind, reason = action(now, heartbeat_at=now - ps.HEARTBEAT_LIMIT - 1)
        self.assertEqual("kill", kind)
        self.assertIn("frozen", reason)

    def test_startup_gets_a_longer_heartbeat_limit(self):
        now = T0 + 120
        silent = ps.HEARTBEAT_LIMIT + 10
        self.assertLess(silent, ps.STARTUP_HEARTBEAT)
        self.assertIsNone(action(now, ready=False, heartbeat_at=now - silent))
        self.assertEqual("kill", action(now, ready=False, heartbeat_at=now - ps.STARTUP_HEARTBEAT - 1)[0])

    def test_no_heartbeat_at_all_is_frozen_after_the_startup_limit(self):
        self.assertIsNone(action(T0 + 10, ready=False, heartbeat_at=None))
        self.assertEqual("kill", action(T0 + ps.STARTUP_HEARTBEAT + 1, ready=False, heartbeat_at=None)[0])

    def test_a_session_that_never_gets_ready_is_killed(self):
        now = T0 + ps.READY_LIMIT + 1
        kind, reason = action(now, ready=False)
        self.assertEqual("kill", kind)
        self.assertIn("not ready", reason)

    def test_idle_and_maximum_are_backups_after_the_games_own_limits(self):
        now = T0 + 3 * 3600
        # The game quits itself at the idle limit; the watchdog waits a grace period longer.
        self.assertIsNone(action(T0 + IDLE + 10, last_activity_at=T0, max_s=10 * 3600))
        self.assertIn("idle", action(T0 + IDLE + ps.BACKUP_GRACE + 1, last_activity_at=T0, max_s=10 * 3600)[1])
        self.assertIsNone(action(T0 + MAX + 10, last_activity_at=T0 + MAX))
        self.assertIn("maximum", action(now, last_activity_at=now - 10)[1])

    def test_a_busy_command_keeps_the_heartbeat_fresh_and_is_not_idle(self):
        # A long step: no new command or reply for 9 min, heartbeat fresh.
        now = T0 + 600
        self.assertIsNone(action(now, last_activity_at=now - 540))


class ReplyTests(unittest.TestCase):
    def test_status_is_the_first_line(self):
        self.assertEqual(("stopped", "Game-time limit\nmore"), ps.parse_reply("stopped\nGame-time limit\nmore"))
        self.assertEqual(("ok", ""), ps.parse_reply("ok"))

    def test_only_an_error_is_a_failing_exit_code(self):
        self.assertEqual(0, ps.reply_exit_code("ok"))
        self.assertEqual(0, ps.reply_exit_code("stopped"))
        self.assertEqual(1, ps.reply_exit_code("error"))


class CommandFileTests(unittest.TestCase):
    def test_commands_get_increasing_numbers_and_no_leftovers(self):
        with tempfile.TemporaryDirectory() as tmp:
            cmd = Path(tmp) / "cmd"
            self.assertEqual(1, ps.allocate_command(cmd, "step 1d"))
            self.assertEqual(2, ps.allocate_command(cmd, "get x"))
            self.assertEqual(["0001.txt", "0002.txt"], sorted(p.name for p in cmd.iterdir()))
            self.assertEqual("get x", (cmd / "0002.txt").read_text(encoding="utf-8"))

    def test_a_number_another_sender_is_writing_is_skipped(self):
        with tempfile.TemporaryDirectory() as tmp:
            cmd = Path(tmp) / "cmd"
            cmd.mkdir()
            (cmd / "0003.tmp").write_text("half written", encoding="utf-8")
            self.assertEqual(4, ps.allocate_command(cmd, "state"))
            self.assertEqual("half written", (cmd / "0003.tmp").read_text(encoding="utf-8"))


class ScriptTests(unittest.TestCase):
    def test_commands_waits_comments_and_blank_lines(self):
        text = "# a comment\n\nstate\n  wait 2s\nwait 0.5\nstep 1w   \n"
        self.assertEqual([("send", "state"), ("wait", 2.0), ("wait", 0.5), ("send", "step 1w")], ps.parse_script(text))

    def test_a_malformed_wait_names_its_line(self):
        with self.assertRaisesRegex(ValueError, "line 2"):
            ps.parse_script("state\nwait a bit\n")

    def test_the_example_script_parses(self):
        steps = ps.parse_script((ps.HERE / "examples" / "first-session.txt").read_text(encoding="utf-8"))
        self.assertTrue(steps)
        self.assertTrue(all(kind == "send" for kind, _ in steps))

    def test_play_stops_at_the_first_error(self):
        replies = iter([("ok", "fine"), ("error", "Unknown command"), ("ok", "never sent")])
        sent = []
        with mock.patch.object(ps, "exchange", lambda line: (sent.append(line), next(replies))[1]):
            code = ps.play("state\nbogus\nstep 1d\n", show=lambda _: None)
        self.assertEqual(1, code)
        self.assertEqual(["state", "bogus"], sent)

    def test_play_counts_a_stopped_command_as_fine(self):
        with mock.patch.object(ps, "exchange", lambda line: ("stopped", "Game-time limit")):
            self.assertEqual(0, ps.play("until error within 1d\n", show=lambda _: None))


class EvalTests(unittest.TestCase):
    def test_statements_go_inside_a_probe_method(self):
        source = ps.eval_source('ctx.Record("money", resources.Money());')
        self.assertIn("private static IEnumerator Run(TestContext ctx)", source)
        self.assertIn('ctx.Record("money", resources.Money());', source)
        self.assertIn("yield break;", source)
        self.assertEqual(source.count("{"), source.count("}"))


class RestartTests(unittest.TestCase):
    def test_restart_keeps_options_but_not_the_save_choice(self):
        self.assertEqual(["--timescale", "2", "--load", "My Mod"],
                         ps.restart_args(["--timescale", "2", "--save", "x.json", "--load", "My Mod", "--skip-runner-build"]))
        self.assertEqual(["--sound"], ps.restart_args(["--no-save", "--sound", "--save=y.json"]))

    def test_watched_methods_come_from_the_state_reply(self):
        reply = ("Scene: main\nWatching: mainScript.Time_SetState(mainScript._time_state), "
                 "DragAndDropManager.OnMouseDown(), DragAndDropManager.OnMouseDown(staff._staff)\nSnaps: none")
        self.assertEqual(["mainScript.Time_SetState", "DragAndDropManager.OnMouseDown"], ps.watched_methods(reply))
        self.assertEqual([], ps.watched_methods("Scene: main\nWatching: nothing\n"))


class FinishTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        self.saves = root / "data"
        self.saves.mkdir()
        (self.saves / "auto_save.json").write_text("{}", encoding="utf-8")
        self.out = root / "session"
        self.out.mkdir()
        self.patches = [mock.patch.object(ps.runner, "SAVE_DIR", self.saves),
                        mock.patch.object(ps, "STATE_FILE", root / "session.json")]
        for p in self.patches:
            p.start()
        (self.out / "data_snapshot.json").write_text(json.dumps(ps.runner.snapshot(self.saves)), encoding="utf-8")
        ps.write_state({"out_dir": str(self.out), "status": "ready"})

    def tearDown(self):
        for p in self.patches:
            p.stop()
        self.tmp.cleanup()

    def test_untouched_saves_are_reported_and_the_state_ends(self):
        text = ps.finish(self.out, "quit command")
        self.assertIn("Session ended: quit command", text)
        self.assertIn("Your saves are unchanged.", text)
        self.assertEqual("ended", ps.read_state()["status"])

    def test_a_changed_save_fails(self):
        (self.saves / "auto_save.json").write_text('{"x": 1}', encoding="utf-8")
        self.assertIn("FAILED", ps.finish(self.out, "quit command"))

    def test_finish_runs_once(self):
        first = ps.finish(self.out, "quit command")
        (self.saves / "auto_save.json").write_text('{"x": 1}', encoding="utf-8")
        self.assertEqual(first, ps.finish(self.out, "stopped"))


if __name__ == "__main__":
    unittest.main()
