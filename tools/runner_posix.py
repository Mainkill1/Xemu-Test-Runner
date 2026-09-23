#!/usr/bin/env python3
"""Private direct-launch adapter: retain native wait status without a debugger.

The C# runner owns admission, lifetime, timeout and reporting. This adapter only
execs one target after a bounded handshake, then records waitpid's exit/signal.
No shell, retries, test selection or network operations are implemented here.
"""
from __future__ import annotations
import json
import os
from pathlib import Path
import signal
import sys
import time


def publish(path: Path, value: dict) -> None:
    temporary = path.with_suffix(path.suffix + '.tmp')
    with temporary.open('x', encoding='utf-8') as output:
        json.dump(value, output, separators=(',', ':'))
        output.flush()
        os.fsync(output.fileno())
    os.replace(temporary, path)


def main() -> int:
    directory, identity, executable, *arguments = sys.argv[1:]
    root = Path(directory)
    read_gate, write_gate = os.pipe()
    pid = os.fork()
    if pid == 0:
        os.close(write_gate)
        try:
            permitted = os.read(read_gate, 1) == b'1'
            os.close(read_gate)
            if not permitted:
                os._exit(125)
            for value in (signal.SIGINT, signal.SIGTERM, signal.SIGPIPE):
                signal.signal(value, signal.SIG_DFL)
            os.execv(executable, [executable, *arguments])
        except BaseException as error:
            try:
                publish(root / 'exec-error.json', {'error': str(error)[:1024]})
            finally:
                os._exit(127)
    os.close(read_gate)

    def forward(number, _frame):
        try:
            os.kill(pid, number)
        except ProcessLookupError:
            pass

    for number in (signal.SIGINT, signal.SIGTERM):
        signal.signal(number, forward)
    reaped = False
    try:
        publish(root / 'ready.json', {'identity': identity, 'pid': pid})
        end = time.monotonic() + 10
        while not (root / 'start').exists():
            if time.monotonic() >= end:
                os.kill(pid, signal.SIGKILL)
                os.waitpid(pid, 0)
                reaped = True
                return 125
            time.sleep(0.01)
        os.write(write_gate, b'1')
        os.close(write_gate)
        write_gate = -1
        _, status = os.waitpid(pid, 0)
        reaped = True
        terminated_signal = os.WTERMSIG(status) if os.WIFSIGNALED(status) else None
        exit_code = os.WEXITSTATUS(status) if os.WIFEXITED(status) else None
        publish(root / 'exit-native.json', {
            'identity': identity, 'pid': pid, 'exitCode': exit_code,
            'signal': terminated_signal,
            'coreDumped': bool(os.WCOREDUMP(status)) if terminated_signal else False,
            'finishedUtcUnixMs': time.time_ns() // 1_000_000
        })
        return exit_code if exit_code is not None else 128 + int(terminated_signal or 0)
    finally:
        if write_gate >= 0:
            os.close(write_gate)
        # When publication/handshake fails, an unowned child must not continue.
        if not reaped:
            try:
                os.kill(pid, signal.SIGKILL)
                os.waitpid(pid, 0)
            except (ProcessLookupError, ChildProcessError):
                pass


if __name__ == '__main__':
    raise SystemExit(main())
