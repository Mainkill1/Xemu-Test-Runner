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
from producer import Producer

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
        self.add_mapping = bind('SDL_GameControllerAddMapping', C.c_int, C.c_char_p)
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
        assert self.init(0x2000) == 0, self.error()
        self.pad = None
        self.pump()
        assert self.count() == 0, 'Use an isolated qualification host with no physical/other virtual controllers.'

    async def connect(self):
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            self.pump()
            if self.count() == 1:
                guid = bytes(self.guid(0)).hex()
                mapping = None
                if sys.platform == 'linux':
                    assert self.joystick_name(0) == b'Xemu Runner Gamepad'
                    template = Path(__file__).resolve().parents[2] / 'native/gamepad/linux-sdl-mapping.txt'
                    mapping = template.read_text().strip().format(guid=guid)
                    assert self.add_mapping(mapping.encode()) >= 0, self.error()
                self.pad = self.open(0)
                if self.pad:
                    assert not self.virtual(0), 'Process-local SDL virtual joystick cannot qualify OS injection.'
                    return {'controllerName': self.name(self.pad).decode(), 'guid': guid, 'sdlMapping': mapping}
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
        await observer.expect([0] * 7)
        # Individually test every core button, preventing swapped aliases from hiding in a combined mask.
        buttons = [1,2,4,8,0x10,0x20,0x40,0x80,0x100,0x200,0x1000,0x2000,0x4000,0x8000]
        for button in buttons:
            producer.state = [button,0,0,0,0,0,0]
            await observer.expect(producer.state)
        patterns = [[0x3141,64,192,-32768,32767,16384,-16384],
                    [0x820a,255,0,32767,-32768,-12345,5432], [0] * 7]
        for pattern in patterns:
            producer.state = pattern
            await observer.expect(pattern)
        producer.state = [0x1000,255,255,10000,10000,-10000,-10000]
        await observer.expect(producer.state)
        assert producer.failure is None, producer.failure
        if teardown == 'watchdog':
            await producer.stop_heartbeat()
            await asyncio.to_thread(producer.proc.wait, 3)
            assert producer.proc.returncode == 3
        elif teardown == 'kill':
            await producer.stop_heartbeat()
            producer.proc.kill(); await asyncio.to_thread(producer.proc.wait, 3)
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
            'scope':'OS API submission and independent SDL readback, not guest consumption'}
        print(json.dumps(result), flush=True)
        return result
    except BaseException:
        if producer:
            print('FAIL controller diagnostics', json.dumps({
                'workerExit': producer.proc.poll(), 'heartbeatFailure': repr(producer.failure),
                'lastReceipt': producer.last_receipt, 'receiptTimes': producer.receipt_times,
                'failureTime': time.monotonic(), 'requestedState': producer.state}), flush=True)
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
    for teardown in ('stop', 'watchdog', 'malformed', 'kill'):
        results.append(await roundtrip(executable, teardown))
    Path('gamepad-results.json').write_text(json.dumps(results, indent=2))

if __name__ == '__main__':
    asyncio.run(main())
