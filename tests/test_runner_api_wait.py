"""Waiting uses only bounded read-only requests; output is quiet unless requested."""
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
from urllib.parse import parse_qs, urlsplit
from test_runner_api import fixture

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "runner_tests.py"


def responder(finish_after=2, attention=False):
    polls = 0
    def respond(method, path, body, headers):
        nonlocal polls
        if urlsplit(path).path == "/api/v1/agent":
            return 200, {"capabilities": ["completionWait"]}, {}
        if urlsplit(path).path.endswith("/sample/wait"):
            polls += 1
            done = polls >= finish_after
            return 200, {"id": "sample", "event": "attention" if attention else "finished" if done else "heartbeat",
                         "terminal": done and not attention, "state": "uploaded" if attention else "tested" if done else "testing",
                         "code": "start_required" if attention else None,
                         "runId": None if attention else "run-sample",
                         "result": {"available": True, "outcome": {"execution": "crashed"}} if done and not attention else None}, {}
        return 404, {"code": "unexpected", "error": path}, {}
    return respond


class WaitClientChecks(unittest.TestCase):
    def invoke(self, origin, *args):
        return subprocess.run([sys.executable, str(SCRIPT), "wait", "sample", *args],
            env={**os.environ, "XEMU_RUNNER_URL": origin}, capture_output=True, text=True, timeout=15)

    def test_follow_is_silent_until_terminal_and_performs_only_gets(self):
        with fixture(responder()) as (origin, calls):
            result = self.invoke(origin, "--follow", "--interval", "1")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.stderr, "")
        self.assertEqual(len(result.stdout.splitlines()), 1)
        value = json.loads(result.stdout)
        self.assertTrue(value["terminal"])
        self.assertEqual(value["result"]["outcome"]["execution"], "crashed")
        self.assertTrue(all(method == "GET" for method, _, _, _ in calls))
        self.assertEqual(len(calls), 3)
        self.assertFalse(any("artifacts" in path for _, path, _, _ in calls))

    def test_updates_are_opt_in_and_emit_only_small_heartbeats_plus_final(self):
        with fixture(responder(3)) as (origin, _):
            result = self.invoke(origin, "--follow", "--updates", "--interval", "1")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        values = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual([value["event"] for value in values], ["heartbeat", "heartbeat", "finished"])
        self.assertTrue(all(len(json.dumps(value)) < 1024 for value in values[:-1]))

    def test_zero_wait_returns_current_state_without_starting_anything(self):
        with fixture(responder(100)) as (origin, calls):
            result = self.invoke(origin, "--max-wait", "0")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(json.loads(result.stdout)["event"], "heartbeat")
        self.assertEqual(len(calls), 2)
        self.assertEqual(parse_qs(urlsplit(calls[-1][1]).query)["wait"], ["0"])

    def test_attention_stops_follow_without_polling_forever(self):
        with fixture(responder(attention=True)) as (origin, calls):
            result = self.invoke(origin, "--follow")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(json.loads(result.stdout)["code"], "start_required")
        self.assertEqual(len(calls), 2)

    def test_job_option_selects_the_existing_api_job_without_guessing_ids(self):
        with fixture(responder(1)) as (origin, calls):
            result = self.invoke(origin, "--job", "--follow")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(urlsplit(calls[-1][1]).path, "/api/v1/jobs/sample/wait")

    def test_missing_capability_does_not_fall_back_to_shell_or_old_status(self):
        with fixture(lambda *args: (200, {"capabilities": []}, {})) as (origin, calls):
            result = self.invoke(origin, "--follow")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)["code"], "capability_missing")
        self.assertEqual(len(calls), 1)
