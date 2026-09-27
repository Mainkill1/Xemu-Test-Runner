"""Pipe scheduling regression only; does not simulate or qualify an OS gamepad."""
import asyncio
import json
from pathlib import Path
import sys
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


if __name__ == '__main__':
    if sys.argv[1:] == ['--echo']:
        echo_pipe()
    else:
        asyncio.run(check())
