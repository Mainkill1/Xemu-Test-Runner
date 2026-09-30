"""Read real OS controller state in a different process from the user-mode adapter.
No mocks, custom drivers, SDL virtual devices, or xemu modifications.
"""
import asyncio
import ctypes as C
import ctypes.util
import json
import os
from pathlib import Path
import platform
import sys
import time
import threading
from producer import Producer

TEARDOWNS = ('stop', 'watchdog', 'malformed', 'kill', 'eof', 'partial', 'stale', 'backpressure')


def configure_mapping():
    if sys.platform == 'linux':
        path = Path(__file__).resolve().parents[2] / 'native/gamepad/linux-sdl-mapping.txt'
        mapping = path.read_text().strip()
        assert len(mapping.split(',')[0]) == 32 and '{' not in mapping
        os.environ['SDL_GAMECONTROLLERCONFIG'] = mapping
    elif os.name == 'nt':
        # The supervisor puts this in only the target environment before SDL init.
        assert os.environ.get('SDL_JOYSTICK_RAWINPUT') == '0'


class Guid(C.Structure):
    _fields_ = [('data', C.c_ubyte * 16)]

class Observer:
    def __init__(self):
        lib = os.environ.get('SDL2_LIBRARY') or ctypes.util.find_library('SDL2-2.0') or 'SDL2.dll'
        self.sdl = C.CDLL(lib)
        def bind(name, restype, *args):
            f = getattr(self.sdl, name); f.restype = restype; f.argtypes = list(args); return f
        self.init = bind('SDL_Init', C.c_int, C.c_uint32)
        bind('SDL_SetHint', C.c_int, C.c_char_p, C.c_char_p)(b'SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS', b'1')
        self.count = bind('SDL_NumJoysticks', C.c_int)
        self.pump = bind('SDL_PumpEvents', None)
        self.update = bind('SDL_GameControllerUpdate', None)
        self.open = bind('SDL_GameControllerOpen', C.c_void_p, C.c_int)
        self.joystick_name = bind('SDL_JoystickNameForIndex', C.c_char_p, C.c_int)
        self.close = bind('SDL_GameControllerClose', None, C.c_void_p)
        self.attached = bind('SDL_GameControllerGetAttached', C.c_int, C.c_void_p)
        self.axis = bind('SDL_GameControllerGetAxis', C.c_int16, C.c_void_p, C.c_int)
        self.button = bind('SDL_GameControllerGetButton', C.c_uint8, C.c_void_p, C.c_int)
        self.name = bind('SDL_GameControllerName', C.c_char_p, C.c_void_p)
        self.guid = bind('SDL_JoystickGetDeviceGUID', Guid, C.c_int)
        self.virtual = bind('SDL_JoystickIsVirtual', C.c_int, C.c_int)
        self.error = bind('SDL_GetError', C.c_char_p)
        self.quit = bind('SDL_Quit', None)
        configure_mapping()
        assert self.init(0x2000) == 0, self.error()
        self.pad = None
        self.pump()
        self.baseline_count = self.count()
        if sys.platform == 'linux':
            self.expected_guid = os.environ['SDL_GAMECONTROLLERCONFIG'].split(',')[0]
            assert all(bytes(self.guid(i)).hex() != self.expected_guid
                       for i in range(self.baseline_count)), \
                'A preexisting controller has the test helper GUID.'
        else:
            assert self.baseline_count == 0, \
                'Use an isolated Windows qualification host with no other controllers.'

    async def connect(self):
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            self.pump()
            if self.count() == self.baseline_count + 1:
                if sys.platform == 'linux':
                    matches = [i for i in range(self.count())
                               if bytes(self.guid(i)).hex() == self.expected_guid]
                    assert len(matches) == 1, ('Ambiguous helper GUID', matches)
                    index = matches[0]
                else:
                    index = 0
                guid = bytes(self.guid(index)).hex()
                mapping = None
                if sys.platform == 'linux':
                    assert self.joystick_name(index) == b'Xemu Runner Gamepad'
                    mapping = os.environ['SDL_GAMECONTROLLERCONFIG']
                    assert mapping.split(',')[0] == guid, ('Installed mapping GUID mismatch', guid)
                    # No SDL_AddMapping call: unmodified xemu gets the same launch environment.
                self.pad = self.open(index)
                if self.pad:
                    assert not self.virtual(index), 'Process-local SDL virtual joystick cannot qualify OS injection.'
                    return {'controllerName': self.name(self.pad).decode(), 'guid': guid,
                            'sdlMapping': mapping, 'preexistingControllers': self.baseline_count}
            await asyncio.sleep(0.01)
        raise AssertionError(('OS gamepad not visible through SDL', self.count(), self.error()))

    def state(self):
        self.pump(); self.update()
        if not self.attached(self.pad): return None
        # SDL's standard ordering -> our canonical XInput-style flags.
        flags = [0x1000, 0x2000, 0x4000, 0x8000, 0x20, 0x400, 0x10, 0x40, 0x80,
                 0x100, 0x200, 1, 2, 4, 8]
        buttons = sum(mask for i, mask in enumerate(flags) if self.button(self.pad, i))
        return [buttons] + [self.axis(self.pad, i) for i in range(6)]

    async def expect(self, state):
        buttons, lt, rt, lx, ly, rx, ry = state
        inv = lambda x: 32767 if x == -32768 else -32768 if x == 32767 else -x
        expected = [buttons, lx, inv(ly), rx, inv(ry), round(lt * 32767 / 255), round(rt * 32767 / 255)]
        deadline = time.monotonic() + 3
        last = None
        while time.monotonic() < deadline:
            last = self.state()
            # Signed-axis conversion permits 2 LSB; trigger conversion permits 1 source byte.
            if last and last[0] == expected[0] and all(abs(last[i]-expected[i]) <= (130 if i >= 5 else 2) for i in range(1,7)):
                return last
            await asyncio.sleep(0.005)
        raise AssertionError({'expectedSDL': expected, 'observedSDL': last})

    async def disconnected(self):
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            self.pump()
            if not self.attached(self.pad): return
            await asyncio.sleep(0.01)
        raise AssertionError('Virtual controller remained connected after teardown.')

    def dispose(self):
        if self.pad: self.close(self.pad)
        self.quit()

