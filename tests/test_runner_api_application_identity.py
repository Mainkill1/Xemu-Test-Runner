"""Identity is supplied by the runner before any requested start; no raw file analysis."""
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
from urllib.parse import urlsplit
from test_runner_api import fixture

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "runner_tests.py"
SHA = "c" * 64


def respond(method, path, body, headers):
    route = urlsplit(path).path
    if route == "/api/v1/help":
        return 200, {"capabilities": ["requestedTests", "applicationIdentity"]}, {}
    if route == "/api/v1/test-configs":
        return 200, {"items": [{"id": "smoke", "revision": "a" * 64}], "nextOffset": None}, {}
    if route == "/api/v1/applications/candidate":
        return 200, {"id": "candidate", "sha256": SHA, "launchSha256": "b" * 64, "executable": "xemu", "launchExecutable": "launch.sh"}, {}
    if route == "/api/v1/test-runs":
        return 200, {"id": body["id"], "state": "uploaded"}, {}
    if route.endswith("/start"):
        return 202, {"state": "queued"}, {}
    return 404, {"code": "unexpected_request", "error": path}, {}


class ApplicationIdentityClientChecks(unittest.TestCase):
    def test_select_returns_the_native_hash_instead_of_null(self):
        with fixture(respond) as (origin, calls):
            result = subprocess.run([sys.executable, str(SCRIPT), "select", "candidate", "--id", "selection", "--tests", "smoke", "--start"],
                env={**os.environ, "XEMU_RUNNER_URL": origin}, text=True, capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        value = json.loads(result.stdout)
        self.assertEqual(value["sha256"], SHA)
        self.assertEqual(value["launchSha256"], "b" * 64)
        identity = next(i for i, (_, path, _, _) in enumerate(calls) if path == "/api/v1/applications/candidate")
        start = next(i for i, (_, path, _, _) in enumerate(calls) if path.endswith("/start"))
        self.assertLess(identity, start)
        self.assertFalse(any("/files/" in path or "/artifacts/" in path for _, path, _, _ in calls))
