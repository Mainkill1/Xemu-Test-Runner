"""Exercise the public agent CLI against a recorded HTTP-only fixture."""
from __future__ import annotations

from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest
from urllib.parse import urlsplit, parse_qs

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "runner_api.py"
CAPABILITIES = ["jobSummaries", "resultSummaries", "pinnedTests", "payloadReuse", "artifactPages", "logCursors"]


@contextmanager
def fixture(responder):
    calls = []

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def handle_request(self):
            raw = self.rfile.read(int(self.headers.get("Content-Length", "0")))
            try:
                body = json.loads(raw) if raw else None
            except (ValueError, UnicodeDecodeError):
                body = raw
            calls.append((self.command, self.path, body, dict(self.headers)))
            status, response, headers = responder(self.command, self.path, body, dict(self.headers))
            encoded = response if isinstance(response, bytes) else json.dumps(response).encode()
            self.send_response(status)
            self.send_header("Content-Length", str(len(encoded)))
            for name, value in headers.items():
                self.send_header(name, value)
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(encoded)

        do_GET = do_POST = do_PUT = do_DELETE = do_HEAD = handle_request

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}", calls
    finally:
        server.shutdown()
        server.server_close()
        worker.join(timeout=5)


def baseline(method, path, body, headers):
    parsed = urlsplit(path)
    if parsed.path == "/api/v1/agent":
        return 200, {"api": "xemu-test-runner", "capabilities": CAPABILITIES}, {}
    if parsed.path == "/api/v1/jobs/example":
        return 200, {"ok": True, "id": "example", "state": "tested", "runId": "run-1", "cursor": "c", "blocker": None}, {}
    if parsed.path == "/api/v1/runs/run-1":
        return 200, {"ok": True, "runId": "run-1", "available": True,
                     "outcome": {"execution": "completed", "correctness": "failed", "evidence": "complete", "comparison": "ineligible"},
                     "reasons": ["guest assertion failed"], "failures": [], "moreReasons": 0, "moreFailures": 0}, {}
    return 404, {"code": "route_not_found", "error": path, "hint": "Use the advertised route."}, {}


