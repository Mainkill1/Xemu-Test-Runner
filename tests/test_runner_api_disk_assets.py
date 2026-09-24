"""Disk asset client commands must upload once and never authorize test execution."""
import hashlib
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
        return 200, {"capabilities": ["diskAssets"]}, {}
    if route == "/api/v1/disk-assets" and method == "POST":
        return 200, {**body, "ready": False}, {}
    if route == "/api/v1/disk-assets" and method == "GET":
        return 200, {"items": [], "nextOffset": None, "storedBytes": 0}, {}
    if route == "/api/v1/disk-assets/shared":
        return 200, {"id": "shared", "kind": "xiso-seed", "ready": True}, {}
    if route == "/api/v1/disk-assets/shared/content" and method == "GET":
        return 200, {"complete": False, "partial": False, "length": 0}, {}
    if route == "/api/v1/disk-assets/shared/content" and method == "PUT":
        return 201, {"complete": True}, {}
    return 404, {"code": "unexpected_request", "error": route}, {}

class DiskAssetClientChecks(unittest.TestCase):
    def run_client(self, origin, *args):
        return subprocess.run([sys.executable, str(SCRIPT), *args],
            env={**os.environ, "XEMU_RUNNER_URL": origin}, text=True,
            capture_output=True, timeout=20)

    def test_disk_upload_does_not_start_or_submit_a_test(self):
        with tempfile.TemporaryDirectory() as folder, fixture(respond) as (origin, calls):
            source = Path(folder) / "blank.qcow2"
            source.write_bytes(b"catalog-seed")
            result = self.run_client(origin, "disk-upload", "shared", str(source), "--kind", "xiso-seed")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        value = json.loads(result.stdout)
        self.assertEqual(value["id"], "shared")
        self.assertEqual(value["sha256"], hashlib.sha256(b"catalog-seed").hexdigest())
        self.assertTrue(any(method == "PUT" and path.startswith("/api/v1/disk-assets/shared/content")
                            for method, path, _, _ in calls))
        self.assertFalse(any("/start" in path or "/submit" in path or "/test-runs" in path
                             for _, path, _, _ in calls))

    def test_disk_list_is_a_single_catalog_read(self):
        with fixture(respond) as (origin, calls):
            result = self.run_client(origin, "disk-list")
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(len(calls), 2)  # capability help + catalog
        self.assertEqual(urlsplit(calls[-1][1]).path, "/api/v1/disk-assets")

if __name__ == "__main__":
    unittest.main()
