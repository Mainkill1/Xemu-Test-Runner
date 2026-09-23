"""The focused upload helper must never infer permission to run tests."""
from pathlib import Path
import json
import os
import subprocess
import sys
import tempfile
import unittest
from urllib.parse import urlsplit
from test_runner_api import fixture

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "runner_tests.py"
REVISION = "a" * 64


def respond(method, path, body, headers):
    route = urlsplit(path).path
    if route == "/api/v1/help":
        return 200, {"capabilities": ["namedConfigs", "requestedTests", "uploadOnly"]}, {}
    if route == "/api/v1/test-configs":
        return 200, {"items": [{"id": "smoke", "revision": REVISION}], "nextOffset": None}, {}
    if route == "/api/v1/jobs":
        return 200, {"id": body["id"], "state": "draft"}, {}
    if "/files/" in route:
        if method == "GET":
            return 200, {"complete": False, "partial": False, "length": 0}, {}
        return 201, {"complete": True}, {}
    if route == "/api/v1/test-runs":
        return 200, {"id": body["id"], "state": "uploaded"}, {}
    if route.endswith("/start"):
        return 202, {"state": "queued"}, {}
    if route.startswith("/api/v1/test-configs/") and method == "POST":
        return 200, {"id": route.split("/")[-1], "revision": REVISION}, {}
    return 404, {"code": "unexpected_request", "error": route}, {}


class RequestedClientChecks(unittest.TestCase):
    def invoke(self, origin, *args):
        return subprocess.run([sys.executable, str(SCRIPT), *args],
            env={**os.environ, "XEMU_RUNNER_URL": origin}, text=True, capture_output=True, timeout=20)

    def test_upload_and_selection_do_not_start(self):
        with tempfile.TemporaryDirectory() as folder, fixture(respond) as (origin, calls):
            (Path(folder) / "candidate.exe").write_bytes(b"fixture")
            result = self.invoke(origin, "upload", folder, "--exe", "candidate.exe", "--id", "upload", "--tests", "smoke")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        value = json.loads(result.stdout)
        self.assertFalse(value["startRequested"])
        self.assertEqual(value["tests"][0]["state"], "uploaded")
        self.assertFalse(any("/start" in path or "/submit" in path for _, path, _, _ in calls))
        self.assertEqual(sum(method == "PUT" for method, _, _, _ in calls), 1)

    def test_start_flag_explicitly_queues_multiple_tests_after_one_upload(self):
        with tempfile.TemporaryDirectory() as folder, fixture(respond) as (origin, calls):
            (Path(folder) / "candidate.exe").write_bytes(b"fixture")
            result = self.invoke(origin, "upload", folder, "--exe", "candidate.exe", "--id", "upload", "--tests", "smoke", "smoke", "--start")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(sum(path.endswith("/start") for _, path, _, _ in calls), 2)
        self.assertEqual(sum(method == "PUT" for method, _, _, _ in calls), 1)
        self.assertEqual(len(json.loads(result.stdout)["tests"]), 2)

    def test_start_without_selection_fails_before_application_creation(self):
        with tempfile.TemporaryDirectory() as folder, fixture(respond) as (origin, calls):
            result = self.invoke(origin, "upload", folder, "--exe", "candidate.exe", "--id", "upload", "--start")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)["code"], "tests_required")
        self.assertFalse(any(method != "GET" for method, _, _, _ in calls))

    def test_new_named_config_upload_preserves_supplied_plan(self):
        with tempfile.TemporaryDirectory() as folder, fixture(respond) as (origin, calls):
            config = Path(folder) / "test.json"
            job = {"executable": "candidate.exe", "plan": [{"type": "wait", "delayMs": 123}]}
            config.write_text(json.dumps(job))
            result = self.invoke(origin, "config-upload", "new-name", str(config), "--assets", "seed")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        post = next(body for method, path, body, _ in calls if method == "POST")
        self.assertEqual(post["job"], job)
        self.assertEqual(post["sourceJobId"], "seed")
        self.assertFalse(any("/test-runs" in path or "/submit" in path for _, path, _, _ in calls))
