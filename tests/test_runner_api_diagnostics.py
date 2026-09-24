"""Diagnostic inspection is compact; ZIP transfer is explicit and SHA-verified."""
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

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts' / 'runner_tests.py'
DATA = b'fixture diagnostic archive bytes'
DIGEST = hashlib.sha256(DATA).hexdigest()

def responder(digest=DIGEST, state='partial'):
    def respond(method, path, body, headers):
        if urlsplit(path).path == '/api/v1/runs/run-1/diagnostics':
            return 200, {'ok': True, 'available': True, 'runId': 'run-1',
                         'crash': {'crashed': True, 'code': 'SIGSEGV'},
                         'bundle': {'state': state, 'bytes': len(DATA), 'sha256': digest,
                                    'href': '/api/v1/runs/run-1/artifacts/diagnostics.zip'}}, {}
        if urlsplit(path).path.endswith('/artifacts/diagnostics.zip'):
            return 200, DATA, {}
        return 404, {'code': 'unexpected_route', 'error': path}, {}
    return respond

class DiagnosticClientChecks(unittest.TestCase):
    def invoke(self, origin, *args):
        return subprocess.run([sys.executable, str(SCRIPT), 'diagnostics', 'run-1', *args],
                              env={**os.environ, 'XEMU_RUNNER_URL': origin}, capture_output=True, text=True, timeout=10)

    def test_default_reads_one_small_summary_and_no_zip(self):
        with fixture(responder()) as (origin, calls):
            result = self.invoke(origin)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(json.loads(result.stdout)['crash']['crashed'])
        self.assertEqual(len(calls), 1)
        self.assertLess(len(result.stdout), 4096)
        self.assertEqual(result.stderr, '')

    def test_explicit_download_verifies_hash_and_preserves_partial_status(self):
        with tempfile.TemporaryDirectory() as folder, fixture(responder()) as (origin, calls):
            path = Path(folder) / 'diagnostics.zip'
            result = self.invoke(origin, '--out', str(path))
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual(path.read_bytes(), DATA)
            self.assertEqual(json.loads(result.stdout)['sha256'], DIGEST)
            self.assertEqual(json.loads(result.stdout)['bundleState'], 'partial')
        self.assertEqual(len(calls), 2)
        self.assertTrue(all(call[0] == 'GET' for call in calls))

    def test_same_length_wrong_zip_is_not_accepted(self):
        with tempfile.TemporaryDirectory() as folder, fixture(responder('a' * 64)) as (origin, _):
            result = self.invoke(origin, '--out', str(Path(folder) / 'diagnostics.zip'))
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)['code'], 'diagnostic_hash_mismatch')

    def test_failed_bundle_does_not_trigger_download_or_execution(self):
        with tempfile.TemporaryDirectory() as folder, fixture(responder(state='failed')) as (origin, calls):
            result = self.invoke(origin, '--out', str(Path(folder) / 'diagnostics.zip'))
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)['code'], 'diagnostic_bundle_unavailable')
        self.assertEqual(len(calls), 1)
