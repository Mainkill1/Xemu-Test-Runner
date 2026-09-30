"""Pipe scheduling regression only; does not simulate or qualify an OS gamepad."""
import asyncio
import json
from pathlib import Path
import sys
import subprocess
import time


def echo_pipe(batch=False):
    print(json.dumps({'type': 'ready', 'additionalDriver': False,
                      'scope': 'pipe-fixture-not-a-gamepad'}), flush=True)
    delayed = []
    for line in sys.stdin:
        if line.strip() == 'stop':
            return
        reply = json.dumps({'type': 'applied', 'sequence': int(line.split()[1]),
                            'appliedAtUs': time.monotonic_ns() // 1000})
        if batch:
            delayed.append(reply)
            if len(delayed) < 8:
                continue
            print('\n'.join(delayed), flush=True)
            batch = False
        else:
            print(reply, flush=True)


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
        diagnostic = sender.snapshot()
        assert diagnostic['lastSendCompletedAt'] is not None
        assert diagnostic['lastSendSequence'] >= before + 8
        assert diagnostic['lastReceiptSequence'] > 0
        assert diagnostic['pendingCount'] >= 0
        state = [0x1000, 0, 0, 0, 0, 0, 0]
        after = sender.set_state(state)
        receipt = await sender.wait_applied_state(state, after)
        assert receipt['sequence'] > after and receipt['state'] == state
        print('PASS heartbeat sender progresses while consumer thread blocks')
    finally:
        await sender.dispose()
        await sender.dispose()  # A second owner cleanup must be harmless.
    sender = await Producer.start([sys.executable, str(Path(__file__).resolve()), '--batch-echo'])
    try:
        deadline = time.monotonic() + 2
        while sender.sequence < 8 and time.monotonic() < deadline:
            await asyncio.sleep(0.01)
        assert sender.sequence >= 8, 'Delayed receipts blocked fresh input submission'
        deadline = time.monotonic() + 2
        while (sender.last_receipt or {}).get('sequence', 0) < 8 and time.monotonic() < deadline:
            await asyncio.sleep(0.01)
        assert (sender.last_receipt or {}).get('sequence', 0) >= 8, 'Submitted input was not independently acknowledged'
        assert sender.failure is None, sender.failure
        await sender.stop_heartbeat()
        assert sender.last_receipt['sequence'] == sender.sequence, 'Shutdown lost pending receipts'
        print('PASS delayed receipts do not block bounded fresh-state submission')
    finally:
        if sender.sequence < 8:
            sender.proc.kill()
        await sender.dispose()


if __name__ == '__main__':
    if sys.argv[1:] == ['--fail']:
        print('intentional startup error', file=sys.stderr, flush=True)
        sys.exit(3)
    if sys.argv[1:] in (['--echo'], ['--batch-echo']):
        echo_pipe(sys.argv[1:] == ['--batch-echo'])
    else:
        asyncio.run(check())
