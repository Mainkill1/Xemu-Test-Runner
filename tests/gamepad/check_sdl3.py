"""Run identical OS-input qualification with a separate SDL3 consumer.

The producer still has no SDL dependency and runs in its own process. This
adapter only translates SDL3's ID/bool APIs to the shared observer assertions.
"""
import asyncio
import ctypes as C
import os
import json
import sys
from pathlib import Path
import check_os_gamepad as checks


class Observer3(checks.Observer):
    def __init__(self):
        # SDL3 snapshots the environment on first use. Set the launch mapping
        # before loading/calling SDL, not after an unrelated SDL_SetHint call.
        checks.configure_mapping()
        self.sdl = C.CDLL(os.environ['SDL3_LIBRARY'])

        def bind(name, result, *args):
            fn = getattr(self.sdl, name)
            fn.restype, fn.argtypes = result, list(args)
            return fn

        initialize = bind('SDL_Init', C.c_bool, C.c_uint32)
        bind('SDL_SetHint', C.c_bool, C.c_char_p, C.c_char_p)(
            b'SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS', b'1')
        self.error = bind('SDL_GetError', C.c_char_p)
        self.quit = bind('SDL_Quit', None)
        self.pump = bind('SDL_PumpEvents', None)
        self.update = bind('SDL_UpdateGamepads', None)
        self.ids = []
        get_ids = bind('SDL_GetJoysticks', C.POINTER(C.c_uint32), C.POINTER(C.c_int))
        free = bind('SDL_free', None, C.c_void_p)

        def count():
            length = C.c_int()
            data = get_ids(C.byref(length))
            assert data, self.error()
            try:
                self.ids = [data[i] for i in range(length.value)]
            finally:
                free(data)
            return len(self.ids)

        self.count = count
        open_id = bind('SDL_OpenGamepad', C.c_void_p, C.c_uint32)
        guid_id = bind('SDL_GetJoystickGUIDForID', checks.Guid, C.c_uint32)
        name_id = bind('SDL_GetJoystickNameForID', C.c_char_p, C.c_uint32)
        virtual_id = bind('SDL_IsJoystickVirtual', C.c_bool, C.c_uint32)
        self.open = lambda index: open_id(self.ids[index])
        self.guid = lambda index: guid_id(self.ids[index])
        self.joystick_name = lambda index: name_id(self.ids[index])
        self.virtual = lambda index: virtual_id(self.ids[index])
        self.close = bind('SDL_CloseGamepad', None, C.c_void_p)
        self.attached = bind('SDL_GamepadConnected', C.c_bool, C.c_void_p)
        self.axis = bind('SDL_GetGamepadAxis', C.c_int16, C.c_void_p, C.c_int)
        self.button = bind('SDL_GetGamepadButton', C.c_bool, C.c_void_p, C.c_int)
        self.name = bind('SDL_GetGamepadName', C.c_char_p, C.c_void_p)
        if sys.platform == 'linux':
            hint = bind('SDL_GetHint', C.c_char_p, C.c_char_p)(b'SDL_GAMECONTROLLERCONFIG')
            assert hint == os.environ['SDL_GAMECONTROLLERCONFIG'].encode(), 'SDL cached a different launch mapping'
        assert initialize(0x2000), self.error()
        self.pad = None
        self.pump()
        self.record_baseline()


async def main():
    checks.Observer = Observer3
    results = []
    for teardown in checks.TEARDOWNS:
        result = await checks.roundtrip(str(Path(sys.argv[1]).resolve()), teardown)
        result['observerApi'] = 'SDL3'
        results.append(result)
    Path('gamepad-sdl3-results.json').write_text(json.dumps(results, indent=2))


if __name__ == '__main__':
    asyncio.run(main())
