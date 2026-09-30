"""Run unchanged SDL assertions with readback isolated from the pipe producer."""
import asyncio
import json
import os
from pathlib import Path
import sys
import check_os_gamepad as checks
from isolated_observer import IsolatedObserver


async def main():
    if len(sys.argv) != 3 or sys.argv[1] not in ('sdl2', 'sdl3'):
        raise SystemExit('usage: qualify_isolated.py sdl2|sdl3 WORKER')
    api = sys.argv[1]
    if os.name == 'nt':
        # Diagnose competing WGI enumeration while reading this XInput device.
        # This is a qualification experiment, not a production launch change.
        os.environ['SDL_JOYSTICK_WGI'] = '0'
        print('Qualification environment', json.dumps({key: os.environ.get(key) for key in
              ('SDL_JOYSTICK_RAWINPUT', 'SDL_JOYSTICK_WGI', 'GAMEPAD_OBSERVER_PYTHON')}), flush=True)
    if api == 'sdl3':
        from check_sdl3 import Observer3
        factory = Observer3
    else:
        factory = checks.Observer
    observers = []
    def create_observer():
        observer = IsolatedObserver(factory)
        observers.append(observer)
        return observer
    checks.Observer = create_observer
    results = []
    for teardown in checks.TEARDOWNS:
        result = await checks.roundtrip(str(Path(sys.argv[2]).resolve()), teardown)
        result['observerApi'] = api.upper()
        result['observerProcess'] = observers[-1].pid
        result['observerIsolation'] = 'spawned-process'
        result['launchEnvironment']['SDL_JOYSTICK_WGI'] = os.environ.get('SDL_JOYSTICK_WGI')
        results.append(result)
    name = 'gamepad-sdl3-results.json' if api == 'sdl3' else 'gamepad-results.json'
    Path(name).write_text(json.dumps(results, indent=2))


if __name__ == '__main__':
    asyncio.run(main())
