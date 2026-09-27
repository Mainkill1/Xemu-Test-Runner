"""Pipe scheduling regression only; does not simulate or qualify an OS gamepad."""
import asyncio
import json
from pathlib import Path
import sys
import subprocess
import time


def echo_pipe():
    print(json.dumps({'type': 'ready', 'additionalDriver': False,
                      'scope': 'pipe-fixture-not-a-gamepad'}), flush=True)
    for line in sys.stdin:
        if line.strip() == 'stop':
            return
        print(json.dumps({'type': 'applied', 'sequence': int(line.split()[1]),
                          'appliedAtUs': time.monotonic_ns() // 1000}), flush=True)


async def check():
    from producer import Producer
    exited = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), '--fail'],
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    await asyncio.to_thread(exited.wait, 3)
    exited.stdin.write(b'buffered-before-exit')
    failed_sender = Producer(exited)
    await failed_sender.dispose()
    await failed_sender.dispose()
    assert 'intentional startup error' in failed_sender.details
    # Failure diagnostics must survive both start() cleanup paths.
    try:
        await Producer.start([sys.executable, str(Path(__file__).resolve()), '--fail'])
    except AssertionError as error:
        assert 'intentional startup error' in str(error), str(error)
    else:
        raise AssertionError('A failed worker was accepted')
    sender = await Producer.start([sys.executable, str(Path(__file__).resolve()), '--echo'])
    try:
        deadline = time.monotonic() + 3
        while sender.sequence < 2 and time.monotonic() < deadline:
            await asyncio.sleep(0.01)
        before = sender.sequence
        # SDL hotplug/open can block the main thread. Do not starve the sender.
        time.sleep(0.4)
        assert sender.sequence - before >= 8, 'Consumer blocking starved controller heartbeats'
        assert sender.failure is None, sender.failure
        print('PASS heartbeat sender progresses while consumer thread blocks')
    finally:
        await sender.dispose()
        await sender.dispose()  # A second owner cleanup must be harmless.


if __name__ == '__main__':
    if sys.argv[1:] == ['--fail']:
        print('intentional startup error', file=sys.stderr, flush=True)
        sys.exit(3)
    if sys.argv[1:] == ['--echo']:
        echo_pipe()
    else:
        asyncio.run(check())
