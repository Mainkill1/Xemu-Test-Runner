"""Completion waiting has no agent-chosen duration and retries read-only transport interruptions."""
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch
from urllib.parse import urlsplit
from test_runner_api import fixture

SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"
SCRIPT = SCRIPTS / "runner_tests.py"
if str(SCRIPTS) not in sys.path:
    sys.path.insert(0, str(SCRIPTS))
import runner_tests
import runner_wait
from runner_transport import ClientError


def responder(finish_after=2, attention=False, interrupt=False):
    polls = 0
    def respond(method, path, body, headers):
        nonlocal polls
        if urlsplit(path).path == "/api/v1/agent":
            return 200, {"capabilities": ["completionWait"]}, {}
        if urlsplit(path).path.endswith("/sample/wait"):
            polls += 1
            if interrupt and polls == 1:
                return 503, {"code": "temporarily_unavailable", "error": "Reconnect the same read."}, {}
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

    def test_default_wait_is_silent_until_terminal_and_only_reads_the_same_id(self):
        with fixture(responder()) as (origin, calls):
            result = self.invoke(origin)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.stderr, "")
        self.assertEqual(len(result.stdout.splitlines()), 1)
        value = json.loads(result.stdout)
        self.assertTrue(value["terminal"])
        self.assertEqual(value["result"]["outcome"]["execution"], "crashed")
        self.assertTrue(all(method == "GET" for method, _, _, _ in calls))
        self.assertEqual(len(calls), 3)
        self.assertTrue(all(path == "/api/v1/test-runs/sample/wait" for _, path, _, _ in calls[1:]))

    def test_updates_are_opt_in_without_interval_or_follow_arguments(self):
        with fixture(responder(3)) as (origin, _):
            result = self.invoke(origin, "--updates")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        values = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual([value["event"] for value in values], ["heartbeat", "heartbeat", "finished"])
        self.assertTrue(all(len(json.dumps(value)) < 1024 for value in values[:-1]))

    def test_elapsed_days_do_not_expire_a_default_wait(self):
        # Advance a synthetic clock by days, not actual sleeping in CI. The
        # unchanged default arguments must never create an overall deadline.
        args = runner_tests.parser().parse_args(["wait", "sample"])
        class Api:
            timeout = 60
            polls = 0
            elapsed = 0
            def require(self, *names):
                pass
            def json(self, path):
                self.polls += 1
                self.elapsed += 86400
                return {"id": "sample", "event": "finished" if self.polls == 4 else "heartbeat", "terminal": self.polls == 4}
        api = Api()
        with patch.object(runner_wait.time, "monotonic", side_effect=lambda: api.elapsed), patch.object(runner_wait.time, "sleep"):
            value = runner_wait.execute(api, args)
        self.assertEqual(value["event"], "finished")
        self.assertEqual(api.polls, 4)
        self.assertEqual(api.timeout, 60)

    def test_wait_help_does_not_offer_duration_interval_or_follow_options(self):
        result = subprocess.run([sys.executable, str(SCRIPT), "wait", "--help"], capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0)
        for option in ("--max-wait", "--interval", "--follow", "--timeout"):
            self.assertNotIn(option, result.stdout)
        self.assertIn("--updates", result.stdout)

    def test_removed_timer_arguments_are_rejected_without_network_or_start(self):
        for arguments in (("--max-wait", "0"), ("--interval", "5"), ("--follow",)):
            with self.subTest(arguments=arguments), fixture(responder(1)) as (origin, calls):
                result = self.invoke(origin, *arguments)
                self.assertEqual(result.returncode, 1)
                self.assertEqual(json.loads(result.stdout)["code"], "arguments_invalid")
                self.assertEqual(calls, [])

    def test_attention_returns_immediately_without_automatic_start(self):
        with fixture(responder(attention=True)) as (origin, calls):
            result = self.invoke(origin)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(json.loads(result.stdout)["code"], "start_required")
        self.assertEqual(len(calls), 2)

    def test_job_option_selects_the_existing_namespace_without_guessing(self):
        with fixture(responder(1)) as (origin, calls):
            result = self.invoke(origin, "--job")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(calls[-1][1], "/api/v1/jobs/sample/wait")

    def test_transient_failure_reconnects_silently_without_replaying_mutations(self):
        with fixture(responder(interrupt=True)) as (origin, calls):
            result = self.invoke(origin)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(result.stdout.splitlines()), 1)
        self.assertEqual(json.loads(result.stdout)["event"], "finished")
        self.assertTrue(all(method == "GET" for method, _, _, _ in calls))
        self.assertEqual(calls[-1][1], calls[-2][1])

    def test_missing_capability_and_unknown_ids_are_not_retried_forever(self):
        with fixture(lambda *args: (200, {"capabilities": []}, {})) as (origin, calls):
            result = self.invoke(origin)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)["code"], "capability_missing")
        self.assertEqual(len(calls), 1)

    def test_transport_retries_and_invalid_replies_never_become_completion(self):
        args = runner_tests.parser().parse_args(["wait", "sample"])
        class Api:
            timeout = 60
            polls = 0
            def require(self, *names):
                pass
            def json(self, path):
                self.polls += 1
                if self.polls == 1:
                    raise TimeoutError("disconnected")
                return {"id": "other", "event": "finished", "terminal": True}
        api = Api()
        with patch.object(runner_wait.time, "sleep"), self.assertRaises(ClientError) as failure:
            runner_wait.execute(api, args)
        self.assertEqual(failure.exception.code, "wait_response_invalid")
        self.assertEqual(api.polls, 2)
