"""Black-box encoder selection checks; plugin presence alone is not qualification."""
import asyncio
import json
import os
from pathlib import Path
import sys

async def invoke(worker, *args, expected=0):
    proc = await asyncio.create_subprocess_exec(str(worker), *args,
        stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    try:
        out, err = await asyncio.wait_for(proc.communicate(), 20)
    except BaseException:
        if proc.returncode is None:
            proc.kill()
            await proc.wait()
        raise
    assert (proc.returncode == 0) == (expected == 0), (args, proc.returncode, out, err)
    return [json.loads(line) for line in out.splitlines() if line.startswith(b'{')]

async def main():
    worker = Path(sys.argv[1]).resolve()
    catalog = (await invoke(worker, '--list-encoders'))[-1]
    assert catalog['type'] == 'encoders', catalog
    assert {'x264', 'nvenc', 'vaapi', 'qsv', 'amf'} <= {x['id'] for x in catalog['items']}, catalog
    probe = (await invoke(worker, '--probe', '--encoder', 'x264'))[-1]
    assert probe['passed'] and probe['encodedFrames'] == 120 and probe['encodedBytes'] > 0, probe
    assert probe['width'] == 1280 and probe['height'] == 720 and probe['fps'] == 60, probe
    assert probe['framesPerSecond'] > 0 and not probe['browserQualified'], probe
    await invoke(worker, '--fixture', '--encoder', 'not-a-backend', expected=2)
    await invoke(worker, '--fixture', '--bitrate-kbps', '0', expected=2)
    await invoke(worker, '--fixture', '--bitrate-kbps', '10000junk', expected=2)
    await invoke(worker, '--fixture', '--resolution', '320x240', expected=2)
    await invoke(worker, '--fixture', '--window', 'nothex', expected=2)
    print('PASS real encoder probe, explicit backend selection, and bounded CLI configuration', json.dumps(probe))

asyncio.run(main())
