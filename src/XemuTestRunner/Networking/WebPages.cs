using System.Globalization;

namespace XemuTestRunner.Networking;

internal static class WebPages
{
    public static string Home(int refreshMs) => $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Xemu Test Runner</title>
<style>
:root{color-scheme:dark;--bg:#101216;--panel:#171a20;--panel2:#1d2129;--border:#2b313b;--text:#e7ebf0;--muted:#929cab;--accent:#62a8ff;--good:#65d48b;--warn:#e7c45d;--bad:#ef6f6f}
*{box-sizing:border-box}body{margin:0;font:14px/1.45 system-ui,Segoe UI,Arial,sans-serif;background:var(--bg);color:var(--text)}
header{padding:20px 24px;border-bottom:1px solid var(--border);display:flex;justify-content:space-between;align-items:center;background:#13161b}
h1{font-size:20px;margin:0}a{color:var(--accent);text-decoration:none}.wrap{max-width:1250px;margin:auto;padding:22px}
.badge{padding:5px 9px;border:1px solid var(--border);border-radius:6px;background:var(--panel2);font-weight:650;text-transform:uppercase}
.grid{display:grid;grid-template-columns:repeat(4,minmax(150px,1fr));gap:12px;margin:14px 0 22px}.card,.panel{background:var(--panel);border:1px solid var(--border);border-radius:8px}
.card{padding:14px}.card .label{color:var(--muted);font-size:12px}.card .value{font-size:22px;font-weight:650;margin-top:4px}
.panel{padding:16px;margin-bottom:14px}.panel h2{font-size:14px;margin:0 0 12px;color:#fff}
.rows{display:grid;grid-template-columns:150px 1fr;gap:7px 12px}.rows .key{color:var(--muted)}
.links{display:flex;flex-wrap:wrap;gap:9px}.button{display:inline-block;padding:8px 11px;border:1px solid var(--border);border-radius:6px;background:var(--panel2);color:var(--text)}
.button.primary{background:#173354;border-color:#275d95}.queue{display:flex;gap:24px}.queue b{font-size:18px}
.good{color:var(--good)}.warn{color:var(--warn)}.bad{color:var(--bad)}.muted{color:var(--muted)}
@media(max-width:800px){.grid{grid-template-columns:repeat(2,1fr)}.rows{grid-template-columns:110px 1fr}}
</style>
</head>
<body>
<header><h1>Xemu Test Runner</h1><span id="phase" class="badge">STARTING</span></header>
<div class="wrap">
  <div class="links">
    <a class="button primary" href="/control">Test Console</a>
    <a class="button" href="/api/v1/status">Raw Status</a>
    <a class="button" href="/api/v1/metrics/latest">Latest Metrics</a>
    <a class="button" href="/api/v1/queue">Queue JSON</a>
  </div>

  <div class="grid">
    <div class="card"><div class="label">Host CPU</div><div id="hostCpu" class="value">-</div></div>
    <div class="card"><div class="label">Process CPU</div><div id="procCpu" class="value">-</div></div>
    <div class="card"><div class="label">GPU</div><div id="gpu" class="value">-</div></div>
    <div class="card"><div class="label">Process GPU</div><div id="procGpu" class="value">-</div></div>
    <div class="card"><div class="label">Host Memory</div><div id="hostMem" class="value">-</div></div>
    <div class="card"><div class="label">Process Memory</div><div id="procMem" class="value">-</div></div>
    <div class="card"><div class="label">VRAM</div><div id="vram" class="value">-</div></div>
    <div class="card"><div class="label">Process VRAM</div><div id="procVram" class="value">-</div></div>
    <div class="card"><div class="label">Swap / Pagefile</div><div id="swap" class="value">-</div></div>
    <div class="card"><div class="label">GPU Temp / Power</div><div id="gpuCard" class="value">-</div></div>
  </div>

  <div class="panel">
    <h2>Current Test</h2>
    <div class="rows">
      <div class="key">Job</div><div id="job">-</div>
      <div class="key">Run ID</div><div id="runId">-</div>
      <div class="key">PID</div><div id="pid">-</div>
      <div class="key">Job runtime</div><div id="jobRuntime">-</div>
      <div class="key">Disk I/O</div><div id="io">-</div>
      <div class="key">GPU temp/power</div><div id="gpuExtra">-</div>
      <div class="key">Collector</div><div id="collector">-</div>
    </div>
  </div>

  <div class="panel">
    <h2>Queue</h2>
    <div class="queue">
      <span>Pending <b id="pending">0</b></span>
      <span>Testing <b id="testing">0</b></span>
      <span>Tested <b id="tested">0</b></span>
    </div>
  </div>

  <div class="panel">
    <h2>Last Test</h2>
    <div class="rows">
      <div class="key">Job</div><div id="lastJob">-</div>
      <div class="key">Result</div><div id="lastResult">-</div>
      <div class="key">Finished</div><div id="lastFinished">-</div>
      <div class="key">Runner uptime</div><div id="uptime">-</div>
    </div>
  </div>
</div>
<script>
const refreshMs={{refreshMs.ToString(CultureInfo.InvariantCulture)}};
const $=id=>document.getElementById(id);
function pct(v){return v==null?'-':v.toFixed(1)+'%'}
function bytes(v){if(v==null)return '-';const u=['B','KiB','MiB','GiB','TiB'];let i=0,n=Number(v);while(n>=1024&&i<u.length-1){n/=1024;i++}return n.toFixed(i?2:0)+' '+u[i]}
function dur(ms){if(ms==null)return '-';let s=Math.floor(ms/1000),h=Math.floor(s/3600);s%=3600;let m=Math.floor(s/60);s%=60;return [h,m,s].map(x=>String(x).padStart(2,'0')).join(':')}
function setPhase(p){$('phase').textContent=(p||'-').toUpperCase();$('phase').className='badge '+(p==='running'?'good':p==='paused'?'warn':p==='interrupted'?'bad':'')}
async function update(){
 try{
  const r=await fetch('/api/v1/status',{cache:'no-store'}); if(!r.ok)return;
  const s=await r.json(),m=s.LatestMetric||{};
  setPhase(s.Phase);
  $('hostCpu').textContent=pct(m.HostCpuPercent);
  $('procCpu').textContent=m.ProcessCpuPercent==null?'-':m.ProcessCpuPercent.toFixed(1)+'% core';
  $('gpu').textContent=pct(m.GpuUtilizationPercent);
  $('procGpu').textContent=pct(m.ProcessGpuUtilizationPercent);
  $('hostMem').textContent=m.HostMemoryUsedBytes==null?'-':bytes(m.HostMemoryUsedBytes)+' / '+bytes(m.HostMemoryTotalBytes);
  $('procMem').textContent=bytes(m.ProcessWorkingSetBytes);
  $('vram').textContent=m.VramUsedBytes==null?'-':bytes(m.VramUsedBytes)+' / '+bytes(m.VramTotalBytes);
  $('procVram').textContent=bytes(m.ProcessVramBytes);
  $('swap').textContent=m.SwapTotalBytes!=null?bytes(m.SwapUsedBytes)+' / '+bytes(m.SwapTotalBytes):(m.PageFileUsagePercent==null?'-':m.PageFileUsagePercent.toFixed(1)+'%');
  $('gpuCard').textContent=m.GpuTemperatureC==null?'-':m.GpuTemperatureC.toFixed(1)+' C / '+(m.GpuPowerWatts==null?'-':m.GpuPowerWatts.toFixed(1)+' W');
  $('job').textContent=s.CurrentJob||'-'; $('runId').textContent=s.RunId||'-'; $('pid').textContent=s.ProcessId??'-';
  $('jobRuntime').textContent=s.JobStartedUtc?dur(Date.now()-Date.parse(s.JobStartedUtc)):'-';
  $('io').textContent=m.ProcessReadBytesPerSecond==null?'-':'R '+bytes(m.ProcessReadBytesPerSecond)+'/s   W '+bytes(m.ProcessWriteBytesPerSecond)+'/s';
  $('gpuExtra').textContent=m.GpuTemperatureC==null?'-':m.GpuTemperatureC.toFixed(1)+' C   '+(m.GpuPowerWatts==null?'-':m.GpuPowerWatts.toFixed(1)+' W');
  $('collector').textContent=m.CollectorDurationMs==null?'-':m.CollectorDurationMs.toFixed(3)+' ms'+(m.Overrun?' OVERRUN':'');
  $('pending').textContent=s.Queue.Pending; $('testing').textContent=s.Queue.Testing; $('tested').textContent=s.Queue.Tested;
  $('lastJob').textContent=s.LastJob||'-'; $('lastResult').textContent=s.LastResult||'-';
  $('lastFinished').textContent=s.LastFinishedUtc?new Date(s.LastFinishedUtc).toLocaleString():'-';
  $('uptime').textContent=dur(s.UptimeMs??0);
 }catch(e){}
}
update();setInterval(update,refreshMs);
</script>
</body>
</html>
""";

    public static string Control(int refreshMs, int previewIntervalMs, bool previewEnabled) => $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Xemu Test Console</title>
<style>
:root{color-scheme:dark;--bg:#101216;--panel:#171a20;--panel2:#1d2129;--border:#2b313b;--text:#e7ebf0;--muted:#929cab;--accent:#62a8ff;--good:#65d48b;--warn:#e7c45d;--bad:#ef6f6f}
*{box-sizing:border-box}body{margin:0;font:14px/1.4 system-ui,Segoe UI,Arial,sans-serif;background:var(--bg);color:var(--text)}
header{height:58px;padding:0 20px;border-bottom:1px solid var(--border);display:flex;align-items:center;justify-content:space-between;background:#13161b}
a{color:var(--accent);text-decoration:none}.wrap{max-width:1450px;margin:auto;padding:16px}.layout{display:grid;grid-template-columns:minmax(480px,2fr) minmax(330px,1fr);gap:14px}
.panel{background:var(--panel);border:1px solid var(--border);border-radius:8px;padding:14px;margin-bottom:14px}.panel h2{font-size:14px;margin:0 0 10px}
.preview{background:#08090b;border:1px solid var(--border);border-radius:6px;aspect-ratio:4/3;display:flex;align-items:center;justify-content:center;overflow:hidden}
.preview img{width:100%;height:100%;object-fit:contain}.muted{color:var(--muted)}
.controls{display:flex;gap:7px;flex-wrap:wrap}.btn{border:1px solid var(--border);background:var(--panel2);color:var(--text);border-radius:6px;padding:8px 11px;cursor:pointer}.btn:hover{border-color:#536175}.btn.primary{background:#173354;border-color:#275d95}.btn.warn{background:#483d19}.btn.bad{background:#4b2020}
.controller{display:grid;grid-template-columns:repeat(3,64px);gap:6px;justify-content:start}.controller .wide{grid-column:span 3}.action-grid{display:grid;grid-template-columns:repeat(4,1fr);gap:7px;margin-top:10px}
.status-grid{display:grid;grid-template-columns:120px 1fr;gap:6px 10px}.status-grid div:nth-child(odd){color:var(--muted)}
textarea{width:100%;min-height:260px;background:#0d0f13;border:1px solid var(--border);color:#d9e0e8;padding:10px;border-radius:6px;font:12px/1.4 ui-monospace,SFMono-Regular,Consolas,monospace;resize:vertical}
input{background:#0d0f13;border:1px solid var(--border);color:var(--text);border-radius:6px;padding:7px}
.badge{padding:4px 8px;border:1px solid var(--border);border-radius:6px;background:var(--panel2);font-weight:650}.good{color:var(--good)}.warnText{color:var(--warn)}.badText{color:var(--bad)}
.stats{display:grid;grid-template-columns:repeat(4,1fr);gap:7px}.stat{padding:9px;background:var(--panel2);border-radius:6px}.stat small{display:block;color:var(--muted)}.stat strong{font-size:16px}
@media(max-width:950px){.layout{grid-template-columns:1fr}.stats{grid-template-columns:repeat(2,1fr)}}
</style>
</head>
<body>
<header><div><a href="/">Xemu Test Runner</a> / Test Console</div><div><span id="phase" class="badge">STARTING</span></div></header>
<div class="wrap">
<div class="layout">
<section>
  <div class="panel">
    <h2>Live Preview</h2>
    <div class="preview"><img id="preview" alt="xemu preview"><span id="previewText" class="muted">No active preview</span></div>
    <div class="controls" style="margin-top:10px">
      <button class="btn primary" onclick="captureStill()">Save Screenshot</button>
      <button class="btn warn" onclick="pauseXemu()">Pause</button>
      <button class="btn" onclick="resumeXemu()">Resume</button>
      <span id="previewRate" class="muted"></span>
    </div>
  </div>

  <div class="panel">
    <h2>Host / Process</h2>
    <div class="stats">
      <div class="stat"><small>Host CPU</small><strong id="hostCpu">-</strong></div>
      <div class="stat"><small>Process CPU</small><strong id="procCpu">-</strong></div>
      <div class="stat"><small>GPU</small><strong id="gpu">-</strong></div>
      <div class="stat"><small>Process GPU</small><strong id="procGpu">-</strong></div>
      <div class="stat"><small>Host RAM</small><strong id="hostMem">-</strong></div>
      <div class="stat"><small>Process RAM</small><strong id="procMem">-</strong></div>
      <div class="stat"><small>VRAM</small><strong id="vram">-</strong></div>
      <div class="stat"><small>Process VRAM</small><strong id="procVram">-</strong></div>
      <div class="stat"><small>Swap / Pagefile</small><strong id="swap">-</strong></div>
      <div class="stat"><small>Disk I/O</small><strong id="diskIo">-</strong></div>
    </div>
  </div>
</section>

<aside>
  <div class="panel">
    <h2>Current Test</h2>
    <div class="status-grid">
      <div>Job</div><div id="job">-</div>
      <div>Run ID</div><div id="runId">-</div>
      <div>PID</div><div id="pid">-</div>
      <div>Input</div><div id="inputProvider">-</div>
      <div>Recording</div><div id="recordState">off</div>
    </div>
  </div>

  <div class="panel">
    <h2>Xbox Input</h2>
    <div class="controller">
      <span></span><button class="btn" onclick="press('DPadUp')">Up</button><span></span>
      <button class="btn" onclick="press('DPadLeft')">Left</button><button class="btn" onclick="press('DPadDown')">Down</button><button class="btn" onclick="press('DPadRight')">Right</button>
    </div>
    <div class="action-grid">
      <button class="btn" onclick="press('A')">A</button><button class="btn" onclick="press('B')">B</button><button class="btn" onclick="press('X')">X</button><button class="btn" onclick="press('Y')">Y</button>
      <button class="btn" onclick="press('White')">White</button><button class="btn" onclick="press('Black')">Black</button><button class="btn" onclick="press('Back')">Back</button><button class="btn" onclick="press('Start')">Start</button>
      <button class="btn" onclick="press('LTrigger')">L Trigger</button><button class="btn" onclick="press('RTrigger')">R Trigger</button><button class="btn" onclick="press('LStick')">L Stick</button><button class="btn" onclick="press('RStick')">R Stick</button>
    </div>
    <div class="controls" style="margin-top:10px">
      <input id="customButton" placeholder="Button name" value="A">
      <input id="holdMs" type="number" min="1" value="100" style="width:90px">
      <button class="btn" onclick="pressCustom()">Send</button>
    </div>
  </div>

  <div class="panel">
    <h2>Input Recorder</h2>
    <div class="controls">
      <button class="btn primary" onclick="recordStart()">Start Recording</button>
      <button class="btn" onclick="recordStop()">Stop</button>
      <button class="btn" onclick="recordClear()">Clear</button>
      <button class="btn" onclick="copyPlan()">Copy Plan</button>
    </div>
    <p class="muted">Manual button presses and saved screenshots are converted into timed job-plan steps.</p>
    <p id="recordSaved" class="muted"></p>
    <textarea id="plan" spellcheck="false">[]</textarea>
  </div>
</aside>
</div>
</div>
<script>
const refreshMs={{refreshMs.ToString(CultureInfo.InvariantCulture)}};
const previewMs={{previewIntervalMs.ToString(CultureInfo.InvariantCulture)}};
const previewEnabled={{(previewEnabled ? "true" : "false")}};
const $=id=>document.getElementById(id);
let active=false,previewBusy=false;
function pct(v){return v==null?'-':v.toFixed(1)+'%'}
function bytes(v){if(v==null)return '-';const u=['B','KiB','MiB','GiB','TiB'];let i=0,n=Number(v);while(n>=1024&&i<u.length-1){n/=1024;i++}return n.toFixed(i?2:0)+' '+u[i]}
function setPhase(p){$('phase').textContent=(p||'-').toUpperCase();$('phase').className='badge '+(p==='running'?'good':p==='paused'?'warnText':p==='interrupted'?'badText':'')}
async function post(url,obj){const o={method:'POST',headers:{'Content-Type':'application/json'}};if(obj!==undefined)o.body=JSON.stringify(obj);const r=await fetch(url,o);if(!r.ok){throw new Error(await r.text())}return r.headers.get('content-type')?.includes('json')?r.json():null}
async function update(){
 try{
  const [sr,cr]=await Promise.all([fetch('/api/v1/status',{cache:'no-store'}),fetch('/api/v1/control',{cache:'no-store'})]);
  if(sr.ok){const s=await sr.json(),m=s.LatestMetric||{};setPhase(s.Phase);$('job').textContent=s.CurrentJob||'-';$('runId').textContent=s.RunId||'-';$('pid').textContent=s.ProcessId??'-';$('hostCpu').textContent=pct(m.HostCpuPercent);$('procCpu').textContent=m.ProcessCpuPercent==null?'-':m.ProcessCpuPercent.toFixed(1)+'%';$('gpu').textContent=pct(m.GpuUtilizationPercent);$('procGpu').textContent=pct(m.ProcessGpuUtilizationPercent);$('hostMem').textContent=bytes(m.HostMemoryUsedBytes);$('procMem').textContent=bytes(m.ProcessWorkingSetBytes);$('vram').textContent=bytes(m.VramUsedBytes);$('procVram').textContent=bytes(m.ProcessVramBytes);$('swap').textContent=m.SwapTotalBytes!=null?bytes(m.SwapUsedBytes)+' / '+bytes(m.SwapTotalBytes):(m.PageFileUsagePercent==null?'-':m.PageFileUsagePercent.toFixed(1)+'%');$('diskIo').textContent=m.ProcessReadBytesPerSecond==null?'-':'R '+bytes(m.ProcessReadBytesPerSecond)+'/s W '+bytes(m.ProcessWriteBytesPerSecond)+'/s'}
  if(cr.ok){const c=await cr.json();active=c.Active;$('inputProvider').textContent=(c.InputProvider||'-')+(c.InputAvailable?'':' (unavailable)');$('recordState').textContent=c.Recording?'recording ('+c.RecordedSteps+' steps)':'off';if(!active){$('preview').style.display='none';$('previewText').style.display='block'}else{$('previewText').style.display='none';$('preview').style.display='block'}}
 }catch(e){}
}
async function refreshPreview(){
 if(!previewEnabled||!active||document.hidden||previewBusy)return;
 previewBusy=true;
 try{
  const r=await fetch('/api/v1/preview?t='+Date.now(),{cache:'no-store'});
  if(r.ok){const blob=await r.blob(),url=URL.createObjectURL(blob),img=$('preview'),old=img.dataset.url;img.src=url;img.dataset.url=url;if(old)URL.revokeObjectURL(old)}
 }catch(e){}finally{previewBusy=false}
}
async function press(name){try{await post('/api/v1/input/press',{Button:name,DurationMs:Number($('holdMs').value)||100});await loadRecord()}catch(e){alert(e.message)}}
function pressCustom(){press($('customButton').value.trim())}
async function pauseXemu(){try{await post('/api/v1/xemu/pause');await update()}catch(e){alert(e.message)}}
async function resumeXemu(){try{await post('/api/v1/xemu/resume');await update()}catch(e){alert(e.message)}}
async function captureStill(){try{const r=await fetch('/api/v1/screenshot?name=manual-'+Date.now(),{cache:'no-store'});if(!r.ok)throw new Error(await r.text());const blob=await r.blob(),url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download='xemu-screenshot.png';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);await loadRecord()}catch(e){alert(e.message)}}
async function recordStart(){try{const r=await post('/api/v1/input/record/start');showPlan(r)}catch(e){alert(e.message)}}
async function recordStop(){try{const r=await post('/api/v1/input/record/stop');showPlan(r)}catch(e){alert(e.message)}}
async function recordClear(){try{const r=await post('/api/v1/input/record/clear');showPlan(r)}catch(e){alert(e.message)}}
async function loadRecord(){try{const r=await fetch('/api/v1/input/record',{cache:'no-store'});if(r.ok)showPlan(await r.json())}catch(e){}}
function showPlan(r){$('plan').value=JSON.stringify(r.Plan||[],null,2);$('recordState').textContent=r.Recording?'recording ('+(r.Plan?.length||0)+' steps)':'off';$('recordSaved').textContent=r.SavedFile?'Saved: '+r.SavedFile:''}
async function copyPlan(){try{await navigator.clipboard.writeText($('plan').value)}catch(e){$('plan').select();document.execCommand('copy')}}
$('previewRate').textContent=previewEnabled?'Preview interval: '+previewMs+' ms':'Live preview disabled in config';
update();loadRecord();setInterval(update,refreshMs);if(previewEnabled)setInterval(refreshPreview,previewMs);
</script>
</body>
</html>
""";
}
