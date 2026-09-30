"""Independent Windows XInput 1.4 readback. No SDL, DLL hooks or driver install."""
import asyncio
import ctypes as C
import json
import os
from pathlib import Path
import platform
import sys
import time
from producer import Producer

class Gamepad(C.Structure):
    _fields_ = [('buttons', C.c_uint16), ('lt', C.c_ubyte), ('rt', C.c_ubyte),
                ('lx', C.c_int16), ('ly', C.c_int16), ('rx', C.c_int16), ('ry', C.c_int16)]

class State(C.Structure):
    _fields_ = [('packet', C.c_uint32), ('gamepad', Gamepad)]

def snapshot():
    """Failure diagnostics: compare the named API with the ordinal SDL uses."""
    if os.name != 'nt':
        return None
    dll = C.WinDLL('XInput1_4.dll')
    result = {}
    for label, symbol in (('XInputGetState', 'XInputGetState'), ('SDL-ordinal-100', 100)):
        try:
            fn = dll[symbol]
        except AttributeError:
            result[label] = {'available': False}
            continue
        fn.argtypes, fn.restype = [C.c_uint32, C.POINTER(State)], C.c_uint32
        slots = []
        for slot in range(4):
            state = State()
            code = fn(slot, C.byref(state))
            slots.append({'slot': slot, 'code': code, 'packet': state.packet,
                          'state': [getattr(state.gamepad, n) for n, _ in Gamepad._fields_]})
        result[label] = slots
    return result


def enable_probe():
    """Failure-only probe: distinguish disabled XInput reporting from lost input."""
    if os.name != 'nt':
        return None
    dll = C.WinDLL('XInput1_4.dll')
    enable = dll.XInputEnable
    enable.argtypes, enable.restype = [C.c_int], None
    enable(1)
    time.sleep(0.05)
    return snapshot()


async def main():
    assert os.name == 'nt', 'XInput qualification requires Windows'
    dll = C.WinDLL('XInput1_4.dll')
    get_state = dll.XInputGetState
    get_state.argtypes = [C.c_uint32, C.POINTER(State)]
    get_state.restype = C.c_uint32
    def read():
        result = {}
        for slot in range(4):
            state = State()
            code = get_state(slot, C.byref(state))
            if code == 0:
                result[slot] = [getattr(state.gamepad, name) for name, _ in Gamepad._fields_]
            else:
                assert code == 1167, ('XInput error', code)
        return result
    assert not read(), 'Use an isolated qualification host without other controllers'
    producer = await Producer.start(str(Path(sys.argv[1]).resolve()))
    try:
        observed = []
        patterns = [[0] * 7] + [[b, 0, 0, 0, 0, 0, 0] for b in
            [1, 2, 4, 8, 0x10, 0x20, 0x40, 0x80, 0x100, 0x200, 0x1000, 0x2000, 0x4000, 0x8000]]
        patterns += [[0x3141, 64, 192, -32768, 32767, 16384, -16384],
                     [0x820a, 255, 0, 32767, -32768, -12345, 5432], [0] * 7]
        for pattern in patterns:
            after = producer.set_state(pattern)
            await producer.wait_applied_state(pattern, after)
            deadline = time.monotonic() + 5
            last = None
            while time.monotonic() < deadline:
                assert producer.failure is None, producer.failure
                last = read()
                if len(last) == 1:
                    actual = next(iter(last.values()))
                    if actual[0] == pattern[0] and all(abs(a-b) <= 2 for a,b in zip(actual[1:], pattern[1:])):
                        observed.append(actual)
                        break
                await asyncio.sleep(0.01)
            else:
                raise AssertionError({'requested': pattern, 'observed': last,
                    'lastReceipt': producer.last_receipt, 'workerExit': producer.proc.poll(),
                    'pipeBoundary': producer.snapshot()})
        await producer.dispose()
        assert producer.proc.returncode == 0, producer.details
        deadline = time.monotonic() + 3
        while read() and time.monotonic() < deadline:
            await asyncio.sleep(0.01)
        assert not read(), 'XInput device remained after worker teardown'
        result = {'result': 'passed', 'platform': platform.platform(), 'consumerApi': 'XInput1_4',
                  'patterns': observed, 'backend': producer.ready,
                  'scope': 'independent OS readback, not guest consumption'}
        Path('gamepad-xinput-results.json').write_text(json.dumps(result, indent=2))
        print(json.dumps(result), flush=True)
    finally:
        await producer.dispose()
        if producer.details:
            print('WORKER STDERR:', producer.details, flush=True)

if __name__ == '__main__':
    asyncio.run(main())