async def roundtrip(executable, teardown):
    observer = Observer()
    producer = None
    try:
        producer = await Producer.start(executable)
        print('OS helper ready', json.dumps(producer.ready), flush=True)
        device = await observer.connect()
        print('Independent consumer connected', json.dumps(device), flush=True)
        async def apply_and_observe(state):
            after = producer.set_state(state)
            await producer.wait_applied_state(state, after)
            return await observer.expect(state)
        await apply_and_observe([0] * 7)
        # Individually test every core button, preventing swapped aliases from hiding in a combined mask.
        buttons = [1,2,4,8,0x10,0x20,0x40,0x80,0x100,0x200,0x1000,0x2000,0x4000,0x8000]
        for button in buttons:
            await apply_and_observe([button,0,0,0,0,0,0])
        patterns = [[0x3141,64,192,-32768,32767,16384,-16384],
                    [0x820a,255,0,32767,-32768,-12345,5432], [0] * 7]
        for pattern in patterns:
            await apply_and_observe(pattern)
        await apply_and_observe([0x1000,255,255,10000,10000,-10000,-10000])
        assert producer.failure is None, producer.failure
        if teardown == 'watchdog':
            await producer.stop_heartbeat()
            await asyncio.to_thread(producer.proc.wait, 3)
            assert producer.proc.returncode == 3
        elif teardown == 'kill':
            await producer.stop_heartbeat()
            producer.proc.kill(); await asyncio.to_thread(producer.proc.wait, 3)
        elif teardown == 'backpressure':
            await producer.stop_heartbeat()
            # Fill stdout without reading receipts. The native device thread
            # must expire even while the protocol thread is blocked in output.
            payload = ''.join('state ' + str(producer.sequence + i) +
                              ' 4096 255 255 10000 10000 -10000 -10000\n'
                              for i in range(1, 20_001)).encode()
            def flood():
                try:
                    producer.proc.stdin.write(payload)
                    producer.proc.stdin.flush()
                except OSError:
                    pass  # Expected after killing the blocked fixture process.
            writer = threading.Thread(target=flood, name='backpressure-fixture', daemon=True)
            writer.start()
            try:
                await observer.disconnected()
                assert producer.proc.poll() is None and writer.is_alive(), (
                    'Did not establish blocked output while device disappeared')
            finally:
                if producer.proc.poll() is None:
                    producer.proc.kill()
                await asyncio.to_thread(producer.proc.wait, 3)
                await asyncio.to_thread(writer.join, 3)
                assert not writer.is_alive(), 'Backpressure test leaked its writer'
        elif teardown in ('eof', 'partial', 'stale'):
            await producer.stop_heartbeat()
            if teardown == 'eof':
                producer.proc.stdin.close()
            elif teardown == 'partial':
                producer.proc.stdin.write(b'state 9999 '); producer.proc.stdin.flush()
            else:
                producer.proc.stdin.write(('state ' + str(producer.sequence) + ' 0 0 0 0 0 0 0\n').encode())
                producer.proc.stdin.flush()
            await asyncio.to_thread(producer.proc.wait, 3)
            assert producer.proc.returncode == (0 if teardown == 'eof' else 3)
        elif teardown == 'malformed':
            await producer.stop_heartbeat()
            producer.proc.stdin.write(b'state 9999 0 999 0 0 0 0 0\n'); producer.proc.stdin.flush()
            await asyncio.to_thread(producer.proc.wait, 3)
            assert producer.proc.returncode == 3
        else:
            await producer.dispose()
            assert producer.proc.returncode == 0
        await observer.disconnected()
        result = {'result':'passed','platform':platform.platform(), 'observerProcess':os.getpid(),
            'producerProcess':producer.proc.pid,'backend':producer.ready,'device':device,
            'teardown':teardown,'buttonsChecked':len(buttons),'analogPatternsChecked':len(patterns),
            'lastAppliedReceipt':producer.last_receipt,
            'launchEnvironment': {key: os.environ[key] for key in ('SDL_JOYSTICK_RAWINPUT', 'SDL_GAMECONTROLLERCONFIG') if key in os.environ},
            'scope':'OS API submission and independent SDL readback, not guest consumption'}
        print(json.dumps(result), flush=True)
        return result
    except BaseException:
        if producer:
            print('FAIL controller diagnostics', json.dumps({
                'workerExit': producer.proc.poll(), 'heartbeatFailure': repr(producer.failure),
                'lastReceipt': producer.last_receipt, 'receiptTimes': producer.receipt_times,
                'failureTime': time.monotonic(), 'requestedState': producer.state,
                'pipeBoundary': producer.snapshot()}), flush=True)
            if os.name == 'nt':
                from check_xinput import snapshot, enable_probe
                print('INDEPENDENT XINPUT DIAGNOSTICS', json.dumps(snapshot()), flush=True)
                if producer.proc.poll() is None and producer.last_receipt is not None:
                    print('XINPUT ENABLE FAILURE PROBE', json.dumps(enable_probe()), flush=True)
        raise
    finally:
        if producer:
            await producer.dispose()
            if producer.details:
                print('WORKER STDERR:', producer.details, flush=True)
        observer.dispose()

async def main():
    executable = str(Path(sys.argv[1]).resolve())
    results = []
    for teardown in TEARDOWNS:
        results.append(await roundtrip(executable, teardown))
    Path('gamepad-results.json').write_text(json.dumps(results, indent=2))

if __name__ == '__main__':
    asyncio.run(main())
