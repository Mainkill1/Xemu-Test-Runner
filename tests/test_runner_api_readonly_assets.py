"""Shared firmware uploads use the same HTTP client, never a remote shell."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from urllib.parse import urlsplit
from test_runner_api import fixture

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "runner_tests.py"


def respond(method, path, body, headers):
    route = urlsplit(path).path
    if route == "/api/v1/help":
        return 200, {"capability": "diskAssets"}, {}
    if route == "/api/v1/disk-assets":
        return 200, {**body, "ready": False}, {}
    if route.endswith("/content"):
        if method == "GET":
            return 200, {"complete": False, "partial": False, "length": 0}, {}
        return 201, {"complete": True}, {}
    if route == "/api/v1/disk-assets/boot-v1":
        return 200, {"id": "boot-v1", "kind": "readonly-input", "ready": True}, {}
    return 404, {"error": route}, {}


class ReadOnlyAssetClientChecks(unittest.TestCase):
    def test_shared_rom_upload_is_available_without_extra_tools_or_start(self):
        with tempfile.TemporaryDirectory() as directory, fixture(respond) as (origin, calls):
            path = Path(directory) / "boot.bin"
            path.write_bytes(b"boot-rom")
            result = subprocess.run([sys.executable, str(SCRIPT), "disk-upload", "boot-v1", str(path), "--kind", "readonly-input"],
                env={**os.environ, "XEMU_RUNNER_URL": origin}, capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(json.loads(result.stdout)["kind"], "readonly-input")
        self.assertEqual(sum(method == "PUT" for method, _, _, _ in calls), 1)
        self.assertFalse(any("/start" in path or "/submit" in path or "/test-runs" in path for _, path, _, _ in calls))
