#!/usr/bin/env python3
"""Permit the owning runner's debugger, then exec the diagnostic target.

Linux only; launch explicitly as a packaged diagnostic job, never a benchmark.
The owning XemuTestRunner ancestor and its descendants may attach to this
process. No global sysctl, unrestricted ptracer, elevated privilege or target
memory change is used. PID and native exit/signal behavior survive exec.
"""
import ctypes
import json
import os
from pathlib import Path
import sys


def find_runner_owner(pid: int, proc_root: Path = Path('/proc')) -> int | None:
    seen = set()
    while pid > 1:
        if pid in seen or len(seen) >= 64:
            raise ValueError('Invalid or excessive process ancestry')
        seen.add(pid)
        directory = proc_root / str(pid)
        arguments = (directory / 'cmdline').read_bytes().split(b'\0')
        names = [Path(os.fsdecode(value)).name for value in arguments if value]
        if names and (names[0] == 'XemuTestRunner' or
                      (names[0] == 'dotnet' and len(names) > 1 and
                       names[1] == 'XemuTestRunner.dll')):
            return pid
        parents = [line.split(':', 1)[1].strip()
                   for line in (directory / 'status').read_text().splitlines()
                   if line.startswith('PPid:')]
        if len(parents) != 1:
            raise ValueError('Missing or ambiguous parent process')
        pid = int(parents[0])
    return None


def main(arguments):
    if sys.platform != 'linux':
        raise ValueError('Diagnostic owner attachment is Linux-only')
    if len(arguments) < 2 or arguments[0] != '--':
        raise ValueError('Usage: runner_debug_launch.py -- PROGRAM [ARGUMENT ...]')
    target = Path(arguments[1]).resolve(strict=True)
    if not target.is_file() or not os.access(target, os.X_OK):
        raise ValueError('Diagnostic target must be an executable file')
    owner = find_runner_owner(os.getppid())
    if owner is None:
        raise ValueError('No owning XemuTestRunner ancestor; attachment was not enabled')
    libc = ctypes.CDLL(None, use_errno=True)
    libc.prctl.argtypes = [ctypes.c_int] + [ctypes.c_ulong] * 4
    libc.prctl.restype = ctypes.c_int
    # Linux PR_SET_PTRACER: authorize this one ancestor and its descendants.
    # https://docs.kernel.org/admin-guide/LSM/Yama.html
    if libc.prctl(0x59616d61, owner, 0, 0, 0) != 0:
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error))
    print('XEMU_DIAGNOSTIC_PTRACER ' + json.dumps(dict(
        target=str(target), pid=os.getpid(), ownerPid=owner)), file=sys.stderr, flush=True)
    os.execv(str(target), [str(target), *arguments[2:]])


if __name__ == '__main__':
    try:
        main(sys.argv[1:])
    except (OSError, ValueError) as error:
        print('runner_debug_launch: ' + str(error), file=sys.stderr)
        raise SystemExit(1)
