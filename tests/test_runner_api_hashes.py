"""Result commands delegate calculations to the tester and keep raw CSV optional."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from urllib.parse import urlsplit, parse_qs
from test_runner_api import fixture

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "runner_tests.py"
A, B = "a" * 64, "b" * 64
REPORT = "A aaaaaa -> B bbbbbb | server comparison\nruntime | 100 | 80 | -20% | improved\n"


def respond(method, path, body, headers):
    route = urlsplit(path).path
    if route == "/api/v1/help":
        return 200, {"capabilities": ["executableHashResults", "serverComparison"]}, {}
    if route == "/api/v1/baseline":
        return 200, {"configured": method == "PUT", "sha256": body.get("sha256") if body else None}, {}
    if route.endswith("metrics.csv"):
        return 200, b"raw_time,raw_value\n0,9\n", {}
    if route == "/api/v1/compare" or route.startswith("/api/v1/build-results/"):
        return 200, REPORT.encode(), {}
    return 404, {"error": path}, {}


class HashClientChecks(unittest.TestCase):
    def run_client(self, origin, *args):
        return subprocess.run([sys.executable, str(SCRIPT), *args], env={**os.environ, "XEMU_RUNNER_URL": origin},
                              text=True, capture_output=True, timeout=20)

    def test_compare_passes_sha_arguments_and_prints_the_server_report(self):
        with fixture(respond) as (origin, calls):
            result = self.run_client(origin, "compare", "--a", A, "--b", B)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.stdout.strip(), REPORT.strip())
        query = parse_qs(urlsplit(calls[-1][1]).query)
        self.assertEqual(query["A"], [A])
        self.assertEqual(query["B"], [B])
        self.assertTrue(all(method == "GET" for method, _, _, _ in calls))
        self.assertFalse(any("artifacts" in path for _, path, _, _ in calls))

    def test_default_comparison_omits_a_instead_of_choosing_a_build_locally(self):
        with fixture(respond) as (origin, calls):
            result = self.run_client(origin, "compare", "--b", B)
        self.assertEqual(result.returncode, 0)
        self.assertNotIn("A", parse_qs(urlsplit(calls[-1][1]).query))

    def test_setting_a_baseline_requires_the_explicit_hash_command(self):
        with fixture(respond) as (origin, calls):
            result = self.run_client(origin, "baseline", A)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(json.loads(result.stdout)["sha256"], A)
        self.assertEqual(calls[-1][:3], ("PUT", "/api/v1/baseline", {"sha256": A}))

    def test_result_does_not_download_raw_csv(self):
        with fixture(respond) as (origin, calls):
            result = self.run_client(origin, "result", B)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(len(calls), 2)
        self.assertFalse(any("artifacts" in path for _, path, _, _ in calls))

    def test_raw_csv_is_downloaded_only_when_explicitly_requested(self):
        with tempfile.TemporaryDirectory() as folder, fixture(respond) as (origin, calls):
            output = Path(folder) / "metrics.csv"
            result = self.run_client(origin, "csv", "run-example", str(output))
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual(output.read_bytes(), b"raw_time,raw_value\n0,9\n")
        self.assertEqual(sum(method == "HEAD" for method, _, _, _ in calls), 1)
