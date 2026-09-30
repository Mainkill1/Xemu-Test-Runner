"""Scheduling-only regression, not an OS controller qualification."""
import asyncio
import sys
import threading
import time
from isolated_observer import IsolatedObserver


class BusyObserver:
    async def expect(self, state):
        previous = sys.getswitchinterval()
        try:
            # Model a native callback/emulation path monopolizing this process.
            sys.setswitchinterval(1)
            deadline = time.monotonic() + 0.5
            while time.monotonic() < deadline:
                pass
            return state
        finally:
            sys.setswitchinterval(previous)

    def dispose(self):
        pass


async def check(isolate=True):
    factory = BusyObserver
    try:
        # The real launcher replaces checks.Observer with its proxy factory.
        # Spawning must resolve the original class in the fresh child module.
        if isolate:
            globals()['BusyObserver'] = None
        observer = IsolatedObserver(factory) if isolate else factory()
    finally:
        globals()['BusyObserver'] = factory
    stopped = threading.Event()
    ticks = []
    def heartbeat():
        while not stopped.wait(0.025):
            ticks.append(time.monotonic())
    sender = threading.Thread(target=heartbeat)
    sender.start()
    try:
        await asyncio.sleep(0.1)
        before = len(ticks)
        assert await observer.expect([4096, 0, 0, 0, 0, 0, 0]) == [4096, 0, 0, 0, 0, 0, 0]
        assert len(ticks) - before >= 8, 'Observer GIL stall starved independent heartbeat scheduling'
    finally:
        stopped.set()
        sender.join(3)
        observer.dispose()
        observer.dispose()
    assert not sender.is_alive()
    print('PASS independent observer preserves heartbeat scheduling and cleans up')


if __name__ == '__main__':
    asyncio.run(check('--unisolated' not in sys.argv))
