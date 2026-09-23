"""State inspection is one bounded API read, not cache downloads or shell work."""
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
from test_runner_api import fixture

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "runner_tests.py"


class StateClientChecks(unittest.TestCase):
    def test_state_is_forwarded_without_artifact_download_or_start(self):
        def respond(method, path, body, headers):
            if method == "GET" and path == "/api/v1/runs/one/state":
                return 200, {"runId": "one", "available": True, "mode": "cold", "comparisonReady": False}, {}
            return 404, {"code": "unexpected_call", "error": path}, {}
        with fixture(respond) as (origin, calls):
            value = subprocess.run([sys.executable, str(SCRIPT), "state", "one"],
                env={**os.environ, "XEMU_RUNNER_URL": origin}, text=True, capture_output=True, timeout=20)
        self.assertEqual(value.returncode, 0, value.stdout + value.stderr)
        self.assertEqual(value.stderr, "")
        self.assertEqual(json.loads(value.stdout)["mode"], "cold")
        self.assertEqual(len(value.stdout.splitlines()), 1)
        self.assertEqual(len(calls), 1)
