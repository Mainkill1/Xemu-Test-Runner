"""Native wait-status adapter distinguishes application exits from signals."""
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest

HELPER = Path(__file__).resolve().parents[1] / 'tools' / 'runner_posix.py'

@unittest.skipUnless(sys.platform.startswith('linux'), 'Linux native wait status')
class NativeExitChecks(unittest.TestCase):
    def invoke(self, code, *arguments):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            process = subprocess.Popen([sys.executable, '-I', str(HELPER), directory, 'fixture', sys.executable, '-c', code, *arguments],
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                end = time.monotonic() + 5
                while not (root / 'ready.json').exists():
                    if time.monotonic() >= end: self.fail('Native launch handshake timed out')
                    time.sleep(.01)
                self.assertIsNone(process.poll())
                self.assertFalse((root / 'exit-native.json').exists())
                ready = json.loads((root / 'ready.json').read_text())
                (root / 'start').write_text('1')
                out, error = process.communicate(timeout=10)
                result = json.loads((root / 'exit-native.json').read_text())
                self.assertEqual(result['pid'], ready['pid'])
                self.assertEqual(result['identity'], 'fixture')
                return result, out, error
            finally:
                if process.poll() is None:
                    process.terminate()
                    try: process.wait(timeout=3)
                    except subprocess.TimeoutExpired:
                        process.kill(); process.wait()

    def test_ordinary_exit139_has_no_signal(self):
        value, _, _ = self.invoke('raise SystemExit(139)')
        self.assertEqual(value['exitCode'], 139)
        self.assertIsNone(value['signal'])

    def test_abort_preserves_native_signal(self):
        value, _, _ = self.invoke('import os,resource; resource.setrlimit(resource.RLIMIT_CORE,(0,0)); os.abort()')
        self.assertEqual(value['signal'], signal.SIGABRT)
        self.assertIsNone(value['exitCode'])

    def test_arguments_are_literal_and_output_is_forwarded(self):
        value, out, _ = self.invoke('import sys; print(sys.argv[1])', 'spaces;$(not-a-shell-command)')
        self.assertEqual(out.strip(), b'spaces;$(not-a-shell-command)')
        self.assertEqual(value['exitCode'], 0)

    def test_term_is_a_signal_not_a_crash_guess(self):
        value, _, _ = self.invoke('import os,signal; os.kill(os.getpid(),signal.SIGTERM)')
        self.assertEqual(value['signal'], signal.SIGTERM)
        self.assertIsNone(value['exitCode'])

if __name__ == '__main__': unittest.main()
