"""Black-box checks of the real native worker. No xemu/game qualification is implied."""
import asyncio
import json
import os
import sys
from pathlib import Path

async def main():
    worker = Path(sys.argv[1]).resolve()
    assert worker.is_file(), "The real media worker has not been built."
    proc = await asyncio.create_subprocess_exec(str(worker), "--window", "0", "--pid", str(os.getpid()),
        stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    out, _ = await asyncio.wait_for(proc.communicate(), 5)
    assert proc.returncode != 0, "Desktop/root capture was accepted."
    proc = await asyncio.create_subprocess_exec(str(worker), "--fixture", "--encoder", "x264", stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    try:
        line = await asyncio.wait_for(proc.stdout.readline(), 10)
        event = json.loads(line)
        assert event["type"] == "ready", event
        proc.stdin.write(b'{"type":"stop"}\n')
        await proc.stdin.drain()
        await asyncio.wait_for(proc.wait(), 5)
        assert proc.returncode == 0, "Worker failed graceful shutdown."
    finally:
        if proc.returncode is None: proc.kill(); await proc.wait()
    print("PASS native worker refuses desktop capture and shuts down its real pipeline")

asyncio.run(main())
