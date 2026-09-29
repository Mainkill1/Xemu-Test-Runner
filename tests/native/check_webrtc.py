"""Real H.264 encoder -> DTLS/SRTP -> browser decoder qualification.

The signaling bridge is in-process, not a production HTTP/TLS server. No gamepad
injection, xemu correctness, hardware encoding or glass-to-glass claim is implied.
"""
import argparse
import asyncio
from collections import deque
import json
import os
from pathlib import Path
import sys
import time

from playwright.async_api import async_playwright

HTML = r'''<!doctype html><html><body><video id="video" autoplay muted playsinline></video><script>
window.events=[];window.errors=[];window.pendingIce=[];window.channels={};window.receipts=[];
window.receive=async e=>{
  events.push(e);
  if(e.type==='answer'){
    await pc.setRemoteDescription({type:'answer',sdp:e.sdp});
    for(const c of pendingIce)await pc.addIceCandidate(c);
    pendingIce=[];
  }
  if(e.type==='ice'){
    const c={candidate:e.candidate,sdpMLineIndex:e.sdpMLineIndex};
    if(pc.remoteDescription)await pc.addIceCandidate(c);else pendingIce.push(c);
  }
  if(e.type==='error')errors.push(e.error);
};
window.begin=async options=>{
 window.pc=new RTCPeerConnection({iceServers:[]});
 for(const name of ['connectionstatechange','iceconnectionstatechange','signalingstatechange'])
   pc.addEventListener(name,()=>events.push({type:name,state:pc.connectionState,ice:pc.iceConnectionState}));
 const early=[];
 let offerSent=false;
 pc.onicecandidate=e=>{
   if(!e.candidate)return;
   const c={type:'ice',candidate:e.candidate.candidate,sdpMLineIndex:e.candidate.sdpMLineIndex};
   if(options.earlyIce&&!offerSent)early.push(c);else sendWorker(c).catch(e=>errors.push(String(e)));
 };
 pc.ontrack=e=>{
   video.srcObject=new MediaStream([e.track]);
   video.play().catch(e=>errors.push(String(e)));
 };
 const t=pc.addTransceiver('video',{direction:'recvonly'});
 const codecs=RTCRtpReceiver.getCapabilities('video').codecs.filter(c=>options.noH264?
    c.mimeType.toLowerCase()==='video/vp8':
    c.mimeType.toLowerCase()==='video/h264'&&c.sdpFmtpLine?.includes('packetization-mode=1')&&c.sdpFmtpLine?.includes('profile-level-id=42e0'));
 if(!codecs.length)throw Error('Browser has no required decoder. Use installed Chrome, not codec-stripped headless shell.');
 t.setCodecPreferences(codecs);
 channels['input-state']=pc.createDataChannel('input-state',{ordered:false,maxRetransmits:0});
 channels['session-control']=pc.createDataChannel('session-control',{ordered:true});
 if(options.badChannel)channels['unexpected']=pc.createDataChannel('unexpected');
 for(const [label,ch] of Object.entries(channels)){
   ch.onerror=e=>errors.push(label+': '+String(e.error));
   ch.onmessage=e=>receipts.push(JSON.parse(e.data));
 }
 let offer=await pc.createOffer();
 // Payload numbers are negotiated, not a hard-coded encoder constant. Force a
 // different valid dynamic payload in the local description and wire offer.
 if(options.payload){
   const old=offer.sdp.match(/a=rtpmap:(\d+) H264\/90000/)[1];
   let s=offer.sdp.replace(new RegExp('(a=(?:rtpmap|rtcp-fb|fmtp):)'+old+'(?= )','g'),'$1'+options.payload);
   s=s.replace(/m=video ([^\r\n]+)/,(_,rest)=>'m=video '+rest.split(' ').map((x,i)=>i>=2&&x===old?String(options.payload):x).join(' '));
   offer={type:'offer',sdp:s};
 }
 await pc.setLocalDescription(offer);
 if(options.earlyIce){
   await new Promise(resolve=>{
     if(pc.iceGatheringState==='complete')return resolve();
     const timer=setTimeout(resolve,2000);
     pc.addEventListener('icegatheringstatechange',()=>{if(pc.iceGatheringState==='complete'){clearTimeout(timer);resolve();}});
   });
   for(const c of early)await sendWorker(c);
 }
 offerSent=true;
 await sendWorker({type:'offer',sdp:offer.sdp});
};
window.stats=async()=>{
 const all=await pc.getStats();let frames=0,keyframes=0,decodeSeconds=0,jitterSeconds=0,emitted=0;const reports=[];
 for(const r of all.values()){
   if(r.type==='inbound-rtp'&&r.kind==='video'){
     frames+=r.framesDecoded||0;keyframes+=r.keyFramesDecoded||0;
     decodeSeconds+=r.totalDecodeTime||0;jitterSeconds+=r.jitterBufferDelay||0;emitted+=r.jitterBufferEmittedCount||0;
   }
   if(['inbound-rtp','codec','transport','candidate-pair'].includes(r.type))reports.push(r);
 }
 return {frames,keyframes,decodeSeconds,jitterSeconds,emitted,width:video.videoWidth,height:video.videoHeight,
   connection:pc.connectionState,channels:Object.fromEntries(Object.entries(channels).map(([k,v])=>[k,v.readyState])),errors,reports};
};
window.pixels=()=>{
 const c=document.createElement('canvas');c.width=16;c.height=9;const ctx=c.getContext('2d');ctx.drawImage(video,0,0,16,9);
 return Array.from(ctx.getImageData(0,0,16,9).data);
};
</script></body></html>'''

