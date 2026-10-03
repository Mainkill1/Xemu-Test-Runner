"""Verify Linux diagnostic records against independent proc/sys fixtures."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

TOOL = Path(__file__).resolve().parents[1] / 'tools/linux_performance_context.py'


def load_tool():
    spec = importlib.util.spec_from_file_location('linux_performance_context', TOOL)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def stat_line(pid=123, start=777):
    # Linux stat fields are numbered from one, including PID and comm.
    fields = {number: '0' for number in range(3, 53)}
    fields.update({3: 'R', 14: '120', 15: '23', 22: str(start), 39: '6'})
    return f'{pid} (vcpu ) with spaces) ' + ' '.join(fields[n] for n in range(3, 53))


class LinuxContextChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.proc, self.sys = self.root / 'proc', self.root / 'sys'
        self.proc.mkdir(); self.sys.mkdir()

    def write(self, relative, text):
        p = self.root / relative
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text)
        return p

    def process(self, start=777):
        self.write('proc/123/stat', stat_line(start=start))
        self.write('proc/123/maps', '1000-2000 r-xp 00000000 08:01 1 /app/xemu\n')
        self.write('proc/123/task/123/stat', stat_line(start=start))
        self.write('proc/123/task/123/status', 'Cpus_allowed_list:\t0-7\nvoluntary_ctxt_switches:\t11\nnonvoluntary_ctxt_switches:\t4\n')
        self.write('proc/sys/kernel/randomize_va_space', '2\n')

    def test_thread_stat_uses_documented_fields_despite_spaces_and_parentheses(self):
        tool = load_tool()
        got = tool.parse_stat(stat_line())
        self.assertEqual(got, {'pid': 123, 'name': 'vcpu ) with spaces', 'state': 'R',
                              'userTicks': 120, 'systemTicks': 23,
                              'startTicks': 777, 'lastProcessor': 6})

    def test_clock_units_affinity_and_mapping_are_preserved_without_changing_files(self):
        self.process()
        self.write('sys/devices/system/cpu/cpufreq/policy0/scaling_cur_freq', '2100000\n')
        self.write('sys/devices/system/cpu/cpufreq/policy0/scaling_governor', 'schedutil\n')
        self.write('sys/class/drm/card0/device/gpu_busy_percent', '87\n')
        self.write('sys/class/drm/card0/device/hwmon/hwmon4/temp1_input', '67000\n')
        self.write('sys/class/drm/card0/device/hwmon/hwmon4/power1_average', '12000000\n')
        before = {str(p): p.read_bytes() for p in self.root.rglob('*') if p.is_file()}
        got = load_tool().capture(123, 777, self.proc, self.sys)
        self.assertEqual(got['status'], 'captured')
        self.assertEqual(got['cpuPolicies']['policy0']['scaling_cur_freq'], '2100000')
        self.assertEqual(got['cpuPolicies']['policy0']['scaling_governor'], 'schedutil')
        self.assertEqual(got['threads'][0]['allowedCpus'], '0-7')
        self.assertEqual(got['threads'][0]['voluntaryContextSwitches'], 11)
        self.assertEqual(got['threads'][0]['involuntaryContextSwitches'], 4)
        self.assertEqual(got['threads'][0]['lastProcessor'], 6)
        self.assertEqual(got['gpu']['card0']['gpu_busy_percent'], '87')
        self.assertEqual(got['gpu']['card0']['hwmon']['hwmon4']['temp1_input'], '67000')
        self.assertEqual(got['gpu']['card0']['hwmon']['hwmon4']['power1_average'], '12000000')
        self.assertEqual(got['randomizeVaSpace'], '2')
        self.assertIn('/app/xemu', got['maps'])
        self.assertEqual(before, {str(p): p.read_bytes() for p in self.root.rglob('*') if p.is_file()})

    def test_unavailable_readings_are_missing_with_errors_never_zero(self):
        self.process()
        got = load_tool().capture(123, 777, self.proc, self.sys)
        self.assertEqual(got['status'], 'captured')
        self.assertEqual(got['cpuPolicies'], {})
        self.assertEqual(got['gpu'], {})
        self.assertTrue(any('cpufreq' in e for e in got['errors']))
        self.assertTrue(any('drm' in e for e in got['errors']))

    def test_missing_process_stops_collection(self):
        got = load_tool().capture(123, 777, self.proc, self.sys)
        self.assertEqual(got['status'], 'process_unavailable')
        self.assertNotIn('threads', got)

    def test_reused_pid_does_not_collect_the_replacement_process(self):
        self.process(start=888)
        got = load_tool().capture(123, 777, self.proc, self.sys)
        self.assertEqual(got['status'], 'pid_reused')
        self.assertNotIn('maps', got)
        self.assertNotIn('threads', got)

    def test_pid_reuse_during_capture_discards_mixed_context(self):
        self.process()
        target = self.proc / '123/stat'
        original = Path.read_text
        reads = 0
        def changing_read(path, *args, **kwargs):
            nonlocal reads
            text = original(path, *args, **kwargs)
            if path == target:
                reads += 1
                if reads == 1:
                    target.write_text(stat_line(start=888))
            return text
        with mock.patch.object(Path, 'read_text', changing_read):
            got = load_tool().capture(123, 777, self.proc, self.sys)
        self.assertEqual(got['status'], 'pid_reused')
        self.assertNotIn('maps', got)
        self.assertNotIn('threads', got)

    def test_reused_thread_does_not_mix_affinity_with_old_thread_counters(self):
        self.process()
        target = self.write('proc/123/task/124/stat', stat_line(pid=124, start=999))
        self.write('proc/123/task/124/status', 'Cpus_allowed_list: 2\nvoluntary_ctxt_switches: 5\nnonvoluntary_ctxt_switches: 7\n')
        original = Path.read_text
        reads = 0
        def changing_read(path, *args, **kwargs):
            nonlocal reads
            text = original(path, *args, **kwargs)
            if path == target:
                reads += 1
                if reads == 1:
                    target.write_text(stat_line(pid=124, start=1000))
            return text
        with mock.patch.object(Path, 'read_text', changing_read):
            got = load_tool().capture(123, 777, self.proc, self.sys)
        self.assertEqual([t['tid'] for t in got['threads']], [123])
        self.assertTrue(any('124' in e and 'identity changed' in e for e in got['errors']))

    def test_disappearing_thread_is_reported_without_losing_other_threads(self):
        self.process()
        (self.proc / '123/task/124').mkdir()
        got = load_tool().capture(123, 777, self.proc, self.sys)
        self.assertEqual([t['tid'] for t in got['threads']], [123])
        self.assertTrue(any('/task/124/stat' in e for e in got['errors']))

    def test_malformed_stat_is_not_reported_as_success(self):
        self.write('proc/123/stat', '123 (bad) R 0')
        got = load_tool().capture(123, 777, self.proc, self.sys)
        self.assertEqual(got['status'], 'process_unavailable')
        self.assertTrue(got['errors'])

    @unittest.skipUnless(sys.platform == 'linux', 'native procfs test')
    def test_cli_collects_one_real_sample_and_finishes(self):
        p = subprocess.run([sys.executable, str(TOOL), '--pid', str(os.getpid()),
                            '--samples', '1'], capture_output=True, text=True, timeout=10)
        self.assertEqual(p.returncode, 0, p.stderr)
        records = [json.loads(line) for line in p.stdout.splitlines()]
        self.assertEqual([r['type'] for r in records], ['header', 'sample', 'finish'])
        self.assertEqual(records[1]['pid'], os.getpid())
        self.assertEqual(records[1]['status'], 'captured')
        self.assertGreater(records[0]['clockTicksPerSecond'], 0)
        self.assertEqual(records[2]['samples'], 1)

    def test_invalid_cli_pid_and_sampling_bounds_fail_before_collection(self):
        for args in (['--pid', '0'], ['--pid', '123', '--samples', '0'],
                     ['--pid', '123', '--interval-ms', '1']):
            p = subprocess.run([sys.executable, str(TOOL), *args], capture_output=True, text=True)
            self.assertNotEqual(p.returncode, 0)
            self.assertEqual(p.stdout, '')


if __name__ == '__main__':
    unittest.main()
