"""Verify diagnostic permission is limited to the owning runner ancestry."""
from pathlib import Path
import importlib.util
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/runner_debug_launch.py'
spec = importlib.util.spec_from_file_location('runner_debug_launch', SCRIPT)
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)


class OwnerChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.addCleanup(self.temporary.cleanup)

    def process(self, pid, parent, arguments):
        directory = self.root / str(pid)
        directory.mkdir()
        (directory / 'status').write_text(f'Name:\tfixture\nPPid:\t{parent}\n')
        (directory / 'cmdline').write_bytes(b'\0'.join(x.encode() for x in arguments) + b'\0')

    def test_native_supervisor_resolves_only_owning_runner(self):
        self.process(15, 13, ['python3', 'native-crash.py'])
        self.process(13, 1, ['/opt/runner/XemuTestRunner', '--config', 'runner.json'])
        self.assertEqual(launcher.find_runner_owner(15, self.root), 13)

    def test_dotnet_host_resolves_application_argument(self):
        self.process(15, 13, ['python3', 'native-crash.py'])
        self.process(13, 1, ['/usr/bin/dotnet', '/opt/runner/XemuTestRunner.dll'])
        self.assertEqual(launcher.find_runner_owner(15, self.root), 13)

    def test_name_in_unrelated_process_arguments_is_not_an_owner(self):
        self.process(15, 13, ['python3', 'inspect.py', '/opt/runner/XemuTestRunner'])
        self.process(13, 1, ['/usr/bin/bash', '-c', 'XemuTestRunner'])
        self.assertIsNone(launcher.find_runner_owner(15, self.root))

    def test_unrelated_process_is_not_an_owner(self):
        self.process(15, 1, ['/usr/bin/python3', 'operator.py'])
        self.assertIsNone(launcher.find_runner_owner(15, self.root))

    def test_ancestry_cycle_is_rejected(self):
        self.process(15, 13, ['python3'])
        self.process(13, 15, ['python3'])
        with self.assertRaises(ValueError):
            launcher.find_runner_owner(15, self.root)

    @unittest.skipUnless(sys.platform == 'linux', 'Linux process attachment launcher')
    def test_non_runner_launch_refuses_before_executing_target(self):
        marker = self.root / 'target-executed'
        command = [sys.executable, str(SCRIPT), '--', sys.executable, '-c',
                   'from pathlib import Path; Path(' + repr(str(marker)) + ').touch()']
        result = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(result.returncode, 1)
        self.assertIn('No owning XemuTestRunner ancestor', result.stderr)
        self.assertFalse(marker.exists())


if __name__ == '__main__':
    unittest.main()
