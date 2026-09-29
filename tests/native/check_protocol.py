"""Native worker malformed-input, resource-bound and shutdown regression checks."""
import asyncio
import json
import sys
from pathlib import Path

async def session(worker, commands, *options, expect_error=True):
    proc=await asyncio.create_subprocess_exec(str(worker),'--fixture','--encoder','x264',*options,
        stdin=asyncio.subprocess.PIPE,stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE)
    try:
        ready=json.loads(await asyncio.wait_for(proc.stdout.readline(),10))
        assert ready['stage']=='signaling-ready' and not ready['browserQualified'],ready
        if commands is not None:
            for value in commands:
                proc.stdin.write((json.dumps(value)+'\n').encode())
            await proc.stdin.drain()
        events=[]
        while proc.returncode is None:
            line=await asyncio.wait_for(proc.stdout.readline(),5)
            if not line:break
            event=json.loads(line);events.append(event)
            if event['type']=='health' and commands is None:
                assert event['encodedFrames']==0 and event['capturedFrames']==0,event
                proc.stdin.write(b'{"type":"stop"}\n');await proc.stdin.drain()
        await asyncio.wait_for(proc.wait(),5)
        detail=(await proc.stderr.read()).decode(errors='replace')
        assert proc.returncode==(3 if expect_error else 0),(proc.returncode,events,detail)
        if expect_error:assert any(x['type']=='error' for x in events),(events,detail)
    finally:
        if proc.returncode is None:
            proc.kill();await proc.wait()


async def overload(worker):
    # Deliberately throttle the supervisor's output reader. Message count alone
    # is not overload: under instrumentation, an unthrottled consumer may keep up.
    proc=await asyncio.create_subprocess_exec(str(worker),'--fixture','--encoder','x264',
        stdin=asyncio.subprocess.PIPE,stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE,limit=4096)
    try:
        ready=json.loads(await asyncio.wait_for(proc.stdout.readline(),10))
        assert ready['stage']=='signaling-ready',ready
        line=(json.dumps({'type':'bitrate','bitrateKbps':3000})+'\n').encode()
        proc.stdin.write(line*20000+b'{"type":"stop"}\n')
        events=[]
        async def slow_reader():
            while data:=await proc.stdout.readline():
                value=json.loads(data);events.append(value)
                if value['type']=='error':break
                await asyncio.sleep(0.02)
        await asyncio.wait_for(slow_reader(),20)
        out,err=await asyncio.wait_for(proc.communicate(),5)
        events.extend(json.loads(x) for x in out.splitlines())
        assert proc.returncode==3 and any('command queue' in e.get('error','') for e in events),(proc.returncode,events[-4:],err[-512:])
    finally:
        if proc.returncode is None:
            proc.kill();await proc.communicate()


async def main():
    worker=Path(sys.argv[1]).resolve()
    await session(worker,None,expect_error=False)
    print('PASS signaling readiness does not run capture or encoding')
    await session(worker,[], '--handshake-timeout-ms','250')
    print('PASS an absent peer expires without claiming a playable stream')
    for value in [{"type":"offer","sdp":{}},{"type":[]},{"type":"ice","candidate":1,"sdpMLineIndex":0},
                  {"type":"bitrate","bitrateKbps":"3000"},{"type":"send","data":None}]:
        await session(worker,[value])
    print('PASS wrong JSON types fail structurally instead of crashing native code')
    await session(worker,[{'type':'ice','candidate':'candidate:1 1 UDP 1 127.0.0.1 9000 typ host','sdpMLineIndex':0}]*129)
    print('PASS pending ICE is bounded')
    await overload(worker)
    print('PASS command flood fails explicitly instead of building an unbounded queue')

asyncio.run(main())
