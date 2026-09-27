"""One-clock upper bound from source paint submission to decoded observation.

A frame barcode crosses the actual capture/encode/SRTP/decode pipeline. The
return trip through Playwright is included; this is not physical glass latency.
"""
import asyncio
import time

READ_BARCODE = r'''([width,height])=>{
 const c=document.createElement('canvas');c.width=video.videoWidth;c.height=video.videoHeight;
 const ctx=c.getContext('2d',{willReadFrequently:true});ctx.drawImage(video,0,0);
 const scale=Math.min(c.width/width,c.height/height),ox=(c.width-width*scale)/2,oy=(c.height-height*scale)/2;
 let code=0,inverse=0;
 for(let i=0;i<32;i++){
   const p=ctx.getImageData(Math.floor(ox+(25+i*10)*scale),Math.floor(oy+30*scale),1,1).data;
   const bit=(p[0]+p[1]+p[2])/3>127?1:0;
   if(i<16)code|=bit<<i;else inverse|=bit<<(i-16);
 }
 return (code^inverse)===65535?code:null;
}'''

async def measure(page, window):
    samples=[]
    seen=set()
    for _ in range(80):
        frame=await page.evaluate(READ_BARCODE,[800,450])
        observed=time.monotonic()
        submitted=getattr(window,'paint_times',{}).get(frame)
        if submitted is not None and frame not in seen:
            samples.append((observed-submitted)*1000)
            seen.add(frame)
        await asyncio.sleep(0.02)
    assert len(samples)>=30,{'error':'Too few distinct decoded frame barcodes','samples':len(samples)}
    samples.sort()
    p95=samples[int((len(samples)-1)*0.95)]
    assert 0<=p95<150,{'paintToDecodedObservationP95Ms':p95,'samples':len(samples)}
    return {'samples':len(samples),'paintToDecodedObservationMedianMs':round(samples[len(samples)//2],2),
        'paintToDecodedObservationP95Ms':round(p95,2),'includesObservationReturnTrip':True,
        'physicalGlassToGlassMeasured':False}
