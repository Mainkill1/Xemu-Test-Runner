"""Help and argument errors must not enumerate thousands of numeric choices."""
import json
from pathlib import Path
import subprocess
import sys
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "runner_api.py"


class HelpBudgetChecks(unittest.TestCase):
    def test_log_help_is_bounded(self):
        result = subprocess.run([sys.executable, str(SCRIPT), "logs", "--help"],
                                text=True, capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 0)
        self.assertLess(len(result.stdout.encode()), 2048)
        self.assertIn("1..16384", result.stdout)

    def test_out_of_range_error_is_structured_and_small(self):
        result = subprocess.run([sys.executable, str(SCRIPT), "logs", "example", "--max-bytes", "16385"],
                                text=True, capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stderr, "")
        self.assertLess(len(result.stdout.encode()), 512)
        self.assertEqual(json.loads(result.stdout)["code"], "arguments_invalid")
