"""Opt-in procfs/sysfs context for the runner's external diagnostic adapter.

Reads host state; never changes clocks, affinity, ASLR, cache or driver policy.
Output is JSON Lines. Sampling is diagnostic intervention, not a clean benchmark.
"""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import sys
import time


def parse_stat(text):
    left, right = text.index('('), text.rindex(')')
    fields = text[right + 1:].split()
    return {'pid': int(text[:left].strip()), 'name': text[left + 1:right],
            'state': fields[0], 'userTicks': int(fields[11]),
            'systemTicks': int(fields[12]), 'startTicks': int(fields[19]),
            'lastProcessor': int(fields[36])}


def read(path, errors):
    try:
        return path.read_text().strip()
    except (OSError, UnicodeError) as error:
        errors.append(f'{path}: {type(error).__name__}: {error}')
        return None


def directories(path, pattern, errors):
    try:
        return sorted(p for p in path.iterdir()
                      if re.fullmatch(pattern, p.name) and p.is_dir())
    except OSError as error:
        errors.append(f'{path}: {type(error).__name__}: {error}')
        return []


def process_stat(path, errors):
    text = read(path, errors)
    if text is not None:
        try:
            return parse_stat(text)
        except (ValueError, IndexError) as error:
            errors.append(f'{path}: malformed stat: {error}')
    return None


def capture(pid, expected_start, proc=Path('/proc'), sysfs=Path('/sys'), include_maps=True):
    started = time.monotonic_ns()
    errors = []
    result = {'pid': pid, 'startTicks': expected_start,
              'timestampUtc': datetime.now(timezone.utc).isoformat(),
              'monotonicNs': started, 'errors': errors}
    root = proc / str(pid)
    identity = process_stat(root / 'stat', errors)
    if identity is None:
        return {**result, 'status': 'process_unavailable'}
    if identity['startTicks'] != expected_start:
        return {**result, 'status': 'pid_reused'}

    threads = []
    for task in directories(root / 'task', r'[0-9]+', errors):
        stat = process_stat(task / 'stat', errors)
        if stat is None:
            continue
        status = read(task / 'status', errors)
        status_fields = dict(line.split(':', 1) for line in (status or '').splitlines() if ':' in line)
        thread = {'tid': int(task.name), **stat,
                  'allowedCpus': status_fields.get('Cpus_allowed_list', '').strip() or None}
        for source, destination in [('voluntary_ctxt_switches', 'voluntaryContextSwitches'),
                                    ('nonvoluntary_ctxt_switches', 'involuntaryContextSwitches')]:
            try:
                thread[destination] = int(status_fields[source])
            except (KeyError, ValueError):
                thread[destination] = None
                errors.append(f'{task}/status: unavailable {source}')
        final_thread = process_stat(task / 'stat', errors)
        if final_thread is None:
            continue
        if (final_thread['pid'], final_thread['startTicks']) != (stat['pid'], stat['startTicks']):
            errors.append(f'{task}: thread identity changed during capture')
            continue
        threads.append(thread)
    result['threads'] = threads
    policies = {}
    for policy in directories(sysfs / 'devices/system/cpu/cpufreq', r'policy[0-9]+', errors):
        policies[policy.name] = {name: read(policy / name, errors) for name in
                                ('affected_cpus', 'related_cpus', 'scaling_driver',
                                 'scaling_governor', 'scaling_min_freq', 'scaling_max_freq',
                                 'scaling_cur_freq', 'cpuinfo_cur_freq')}
    result['cpuPolicies'] = policies
    gpu = {}
    for card in directories(sysfs / 'class/drm', r'card[0-9]+', errors):
        device = card / 'device'
        gpu[card.name] = {name: read(device / name, errors) for name in
                          ('gpu_busy_percent', 'pp_dpm_sclk', 'pp_dpm_mclk',
                           'power_dpm_force_performance_level')}
        hwmon = {}
        for monitor in directories(device / 'hwmon', r'hwmon[0-9]+', errors):
            values = {'name': read(monitor / 'name', errors)}
            try:
                for path in sorted(monitor.iterdir()):
                    if re.fullmatch(r'(temp[0-9]+_(input|label)|power[0-9]+_(average|input|cap)|freq[0-9]+_input)', path.name):
                        values[path.name] = read(path, errors)
            except OSError as error:
                errors.append(f'{monitor}: {type(error).__name__}: {error}')
            hwmon[monitor.name] = values
        gpu[card.name]['hwmon'] = hwmon
    result['gpu'] = gpu
    result['randomizeVaSpace'] = read(proc / 'sys/kernel/randomize_va_space', errors)
    if include_maps:
        result['maps'] = read(root / 'maps', errors)
    # Reject a snapshot crossing process exit or PID reuse; never attribute its
    # context to the old target identity.
    final = process_stat(root / 'stat', errors)
    if final is None:
        return {key: result[key] for key in ('pid', 'startTicks', 'timestampUtc', 'monotonicNs', 'errors')} | {'status': 'process_unavailable'}
    if final['startTicks'] != expected_start:
        return {key: result[key] for key in ('pid', 'startTicks', 'timestampUtc', 'monotonicNs', 'errors')} | {'status': 'pid_reused'}
    result['status'] = 'captured'
    result['captureDurationNs'] = time.monotonic_ns() - started
    return result


def bounded_int(minimum, maximum):
    def parse(value):
        number = int(value)
        if not minimum <= number <= maximum:
            raise argparse.ArgumentTypeError(f'must be between {minimum} and {maximum}')
        return number
    return parse


def emit(record):
    print(json.dumps(record, separators=(',', ':'), allow_nan=False), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pid', type=bounded_int(1, 2147483647), required=True)
    parser.add_argument('--samples', type=bounded_int(1, 300), default=30)
    parser.add_argument('--interval-ms', type=bounded_int(100, 1000), default=1000)
    args = parser.parse_args()
    if sys.platform != 'linux':
        parser.error('Linux procfs/sysfs is required')
    errors = []
    identity = process_stat(Path('/proc') / str(args.pid) / 'stat', errors)
    if identity is None:
        emit({'type': 'finish', 'status': 'process_unavailable', 'samples': 0, 'errors': errors})
        return 1
    emit({'type': 'header', 'schemaVersion': 1, 'pid': args.pid,
          'startTicks': identity['startTicks'], 'clockTicksPerSecond': os.sysconf('SC_CLK_TCK'),
          'intervalMs': args.interval_ms, 'requestedSamples': args.samples,
          'diagnosticIntervention': True,
          'units': {'cpuFrequency': 'kHz as reported by cpufreq; not guaranteed hardware frequency',
                    'hwmonTemperatureInput': 'millidegrees Celsius',
                    'hwmonPowerInputAverageCap': 'microwatts',
                    'hwmonFrequencyInput': 'Hz', 'threadTimes': 'clock ticks',
                    'lastProcessor': 'most recently executed logical CPU; not migration count'}})
    count, status = 0, 'sample_limit'
    for index in range(args.samples):
        sample = capture(args.pid, identity['startTicks'], include_maps=index == 0)
        emit({'type': 'sample', **sample})
        if sample['status'] != 'captured':
            status = sample['status']
            break
        count += 1
        if index + 1 < args.samples:
            time.sleep(args.interval_ms / 1000)
    emit({'type': 'finish', 'status': status, 'samples': count})
    return 0 if count and status != 'pid_reused' else 1


if __name__ == '__main__':
    raise SystemExit(main())
