"""Compact numeric XISO selectors stay arrays on the HTTP contract."""
import json
import os
import subprocess
import sys
import unittest

from test_runner_api import fixture
from test_runner_api_xiso import SCRIPT, responder


class XisoNumericClientChecks(unittest.TestCase):
    def invoke(self, origin, *args):
        return subprocess.run(
            [sys.executable, str(SCRIPT), *args],
            env={**os.environ, "XEMU_RUNNER_URL": origin},
            capture_output=True,
            text=True,
            timeout=20,
        )

    def test_compact_numeric_arrays_are_sorted_deduplicated_and_not_expanded(self):
        with fixture(responder()) as (origin, calls):
            result = self.invoke(
                origin,
                "select",
                "build-a",
                "--id",
                "example",
                "--suite",
                "pilot",
                "--test-ids",
                "41,3,0,3,29",
                "--test-ids",
                "41",
                "--test-groups",
                "2,0,2",
            )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        body = next(
            body
            for method, path, body, _ in calls
            if path == "/api/v1/xiso-campaigns" and method == "POST"
        )
        self.assertEqual(body["test_ids"], [0, 3, 29, 41])
        self.assertEqual(body["test_groups"], [0, 2])
        self.assertEqual(body["suite"], "pilot")
        self.assertNotIn("tests", body)
        self.assertNotIn("categories", body)
        self.assertLess(len(json.dumps(body)), 170)

    def test_numeric_selector_bounds_fail_before_an_http_request(self):
        with fixture(responder()) as (origin, calls):
            result = self.invoke(
                origin,
                "select",
                "build-a",
                "--id",
                "example",
                "--test-ids",
                "0,512",
            )
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)["code"], "arguments_invalid")
        self.assertEqual(calls, [])

        with fixture(responder()) as (origin, calls):
            result = self.invoke(
                origin,
                "select",
                "build-a",
                "--id",
                "example",
                "--test-groups",
                "8",
            )
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)["code"], "arguments_invalid")
        self.assertEqual(calls, [])

    def test_numeric_selector_count_limit_fails_before_an_http_request(self):
        too_many = ",".join("0" for _ in range(513))
        with fixture(responder()) as (origin, calls):
            result = self.invoke(
                origin,
                "select",
                "build-a",
                "--id",
                "example",
                "--test-ids",
                too_many,
            )
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)["code"], "arguments_invalid")
        self.assertEqual(calls, [])


if __name__ == "__main__":
    unittest.main()
