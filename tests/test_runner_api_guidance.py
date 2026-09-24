"""Keep onboarding examples aligned with the real, read-only argument parser."""
from __future__ import annotations

import ast
from contextlib import redirect_stdout
import io
from pathlib import Path
import re
import shlex
import sys
import unittest
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
import runner_tests


class GuidanceChecks(unittest.TestCase):
    def help_for(self, command: str) -> str:
        output = io.StringIO()
        with redirect_stdout(output), self.assertRaises(SystemExit) as stopped:
            runner_tests.parser().parse_args([command, "--help"])
        self.assertEqual(stopped.exception.code, 0)
        return output.getvalue()

    def examples(self) -> list[list[str]]:
        readme = (ROOT / "README.md").read_text(encoding="utf-8")
        return [shlex.split(line)[2:] for line in readme.splitlines()
                if line.startswith("python scripts/runner_tests.py ")]

    def test_upload_and_start_help_explain_execution_permission(self):
        upload_help = self.help_for("upload").lower()
        self.assertIn("does not start", upload_help)
        self.assertIn("--start", upload_help)
        self.assertIn("queue", self.help_for("start").lower())
        self.assertLess(len(upload_help.encode()), 2048)

    def test_readme_examples_use_real_cli_options(self):
        examples = self.examples()
        self.assertGreaterEqual(len(examples), 5)
        for arguments in examples:
            with self.subTest(arguments=arguments):
                # Parsing alone must never create an HTTP request or run a test.
                runner_tests.parser().parse_args(arguments)

    def test_readme_lists_all_required_client_modules(self):
        source = (ROOT / "scripts" / "runner_tests.py").read_text(encoding="utf-8")
        readme = (ROOT / "README.md").read_text(encoding="utf-8")
        modules = set()
        for node in ast.walk(ast.parse(source)):
            if isinstance(node, ast.Import):
                modules.update(alias.name for alias in node.names)
            elif isinstance(node, ast.ImportFrom) and node.module:
                modules.add(node.module)
        for module in sorted(name for name in modules if name.startswith("runner_")):
            with self.subTest(module=module):
                self.assertIn(module + ".py", readme)
                self.assertTrue((ROOT / "scripts" / (module + ".py")).is_file())

    def test_readme_teaches_default_wait_without_timers(self):
        waits = [arguments for arguments in self.examples() if arguments[0] == "wait"]
        self.assertTrue(waits, "README never shows how to wait for a test.")
        for arguments in waits:
            self.assertTrue(set(arguments).isdisjoint({"--follow", "--max-wait", "--interval"}))

    def test_readme_and_code_guide_links_resolve(self):
        for document in (ROOT / "README.md", ROOT / "docs" / "CODE-GUIDE.md"):
            with self.subTest(document=document.name):
                self.assertTrue(document.is_file(), f"Missing guide: {document.name}")
                text = document.read_text(encoding="utf-8")
                for target in re.findall(r"\[[^\]]+\]\(([^)\s]+)\)", text):
                    parsed = urlsplit(target)
                    if parsed.scheme or not parsed.path:
                        continue
                    self.assertTrue((document.parent / unquote(parsed.path)).exists(),
                                    f"Broken link in {document.name}: {target}")

    def test_upload_and_select_defaults_do_not_authorize_execution(self):
        for arguments in (["upload", "candidate", "--exe", "xemu.exe", "--id", "build-1", "--tests", "smoke"],
                          ["select", "build-1", "--id", "repeat-1", "--tests", "smoke"]):
            with self.subTest(arguments=arguments):
                self.assertFalse(runner_tests.parser().parse_args(arguments).start)
                self.assertTrue(runner_tests.parser().parse_args([*arguments, "--start"]).start)


if __name__ == "__main__":
    unittest.main()
