"""Real native H.264/WebRTC -> Chromium decoder and data-channel integration.
Uses a synthetic native video source; this is not a claim about xemu GPU latency.
"""
import asyncio
import json
import os
import sys
from aiohttp import web
from playwright.async_api import async_playwright

HTML = r'''<!doctype html><html><body><video id="video" autoplay muted playsinline></video><script>
window.events=[];window.errors=[];window.ice=[];window.channels={};window.receipt=null;
window.receive=async e=>{
  events.push(e.type);
  if(e.type==='answer'){await pc.setRemoteDescription({type:'answer',sdp:e.sdp});for(const c of ice)await pc.addIceCandidate(c);ice=[];}
  if(e.type==='ice'){const c={candidate:e.candidate,sdpMLineIndex:e.sdpMLineIndex};if(pc.remoteDescription)await pc.addIceCandidate(c);else ice.push(c);}
  if(e.type==='error')errors.push(e.error);
};
window.begin=async()=>{
 window.pc=new RTCPeerConnection({iceServers:[]});
 pc.onicecandidate=e=>{if(e.candidate)sendWorker({type:'ice',candidate:e.candidate.candidate,sdpMLineIndex:e.candidate.sdpMLineIndex});};
 pc.ontrack=e=>{video.srcObject=new MediaStream([e.track]);};
 const t=pc.addTransceiver('video',{direction:'recvonly'});
 const codecs=RTCRtpReceiver.getCapabilities('video').codecs.filter(c=>c.mimeType.toLowerCase()==='video/h264'&&c.sdpFmtpLine?.includes('packetization-mode=1'));
 t.setCodecPreferences(codecs);
 channels['input-state']=pc.createDataChannel('input-state',{ordered:false,maxRetransmits:0});
 channels['session-control']=pc.createDataChannel('session-control',{ordered:true});
 channels['session-control'].onmessage=e=>{receipt=JSON.parse(e.data);};
 const offer=await pc.createOffer();await pc.setLocalDescription(offer);await sendWorker({type:'offer',sdp:offer.sdp});
};
window.stats=async()=>{let frames=0;for(const r of (await pc.getStats()).values())if(r.type==='inbound-rtp'&&r.kind==='video')frames+=r.framesDecoded||0;return {frames,width:video.videoWidth,height:video.videoHeight,connection:pc.connectionState,errors};};
</script></body></html>'''

async def main():
    app=web.Application()
    app.router.add_get('/', lambda _: web.Response(text=HTML,content_type='text/html'))
    runner=web.AppRunner(app);await runner.setup()
    site=web.TCPSite(runner,'127.0.0.1',0);await site.start()
    port=site._server.sockets[0].getsockname()[1]
    proc=await asyncio.create_subprocess_exec(sys.argv[1],'--fixture',stdin=asyncio.subprocess.PIPE,stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE)
    lock=asyncio.Lock(); received=[]
    async def send(value):
        async with lock:
            proc.stdin.write((json.dumps(value)+'\n').encode());await proc.stdin.drain()
    async def errors():
        lines=[]
        while (line:=await proc.stderr.readline()): lines.append(line.decode(errors='replace'))
        return ''.join(lines)[-8192:]
    stderr=asyncio.create_task(errors())
    try:
        ready=json.loads(await asyncio.wait_for(proc.stdout.readline(),10)); assert ready['type']=='ready',ready
        async with async_playwright() as p:
            browser=await p.chromium.launch(executable_path=os.environ.get('CHROMIUM_EXECUTABLE'),headless=True,args=['--no-sandbox','--autoplay-policy=no-user-gesture-required'])
            page=await browser.new_page()
            await page.expose_function('sendWorker',send)
            await page.goto(f'http://127.0.0.1:{port}/')
            async def pump():
                while (line:=await proc.stdout.readline()):
                    event=json.loads(line)
                    if event['type']=='data':
                        received.append(json.loads(event['data']))
                        await send({'type':'send','data':json.dumps({'ack':received[-1]['sequence']})})
                    elif event['type']!='health': await page.evaluate('e=>receive(e)',event)
            task=asyncio.create_task(pump())
            await page.evaluate('begin()')
            await page.wait_for_function("channels['session-control'].readyState==='open' && video.videoWidth===1280",timeout=15000)
            state={'sequence':1,'browserTimestampUs':100,'controllerIndex':0,'state':{'buttons':12288,'leftTrigger':23,'rightTrigger':254,'leftX':-32768,'leftY':32767,'rightX':12345,'rightY':-5432}}
            await page.evaluate("v=>channels['session-control'].send(JSON.stringify(v))",state)
            await page.wait_for_function('receipt?.ack===1',timeout=5000)
            await asyncio.sleep(2)
            metrics=await page.evaluate('stats()');assert metrics['frames']>=30 and metrics['height']==720 and not metrics['errors'],metrics
            assert received==[state],received
            print('PASS real H.264 WebRTC browser decode and complete controller data',json.dumps(metrics))
            await send({'type':'stop'});await asyncio.wait_for(proc.wait(),5)
            await task;await browser.close()
            assert proc.returncode==0,await stderr
    finally:
        if proc.returncode is None: proc.kill();await proc.wait()
        details=await stderr
        if proc.returncode: print(details,file=sys.stderr)
        await runner.cleanup()

asyncio.run(main())
