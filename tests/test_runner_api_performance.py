"""The agent prints server analysis; it never reads CSV or evaluates samples locally."""
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
from urllib.parse import urlsplit
from test_runner_api import fixture

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "runner_tests.py"
REPORT = "Run fixture | comparison=ineligible\nCPU mean: 20.00 core %\n"


def respond(method, path, body, headers):
    route = urlsplit(path).path
    if route == "/api/v1/agent":
        return 200, {"api": "xemu-test-runner", "version": "fixture", "instance": "instance-a", "capabilities": ["requestedTests", "completionWait"]}, {}
    if route == "/api/v1/runs/fixture/performance":
        if "format=json" in path:
            return 200, {"available": True, "runId": "fixture", "outcome": {"comparison": "ineligible"}}, {}
        return 200, REPORT.encode(), {}
    return 404, {"code": "unexpected_request", "error": path}, {}


class PerformanceClientChecks(unittest.TestCase):
    def invoke(self, origin, *arguments):
        return subprocess.run([sys.executable, str(SCRIPT), *arguments],
            env={**os.environ, "XEMU_RUNNER_URL": origin}, text=True, capture_output=True, timeout=10)

    def test_prints_one_server_generated_report_without_raw_download(self):
        with fixture(respond) as (origin, calls):
            result = self.invoke(origin, "performance", "fixture")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.stdout.strip(), REPORT.strip())
        self.assertEqual(result.stderr, "")
        self.assertEqual(len(calls), 1)
        self.assertEqual(calls[0][0], "GET")
        self.assertFalse(any("artifacts" in path for _, path, _, _ in calls))

    def test_json_preserves_ineligibility(self):
        with fixture(respond) as (origin, calls):
            result = self.invoke(origin, "--json", "performance", "fixture")
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(json.loads(result.stdout)["outcome"]["comparison"], "ineligible")
        self.assertEqual(len(calls), 1)

    def test_ssh_loopback_is_rejected_before_any_remote_request(self):
        with fixture(respond) as (origin, calls):
            env = {**os.environ, "XEMU_RUNNER_URL": origin, "SSH_CONNECTION": "10.0.0.1 12345 10.0.0.2 22"}
            result = subprocess.run([sys.executable, str(SCRIPT), "connect"],
                env=env, text=True, capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)["code"], "ssh_loopback_forbidden")
        self.assertEqual(calls, [])

    def test_connect_proves_http_access_from_this_machine_and_flags_loopback(self):
        with fixture(respond) as (origin, calls):
            result = self.invoke(origin, "connect")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        value = json.loads(result.stdout)
        self.assertTrue(value["reachable"])
        self.assertEqual(value["runner"], origin)
        self.assertTrue(value["loopback"])
        self.assertIn("agent", value["hint"].lower())
        self.assertEqual(len(calls), 1)
        self.assertTrue(all(method == "GET" for method, _, _, _ in calls))
