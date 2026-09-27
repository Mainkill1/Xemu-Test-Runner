"""Real native H.264/WebRTC -> Chromium decoder and data-channel integration.

Synthetic video qualifies the media path, not xemu or physical display latency.
Failure diagnostics retain both peers' SDP, state changes, RTP stats and native logs.
"""
import asyncio
from collections import deque
import json
import os
import sys

from aiohttp import web
from playwright.async_api import async_playwright

HTML = r'''<!doctype html><html><body><video id="video" autoplay muted playsinline></video><script>
window.events=[];window.errors=[];window.ice=[];window.channels={};window.receipt=null;
window.receive=async e=>{
  events.push(e);
  if(e.type==='answer'){await pc.setRemoteDescription({type:'answer',sdp:e.sdp});for(const c of ice)await pc.addIceCandidate(c);ice=[];}
  if(e.type==='ice'){const c={candidate:e.candidate,sdpMLineIndex:e.sdpMLineIndex};if(pc.remoteDescription)await pc.addIceCandidate(c);else ice.push(c);}
  if(e.type==='error')errors.push(e.error);
};
window.begin=async()=>{
 window.pc=new RTCPeerConnection({iceServers:[]});
 for(const name of ['connectionstatechange','iceconnectionstatechange','icegatheringstatechange','signalingstatechange'])
   pc.addEventListener(name,()=>events.push({type:name,connection:pc.connectionState,ice:pc.iceConnectionState,signaling:pc.signalingState}));
 pc.onicecandidate=e=>{if(e.candidate)sendWorker({type:'ice',candidate:e.candidate.candidate,sdpMLineIndex:e.candidate.sdpMLineIndex}).catch(e=>errors.push(String(e)));};
 pc.ontrack=e=>{video.srcObject=new MediaStream([e.track]);video.play().catch(e=>errors.push(String(e)));};
 const t=pc.addTransceiver('video',{direction:'recvonly'});
 window.codecs=RTCRtpReceiver.getCapabilities('video').codecs.filter(c=>c.mimeType.toLowerCase()==='video/h264'&&c.sdpFmtpLine?.includes('packetization-mode=1'));
 if(!codecs.length)throw Error('Chromium exposes no H.264 packetization-mode=1 decoder');
 t.setCodecPreferences(codecs);
 channels['input-state']=pc.createDataChannel('input-state',{ordered:false,maxRetransmits:0});
 channels['session-control']=pc.createDataChannel('session-control',{ordered:true});
 for(const [label,ch] of Object.entries(channels))ch.onerror=e=>errors.push(label+': '+String(e.error));
 channels['session-control'].onmessage=e=>{receipt=JSON.parse(e.data);};
 const offer=await pc.createOffer();await pc.setLocalDescription(offer);await sendWorker({type:'offer',sdp:offer.sdp});
};
window.stats=async()=>{let frames=0;const reports=[];for(const r of (await pc.getStats()).values()){
 if(r.type==='inbound-rtp'&&r.kind==='video')frames+=r.framesDecoded||0;
 if(['inbound-rtp','codec','transport','candidate-pair'].includes(r.type))reports.push(r);
}return {frames,width:video.videoWidth,height:video.videoHeight,connection:pc.connectionState,ice:pc.iceConnectionState,signaling:pc.signalingState,channels:Object.fromEntries(Object.entries(channels).map(([k,v])=>[k,v.readyState])),codecs,errors,reports};};
</script></body></html>'''

async def main():
    app=web.Application()
    async def index(_):
        return web.Response(text=HTML,content_type='text/html')
    app.router.add_get('/',index)
    runner=web.AppRunner(app)
    await runner.setup()
    site=web.TCPSite(runner,'127.0.0.1',0)
    await site.start()
    port=site._server.sockets[0].getsockname()[1]
    env=dict(os.environ)
    env.setdefault('GST_DEBUG','webrtc*:5,nice*:4')
    env['GST_DEBUG_NO_COLOR']='1'
    proc=await asyncio.create_subprocess_exec(sys.argv[1],'--fixture',env=env,
        stdin=asyncio.subprocess.PIPE,stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE)
    lock=asyncio.Lock()
    received=[]
    native_events=deque(maxlen=64)
    native_errors=deque(maxlen=120)
    task=None
    async def send(value):
        async with lock:
            proc.stdin.write((json.dumps(value)+'\n').encode())
            await proc.stdin.drain()
    async def drain_errors():
        while line:=await proc.stderr.readline():
            native_errors.append(line.decode(errors='replace').rstrip())
    stderr=asyncio.create_task(drain_errors())
    try:
        ready=json.loads(await asyncio.wait_for(proc.stdout.readline(),10))
        assert ready['type']=='ready',ready
        native_events.append(ready)
        async with async_playwright() as p:
            browser=await p.chromium.launch(executable_path=os.environ.get('CHROMIUM_EXECUTABLE'),headless=True,
                args=['--no-sandbox','--autoplay-policy=no-user-gesture-required'])
            page=await browser.new_page()
            await page.expose_function('sendWorker',send)
            await page.goto(f'http://127.0.0.1:{port}/')
            async def pump():
                try:
                    while line:=await proc.stdout.readline():
                        event=json.loads(line)
                        native_events.append(event)
                        if event['type']=='data':
                            received.append(json.loads(event['data']))
                            await send({'type':'send','data':json.dumps({'ack':received[-1]['sequence']})})
                        elif event['type']!='health':
                            await page.evaluate('e=>receive(e)',event)
                except Exception as e:
                    native_errors.append('SIGNALING PUMP: '+repr(e))
                    raise
            task=asyncio.create_task(pump())
            try:
                await page.evaluate('begin()')
                await page.wait_for_function("errors.length || (channels['session-control'].readyState==='open' && video.videoWidth===1280)",timeout=15000)
                assert not await page.evaluate('errors'),await page.evaluate('errors')
                state={'sequence':1,'browserTimestampUs':100,'controllerIndex':0,'state':{'buttons':12288,'leftTrigger':23,'rightTrigger':254,'leftX':-32768,'leftY':32767,'rightX':12345,'rightY':-5432}}
                await page.evaluate("v=>channels['session-control'].send(JSON.stringify(v))",state)
                await page.wait_for_function('receipt?.ack===1',timeout=5000)
                await asyncio.sleep(2)
                metrics=await page.evaluate('stats()')
                assert metrics['frames']>=30 and metrics['height']==720 and not metrics['errors'],metrics
                assert received==[state],received
                print('PASS real H.264 WebRTC browser decode and complete controller data',json.dumps(metrics))
                await send({'type':'stop'})
                await asyncio.wait_for(proc.wait(),5)
                await task
                assert proc.returncode==0,tuple(native_errors)
            except BaseException:
                try:
                    diagnostic=await page.evaluate("async()=>({stats:window.pc?await stats():null,local:window.pc?.localDescription?.sdp,remote:window.pc?.remoteDescription?.sdp,events,errors})")
                    print('BROWSER DIAGNOSTICS '+json.dumps(diagnostic),file=sys.stderr)
                except Exception as e:
                    print('Could not query browser diagnostics: '+repr(e),file=sys.stderr)
                print('WORKER EVENTS '+json.dumps(list(native_events)),file=sys.stderr)
                print('NATIVE STDERR\n'+'\n'.join(native_errors),file=sys.stderr)
                raise
            finally:
                if task and not task.done():
                    task.cancel()
                if task:
                    await asyncio.gather(task,return_exceptions=True)
                await browser.close()
    finally:
        if proc.returncode is None:
            proc.kill()
            await proc.wait()
        await stderr
        await runner.cleanup()

asyncio.run(main())