async def qualify(args):
    env=dict(os.environ)
    env.setdefault('GST_DEBUG','webrtc*:3,nice*:3')
    env['GST_DEBUG_NO_COLOR']='1'
    worker=Path(args.worker).resolve()
    window=None
    if args.x11_window:
        from window_fixture import WindowFixture
        window=WindowFixture()
    target=['--window',format(window.window_id,'x'),'--pid',str(os.getpid())] if window else ['--fixture']
    command=[str(worker),*target,'--encoder',args.encoder,'--resolution',args.resolution]
    proc=await asyncio.create_subprocess_exec(*command,env=env,
        stdin=asyncio.subprocess.PIPE,stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE)
    lock=asyncio.Lock()
    received=[]
    native_events=deque(maxlen=80)
    native_errors=deque(maxlen=100)
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
        ready=json.loads(await asyncio.wait_for(proc.stdout.readline(),20))
        assert ready['type']=='ready' and ready['stage']=='signaling-ready' and not ready['browserQualified'],ready
        native_events.append(ready)
        async with async_playwright() as p:
            launch={'headless':True,'args':['--no-sandbox','--autoplay-policy=no-user-gesture-required']}
            if os.environ.get('CHROMIUM_EXECUTABLE'):
                launch['executable_path']=os.environ['CHROMIUM_EXECUTABLE']
            else:
                launch['channel']='chrome'
            browser=await p.chromium.launch(**launch)
            page=await browser.new_page()
            await page.expose_function('sendWorker',send)
            # Embed the fixture directly. Media and both data channels still use
            # real native ICE/DTLS/SRTP/SCTP; no browser HTTP server is needed.
            await page.set_content(HTML)
            async def pump():
                try:
                    while line:=await proc.stdout.readline():
                        event=json.loads(line)
                        native_events.append(event)
                        if event['type']=='data':
                            value=json.loads(event['data'])
                            received.append((event['channel'],value))
                            await send({'type':'send','data':json.dumps({'echoSequence':value['sequence'],'channel':event['channel']})})
                        elif event['type']!='health':
                            await page.evaluate('e=>receive(e)',event)
                except Exception as e:
                    native_errors.append('SIGNALING PUMP: '+repr(e))
                    raise
            task=asyncio.create_task(pump())
            try:
                t0=time.monotonic()
                await page.evaluate('begin',{'payload':args.payload,'earlyIce':args.early_ice,'noH264':args.no_h264,'badChannel':args.bad_channel})
                if args.no_h264 or args.bad_channel:
                    await page.wait_for_function('errors.length>0',timeout=15000)
                    await asyncio.wait_for(proc.wait(),5)
                    assert proc.returncode!=0
                    detail=await page.evaluate('errors')
                    expected='H.264' if args.no_h264 else 'data channel'
                    assert any(expected in x for x in detail),detail
                    print('PASS incompatible peer is refused',json.dumps(detail))
                    return
                await page.wait_for_function("errors.length || (channels['session-control'].readyState==='open' && channels['input-state'].readyState==='open' && video.videoWidth>0)",timeout=15000)
                assert not await page.evaluate('errors'),await page.evaluate('errors')
                first_frame_ms=(time.monotonic()-t0)*1000
                width,height=map(int,args.resolution.split('x'))
                metrics=await page.evaluate('stats()')
                assert (metrics['width'],metrics['height'])==(width,height),metrics
                before_pixels=await page.evaluate('pixels()')
                if window:
                    assert ready['sourceWidth']==640 and ready['sourceHeight']==480,ready
                    # 4:3 source is letterboxed, not stretched or replaced with a desktop.
                    assert max(before_pixels[4*(4*16):4*(4*16)+3])<24,before_pixels
                    center=before_pixels[4*(4*16+8):4*(4*16+8)+3]
                    assert 15<center[0]<50 and 60<center[1]<105 and 105<center[2]<155,center
                    window.resize()
                    await page.wait_for_function("events.some(e=>e.type==='surface-changed' && e.sourceWidth===800 && e.sourceHeight===450 && e.surfaceVersion>1)",timeout=5000)
                    from latency_probe import measure
                    print('PASS decoded visual latency probe',json.dumps(await measure(page,window)))
                echo_ms=[]
                for sequence,channel in enumerate(['input-state','session-control'],1):
                    state={'sequence':sequence,'browserTimestampUs':100*sequence,'controllerIndex':0,
                        'state':{'buttons':12288,'leftTrigger':23,'rightTrigger':254,'leftX':-32768,'leftY':32767,'rightX':12345,'rightY':-5432}}
                    started=time.monotonic()
                    await page.evaluate('([c,v])=>channels[c].send(JSON.stringify(v))',[channel,state])
                    await page.wait_for_function('n=>receipts.some(r=>r.echoSequence===n)',arg=sequence,timeout=5000)
                    echo_ms.append((time.monotonic()-started)*1000)
                    assert received[-1]==(channel,state),received
                await send({'type':'bitrate','bitrateKbps':3000})
                await send({'type':'keyframe'})
                await page.wait_for_function("events.some(e=>e.type==='encoder-config') && events.some(e=>e.type==='keyframe-requested')",timeout=5000)
                before=await page.evaluate('stats()')
                started=time.monotonic()
                await asyncio.sleep(5)
                after=await page.evaluate('stats()')
                seconds=time.monotonic()-started
                fps=(after['frames']-before['frames'])/seconds
                assert fps>=55,{'fps':fps,'seconds':seconds,'before':before,'after':after}
                assert after['keyframes']>before['keyframes']
                assert await page.evaluate('pixels()')!=before_pixels,'Decoded video never changed.'
                used=[r for r in after['reports'] if r['type']=='codec' and r['mimeType'].lower()=='video/h264']
                assert used and (not args.payload or used[0]['payloadType']==args.payload),used
                health=[e for e in native_events if e['type']=='health']
                assert health and health[-1]['encodedBytes']>0 and health[-1]['targetBitrateKbps']==3000,health
                assert not after['errors'],after['errors']
                summary={'browser':browser.version,'encoder':ready['encoder'],'resolution':args.resolution,
                    'payloadType':used[0]['payloadType'],'decodedFrames':after['frames'],'measuredFps':round(fps,2),
                    'firstFrameAfterOfferMs':round(first_frame_ms,2),'dataEchoRoundTripMs':[round(x,2) for x in echo_ms],
                    'meanDecodeMs':round((after['decodeSeconds']-before['decodeSeconds'])*1000/max(after['frames']-before['frames'],1),3),
                    'meanJitterBufferMs':round((after['jitterSeconds']-before['jitterSeconds'])*1000/max(after['emitted']-before['emitted'],1),3),
                    'hostEncodeFps':round(health[-1]['encodeFps'],2),'glassToGlassMeasured':False,'source':'owned-x11-window' if window else 'synthetic'}
                print('PASS real H.264 video, both input channels, bitrate update, keyframes and sustained 60 FPS target',json.dumps(summary))
                if args.peer_close:
                    await page.evaluate('pc.close()')
                    await asyncio.wait_for(proc.wait(),5)
                    assert proc.returncode!=0,'Peer loss was reported as a healthy session.'
                    print('PASS peer disconnect explicitly terminates the media session')
                else:
                    await send({'type':'stop'})
                    await asyncio.wait_for(proc.wait(),5)
                    assert proc.returncode==0,tuple(native_errors)
                await task
            except BaseException:
                try:
                    d=await page.evaluate("async()=>({stats:window.pc?await stats():null,local:window.pc?.localDescription?.sdp,remote:window.pc?.remoteDescription?.sdp,events,errors})")
                    print('BROWSER DIAGNOSTICS '+json.dumps(d),file=sys.stderr)
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
        if window:await window.close()

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('worker')
    parser.add_argument('--encoder',default='x264')
    parser.add_argument('--resolution',default='1280x720',choices=['1280x720','1920x1080'])
    parser.add_argument('--payload',type=int,choices=range(96,128))
    parser.add_argument('--early-ice',action='store_true')
    parser.add_argument('--no-h264',action='store_true')
    parser.add_argument('--bad-channel',action='store_true')
    parser.add_argument('--peer-close',action='store_true')
    parser.add_argument('--x11-window',action='store_true')
    asyncio.run(qualify(parser.parse_args()))
