namespace XemuTestRunner.Networking;

internal static class InputPacingSettingsPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Input Pacing Settings</title>
<style>
:root{color-scheme:dark}*{box-sizing:border-box}body{margin:0;background:#101216;color:#e7ebf0;font:14px/1.45 system-ui}header{display:flex;justify-content:space-between;gap:16px;align-items:center;padding:18px 24px;border-bottom:1px solid #313945}h1{font-size:20px;margin:0}a{color:#62a8ff;text-decoration:none}.wrap{max-width:1050px;margin:auto;padding:20px}.panel{background:#171a20;border:1px solid #313945;border-radius:7px;padding:16px;margin-bottom:14px}.toggle{display:flex;align-items:center;gap:10px;font-weight:600}.grid{display:grid;grid-template-columns:minmax(130px,1.4fr) repeat(4,minmax(105px,1fr));gap:8px;align-items:center}.grid .heading{color:#a2acba;font-size:12px}.grid input{width:100%}input,button{background:#232a35;border:1px solid #47556a;color:inherit;border-radius:5px;padding:8px}button{cursor:pointer}button:hover{border-color:#62a8ff}.actions{display:flex;gap:10px;align-items:center;flex-wrap:wrap}.muted{color:#a2acba}.warning{color:#eac360}.error{color:#ef8b8b;white-space:pre-wrap}.status{padding:5px 9px;border:1px solid #43536b;border-radius:5px;text-transform:uppercase}@media(max-width:760px){.grid{grid-template-columns:1fr 1fr}.grid .heading:first-child{grid-column:1/-1}.row-name{grid-column:1/-1}}
</style>
</head>
<body>
<header>
<h1>Input Pacing Settings</h1>
<nav><a href="/">Home</a> · <a href="/tests">Tests</a> · <a href="/api/v1/settings/input-pacing">JSON</a></nav>
</header>
<main class="wrap">
<div id="error" class="error" role="alert"></div>
<section class="panel">
<label class="toggle"><input id="input-pacing-enabled" type="checkbox"> Enable slow-rig input pacing</label>
<p class="muted">Hybrid pacing waits for both elapsed time and observed target frames. This runner currently reports whether a frame provider is available; time-only fallback is used only when explicitly enabled.</p>
<label class="toggle"><input id="allow-fallback" type="checkbox"> Allow configured time-only fallback when frame pacing is unavailable</label>
<p>Effective mode: <span id="effective-mode" class="status">Loading</span></p>
<p id="capability" class="muted"></p>
</section>
<section class="panel">
<h2>Intervals</h2>
<div class="grid">
<div class="heading">Interval</div><div class="heading">Minimum ms</div><div class="heading">Minimum frames</div><div class="heading">Fallback ms</div><div class="heading">Maximum ms</div>
<div class="row-name">Preparation</div><input id="preparation-minimumMs" type="number" min="0"><input id="preparation-minimumFrames" type="number" min="0"><input id="preparation-fallbackMs" type="number" min="0"><input id="preparation-maximumMs" type="number" min="1">
<div class="row-name">Held button</div><input id="hold-minimumMs" type="number" min="0"><input id="hold-minimumFrames" type="number" min="0"><input id="hold-fallbackMs" type="number" min="0"><input id="hold-maximumMs" type="number" min="1">
<div class="row-name">Released / neutral</div><input id="neutral-minimumMs" type="number" min="0"><input id="neutral-minimumFrames" type="number" min="0"><input id="neutral-fallbackMs" type="number" min="0"><input id="neutral-maximumMs" type="number" min="1">
</div>
<p class="muted">Time and frame minimums start together and both must be satisfied in hybrid mode. Maximum values are action ceilings, not compulsory waits.</p>
</section>
<section class="panel actions">
<button id="save" type="button">Save settings</button>
<button id="reload" type="button">Reload</button>
<span id="saved" class="muted"></span>
</section>
</main>
<script>
const endpoint='/api/v1/settings/input-pacing';
let revision=0;
const $=id=>document.getElementById(id);
const names=['preparation','hold','neutral'];
async function request(url,options){const response=await fetch(url,{cache:'no-store',...options});const text=await response.text();let body={};try{body=text?JSON.parse(text):{}}catch{body={message:text}}if(!response.ok){const error=body.error||body;throw new Error(error.message||text||response.statusText)}return body}
function interval(name){return{minimumMs:Number($(name+'-minimumMs').value),minimumFrames:Number($(name+'-minimumFrames').value),fallbackMs:Number($(name+'-fallbackMs').value),maximumMs:Number($(name+'-maximumMs').value)}}
function populateInterval(name,value){$(''+name+'-minimumMs').value=value.minimumMs;$(''+name+'-minimumFrames').value=value.minimumFrames;$(''+name+'-fallbackMs').value=value.fallbackMs;$(''+name+'-maximumMs').value=value.maximumMs}
function populate(view){revision=view.revision;const settings=view.settings;$('input-pacing-enabled').checked=settings.enabled;$('allow-fallback').checked=settings.allowTimeOnlyFallback;for(const name of names)populateInterval(name,settings[name]);$('effective-mode').textContent=view.effectiveMode;$('capability').textContent=view.capabilities.framePacingAvailable?'Frame pacing available via '+view.capabilities.frameProvider:'Frame pacing unavailable on this runner; enabled settings resolve to the configured fallback or unavailable mode.';$('saved').textContent='Revision '+view.revision+(view.updatedUtc?' · updated '+view.updatedUtc:'')}
async function load(){try{populate(await request(endpoint));$('error').textContent=''}catch(error){$('error').textContent=error.message}}
async function save(){try{const settings={enabled:$('input-pacing-enabled').checked,allowTimeOnlyFallback:$('allow-fallback').checked,preparation:interval('preparation'),hold:interval('hold'),neutral:interval('neutral')};populate(await request(endpoint,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({expectedRevision:revision,settings})}));$('error').textContent=''}catch(error){$('error').textContent=error.message}}
$('save').addEventListener('click',save);$('reload').addEventListener('click',load);load();
</script>
</body>
</html>
""";
}
