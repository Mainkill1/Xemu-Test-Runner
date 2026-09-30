"""Test-only OS readback isolation; never injects or acknowledges controller input."""
import asyncio
import importlib
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import traceback


def _serve(factory_module, factory_name):
    observer = None
    def reply(success, value):
        print(json.dumps([success, value]), flush=True)
    try:
        observer = getattr(importlib.import_module(factory_module), factory_name)()
        reply(True, None)
        for line in sys.stdin:
            command, argument = json.loads(line)
            if command == 'dispose':
                observer.dispose()
                observer = None
                reply(True, None)
                return
            if command == 'poll':
                observer.pump()
                result = bool(observer.pad and observer.attached(observer.pad))
            elif command in ('connect', 'expect', 'disconnected'):
                method = getattr(observer, command)
                result = asyncio.run(method(argument) if command == 'expect' else method())
            else:
                raise ValueError('Unknown observer operation: ' + command)
            reply(True, result)
    except BaseException:
        reply(False, traceback.format_exc())
    finally:
        if observer is not None:
            observer.dispose()


class IsolatedObserver:
    """Keep consumer DLLs/emulation outside the heartbeat producer process.

    JSON pipes deliberately allow native ARM64 Python to supervise an x64 SDL
    consumer without transferring architecture-specific Python import paths.
    Native worker receipts are still verified exclusively by Producer.
    """
    def __init__(self, factory):
        self._lock = threading.Lock()
        self._connected = False
        self._disposed = False
        self.pad = True
        self._responses = queue.Queue()
        module = factory.__module__
        if module == '__main__':
            module = Path(sys.modules[module].__file__).stem
        executable = os.environ.get('GAMEPAD_OBSERVER_PYTHON', sys.executable)
        # Readback is not a benchmark. Do not let an emulated consumer outrank
        # the normal-priority producer and native worker on constrained CI VMs.
        # This only lowers our own test child; it changes no host policy, timer,
        # production target, worker deadline, state assertion or receipt check.
        flags = subprocess.BELOW_NORMAL_PRIORITY_CLASS if os.name == 'nt' else 0
        self._process = subprocess.Popen(
            [executable, str(Path(__file__).resolve()), '--serve', module, factory.__name__],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding='utf-8',
            creationflags=flags)
        self._reader = threading.Thread(target=self._read, name='observer-replies')
        self._reader.start()
        try:
            self._receive(30)
        except BaseException:
            self._terminate()
            raise

    @property
    def pid(self):
        return self._process.pid

    def _read(self):
        try:
            for line in self._process.stdout:
                self._responses.put(json.loads(line))
        except BaseException as error:
            self._responses.put((False, repr(error)))
        finally:
            self._responses.put((False, 'Independent observer exited before replying'))

    def _receive(self, timeout=15):
        try:
            success, value = self._responses.get(timeout=timeout)
        except queue.Empty as error:
            raise TimeoutError('Independent OS observer stopped responding') from error
        if not success:
            raise AssertionError('Independent OS observer failed:\n' + value)
        return value

    def _call(self, command, argument=None):
        with self._lock:
            self._process.stdin.write(json.dumps([command, argument]) + '\n')
            self._process.stdin.flush()
            return self._receive()

    async def connect(self):
        return await asyncio.to_thread(self._call, 'connect')

    async def expect(self, state):
        return await asyncio.to_thread(self._call, 'expect', state)

    async def disconnected(self):
        return await asyncio.to_thread(self._call, 'disconnected')

    def pump(self):
        self._connected = self._call('poll')

    def attached(self, _pad):
        return self._connected

    def _terminate(self):
        try:
            self._process.stdin.close()
        except OSError:
            pass
        try:
            self._process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            self._process.kill()
            self._process.wait(timeout=3)
        self._reader.join(3)
        if self._reader.is_alive():
            raise AssertionError('Independent OS observer leaked its reply reader')
        self._process.stdout.close()

    def dispose(self):
        if self._disposed:
            return
        self._disposed = True
        try:
            if self._process.poll() is None:
                self._call('dispose')
        finally:
            self._terminate()


if __name__ == '__main__':
    if len(sys.argv) != 4 or sys.argv[1] != '--serve':
        raise SystemExit('Only the test launcher may start an observer')
    _serve(sys.argv[2], sys.argv[3])
