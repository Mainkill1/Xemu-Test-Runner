namespace XemuTestRunner.Networking;

internal static class EvidencePage
{
    public const string Html = """
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Test evidence</title>
<style>
body{font:14px system-ui;background:#101216;color:#e7ebf0;margin:24px}a{color:#62a8ff}button,select,input{background:#242a34;color:inherit;padding:7px;border:1px solid #485466;border-radius:5px}table{border-collapse:collapse;width:100%;margin:16px 0}td,th{border-bottom:1px solid #323a46;padding:8px;text-align:left;vertical-align:top}pre{background:#181d25;border:1px solid #323a46;padding:14px;white-space:pre-wrap;max-height:50vh;overflow:auto}nav{display:flex;gap:20px}#artifacts{display:flex;gap:12px;flex-wrap:wrap}.muted,p{color:#a3aebd}.bad{color:#ef8b8b}.good{color:#78d797}.warn{color:#eac360}.summary{display:grid;grid-template-columns:160px 1fr;gap:7px;background:#171a20;border:1px solid #323a46;border-radius:6px;padding:12px;margin:14px 0}
</style>
<nav><a href="/">Home</a><a href="/control">Test console</a><a href="/diagnostics">Diagnostics</a><a href="/api/v1/runs">Run JSON</a></nav>
<h1>Test evidence</h1>
<p>Execution, guest correctness, evidence completeness, and comparison eligibility are separate outcomes. A zero process exit is not a correctness assertion.</p>
<button id="refresh">Refresh runs</button><span id="error" role="status"></span>
<table><thead><tr><th>Run</th><th>Job / variant</th><th>Execution</th><th>Correctness</th><th>Evidence</th><th>Comparison</th></tr></thead><tbody id="runs"></tbody></table>
<h2 id="selected">Select a run</h2>
<div id="summary" class="summary" hidden></div>
<div id="artifacts"></div>
<p><select id="log"><option>stdout.log</option><option>stderr.log</option><option>operator-events.jsonl</option><option>segments.jsonl</option></select> <button id="tail">Read tail</button> <label><input type="checkbox" id="follow"> Follow (1 second)</label></p>
<pre id="text">No run selected.</pre>
<script>
const $=id=>document.getElementById(id);let run=null,busy=false;
async function json(url){const r=await fetch(url,{cache:'no-store'});if(!r.ok)throw new Error(await r.text());return r.json()}
function outcome(r,name,fallback){return r.assessment?.[name]??fallback??'-'}
function cell(tr,value,cls){const td=document.createElement('td');td.textContent=value??'-';if(cls)td.className=cls;tr.append(td);return td}
function cls(v){v=String(v||'').toLowerCase();return ['completed','passed','complete','eligible'].includes(v)?'good':['failed','invalid','incomplete','ineligible','cleanupfailure','runnerfailure'].some(x=>v.includes(x))?'bad':'warn'}
async function list(){try{const rows=await json('/api/v1/runs');$('runs').replaceChildren();for(const row of rows){const tr=document.createElement('tr'),r=row.Result||{},variant=r.experiment?.Variant||'';const first=cell(tr,row.RunId);first.style.cursor='pointer';first.onclick=()=>select(row.RunId);cell(tr,(r.job||r.jobPackage||'-')+(variant?' / '+variant:''));for(const [name,fallback] of [['Execution',r.status],['Correctness',r.correctnessStatus],['Evidence',r.evidenceStatus],['Comparison',r.comparisonStatus]]){const v=outcome(r,name,fallback);cell(tr,v,cls(v))}$('runs').append(tr)}$('error').textContent=''}catch(e){$('error').textContent=e.message}}
function addSummary(label,value){const l=document.createElement('span'),v=document.createElement('span');l.className='muted';l.textContent=label;v.textContent=value??'-';$('summary').append(l,v)}
async function select(id){try{run=id;$('selected').textContent=id;const data=await json('/api/v1/runs/'+encodeURIComponent(id)),r=data.Result||{};$('summary').replaceChildren();$('summary').hidden=false;addSummary('Execution',outcome(r,'Execution',r.status));addSummary('Correctness',outcome(r,'Correctness',r.correctnessStatus));addSummary('Evidence',outcome(r,'Evidence',r.evidenceStatus));addSummary('Comparison',outcome(r,'Comparison',r.comparisonStatus));addSummary('Experiment',r.experiment?.Id||'-');addSummary('Variant',r.experiment?.Variant||'-');addSummary('Comparison reasons',(r.assessment?.ComparisonReasons||[]).join('; ')||'-');addSummary('Measurements',(r.workload?.Measurements||[]).map(x=>x.Name+'='+x.Value+(x.Unit?' '+x.Unit:'')).join(', ')||'-');$('artifacts').replaceChildren();for(const f of data.Artifacts){const a=document.createElement('a');a.textContent=f.Path+' ('+f.Bytes+' B)';a.href='/api/v1/runs/'+encodeURIComponent(id)+'/artifacts/'+f.Path.split('/').map(encodeURIComponent).join('/');$('artifacts').append(a)}await tail()}catch(e){$('error').textContent=e.message}}
async function tail(){if(!run||busy||document.hidden)return;busy=true;const id=run;try{const data=await json('/api/v1/runs/'+encodeURIComponent(id)+'/tail?file='+encodeURIComponent($('log').value)+'&bytes=32768');if(id===run)$('text').textContent='Bytes '+data.Offset+'..'+(data.Offset+data.Bytes)+' / '+data.FileBytes+'\n\n'+data.Text}catch(e){$('text').textContent=e.message}finally{busy=false}}
$('refresh').onclick=list;$('tail').onclick=tail;$('log').onchange=tail;setInterval(()=>{if($('follow').checked)tail()},1000);list();
</script></html>
""";
}