class ClientChecks(unittest.TestCase):
    def run_client(self, origin, *args):
        env = {**os.environ, "XEMU_RUNNER_URL": origin}
        return subprocess.run([sys.executable, str(SCRIPT), *args], env=env, text=True,
                              capture_output=True, timeout=30)

    def test_result_is_compact_quiet_and_does_not_download_artifacts(self):
        with fixture(baseline) as (origin, calls):
            result = self.run_client(origin, "result", "example")
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        value = json.loads(result.stdout)
        self.assertEqual(value["outcome"]["correctness"], "failed")
        self.assertEqual(result.stderr, "")
        self.assertEqual(len(result.stdout.splitlines()), 1)
        self.assertLess(len(result.stdout.encode()), 4096)
        self.assertFalse(any("/artifacts" in call[1] for call in calls))
        self.assertTrue(all("view=summary" in call[1] for call in calls))

    def test_explicit_correctness_gate_fails_a_failed_guest(self):
        with fixture(baseline) as (origin, _):
            result = self.run_client(origin, "result", "example", "--require", "correctness")
        self.assertEqual(result.returncode, 2, result.stderr + result.stdout)
        self.assertEqual(json.loads(result.stdout)["outcome"]["correctness"], "failed")

    def test_not_evaluated_is_not_a_correctness_pass(self):
        def respond(method, path, body, headers):
            status, value, extra = baseline(method, path, body, headers)
            if "/runs/run-1" in path:
                value["outcome"]["correctness"] = "notEvaluated"
            return status, value, extra
        with fixture(respond) as (origin, _):
            result = self.run_client(origin, "result", "example", "--require", "correctness")
        self.assertEqual(result.returncode, 3, result.stderr + result.stdout)

    def test_old_server_reports_missing_capability_without_fallback(self):
        with fixture(lambda *args: (200, {"name": "old runner"}, {})) as (origin, calls):
            result = self.run_client(origin, "status", "example")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)["code"], "capability_missing")
        self.assertEqual(len(calls), 1)

    def test_server_error_preserves_code_and_recovery_hint(self):
        def respond(method, path, body, headers):
            if "agent" in path:
                return baseline(method, path, body, headers)
            return 409, {"error": "Benchmark is active.", "code": "operation_blocked", "hint": "Wait for this attempt to finish."}, {}
        with fixture(respond) as (origin, _):
            result = self.run_client(origin, "status", "example")
        value = json.loads(result.stdout)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(value["code"], "operation_blocked")
        self.assertEqual(value["hint"], "Wait for this attempt to finish.")
        self.assertEqual(value["status"], 409)

    def test_wait_expiry_returns_same_running_job(self):
        def respond(method, path, body, headers):
            if "agent" in path:
                return baseline(method, path, body, headers)
            return 200, {"ok": True, "id": "example", "state": "testing", "runId": "run-1", "cursor": "c", "blocker": None}, {}
        with fixture(respond) as (origin, calls):
            result = self.run_client(origin, "wait", "example", "--max-wait", "0")
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        self.assertEqual(json.loads(result.stdout)["state"], "testing")
        self.assertFalse(any(call[0] != "GET" for call in calls))

    def test_held_job_returns_without_blind_polling(self):
        def respond(method, path, body, headers):
            if "agent" in path:
                return baseline(method, path, body, headers)
            return 200, {"ok": True, "id": "example", "state": "held", "cursor": "c", "blocker": {"code": "attempt_held", "hint": "Inspect preserved target."}}, {}
        with fixture(respond) as (origin, calls):
            result = self.run_client(origin, "wait", "example", "--max-wait", "20")
        self.assertEqual(json.loads(result.stdout)["state"], "held")
        self.assertEqual(len(calls), 2)

    def test_prebaked_run_sends_reference_not_plan(self):
        def respond(method, path, body, headers):
            parsed = urlsplit(path)
            if parsed.path == "/api/v1/jobs/from-test":
                return 202, {"id": "new", "operation": "/api/v1/jobs/new/operation"}, {}
            if parsed.path == "/api/v1/jobs/new/operation":
                return 200, {"state": "completed", "action": "reuse"}, {}
            if parsed.path == "/api/v1/jobs/new/submit":
                return 202, {"state": "queued"}, {}
            if parsed.path == "/api/v1/jobs/new":
                return 200, {"ok": True, "id": "new", "state": "queued", "cursor": "c", "blocker": None}, {}
            return baseline(method, path, body, headers)
        with fixture(respond) as (origin, calls):
            result = self.run_client(origin, "run", "smoke", "--revision", "a" * 64, "--id", "new")
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        creation = next(call[2] for call in calls if call[1] == "/api/v1/jobs/from-test")
        self.assertEqual(creation["testId"], "smoke")
        self.assertLess(len(json.dumps(creation)), 256)
        self.assertNotIn("job", creation)
        self.assertNotIn("plan", creation)
        self.assertFalse(any("/files/" in call[1] for call in calls))

    def test_collect_default_is_one_selected_file_and_compact_receipt(self):
        def respond(method, path, body, headers):
            if path.endswith("/artifacts/assessment.json"):
                return 200, b'{"assessment":"fixture"}', {}
            return baseline(method, path, body, headers)
        with tempfile.TemporaryDirectory() as output, fixture(respond) as (origin, calls):
            result = self.run_client(origin, "collect", "example", output)
            value = json.loads(result.stdout)
            self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
            self.assertEqual(value["fileCount"], 1)
            self.assertNotIn("files", value)
            self.assertTrue((Path(output) / "run-1" / "assessment.json").exists())
        self.assertFalse(any(urlsplit(call[1]).path.endswith("/artifacts") for call in calls))

    def test_collect_all_refuses_incomplete_listing(self):
        def respond(method, path, body, headers):
            if urlsplit(path).path.endswith("/artifacts"):
                return 200, {"items": [], "nextCursor": None, "complete": False, "issues": ["inventory_limit"]}, {}
            return baseline(method, path, body, headers)
        with tempfile.TemporaryDirectory() as output, fixture(respond) as (origin, _):
            result = self.run_client(origin, "collect", "example", output, "--all")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)["code"], "evidence_incomplete")

    def test_redirect_is_not_followed_to_another_service(self):
        with fixture(lambda *args: (302, b"", {"Location": "http://127.0.0.1:1/elsewhere"})) as (origin, calls):
            result = self.run_client(origin, "discover")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)["code"], "redirect_rejected")
        self.assertEqual(len(calls), 1)


if __name__ == "__main__":
    unittest.main()
