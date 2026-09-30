"""Test-only SDL isolation: readback cannot hold the heartbeat producer's GIL."""
import asyncio
import importlib
import multiprocessing
import threading
import traceback


def _serve(connection, factory_module, factory_name):
    observer = None
    try:
        factory = getattr(importlib.import_module(factory_module), factory_name)
        observer = factory()
        connection.send((True, None))
        while True:
            command, argument = connection.recv()
            if command == 'dispose':
                observer.dispose()
                observer = None
                connection.send((True, None))
                return
            if command == 'poll':
                observer.pump()
                result = bool(observer.pad and observer.attached(observer.pad))
            elif command in ('connect', 'expect', 'disconnected'):
                method = getattr(observer, command)
                result = asyncio.run(method(argument) if command == 'expect' else method())
            else:
                raise ValueError('Unknown observer operation: ' + command)
            connection.send((True, result))
    except EOFError:
        pass
    except BaseException:
        try:
            connection.send((False, traceback.format_exc()))
        except (BrokenPipeError, EOFError, OSError):
            pass
    finally:
        if observer is not None:
            observer.dispose()
        connection.close()


class IsolatedObserver:
    """Preserve the readback API, but run all SDL calls in a spawned process.

    Only test assertions/results cross this private pipe. No state is injected
    or acknowledged here: Producer still validates every native worker receipt.
    """
    def __init__(self, factory):
        context = multiprocessing.get_context('spawn')
        self._connection, child = context.Pipe()
        self._lock = threading.Lock()
        self._connected = False
        self._disposed = False
        self.pad = True  # Opaque compatibility token; never a native pointer.
        self._process = context.Process(target=_serve, args=(child, factory.__module__, factory.__name__),
                                        name='independent-sdl-observer')
        self._process.start()
        child.close()
        try:
            self._receive(30)
        except BaseException:
            self._terminate()
            raise

    @property
    def pid(self):
        return self._process.pid

    def _receive(self, timeout=15):
        if not self._connection.poll(timeout):
            raise TimeoutError('Independent SDL observer stopped responding')
        success, value = self._connection.recv()
        if not success:
            raise AssertionError('Independent SDL observer failed:\n' + value)
        return value

    def _call(self, command, argument=None):
        with self._lock:
            self._connection.send((command, argument))
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
        self._connection.close()
        self._process.join(3)
        if self._process.is_alive():
            self._process.terminate()
            self._process.join(3)
        if self._process.is_alive():
            self._process.kill()
            self._process.join(3)
        if self._process.is_alive():
            raise AssertionError('Independent SDL observer survived forced cleanup')

    def dispose(self):
        if self._disposed:
            return
        self._disposed = True
        try:
            if self._process.is_alive():
                self._call('dispose')
        finally:
            self._terminate()
