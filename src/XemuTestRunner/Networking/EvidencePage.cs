namespace XemuTestRunner.Networking;

internal static class EvidencePage
{
    public const string Html = """
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Test evidence</title>
<style>
body{font:14px system-ui;background:#101216;color:#e7ebf0;margin:24px}a{color:#62a8ff}button,select{background:#242a34;color:inherit;padding:7px;border:1px solid #485466;border-radius:5px}table{border-collapse:collapse;width:100%;margin:16px 0}td,th{border-bottom:1px solid #323a46;padding:8px;text-align:left}pre{background:#181d25;border:1px solid #323a46;padding:14px;white-space:pre-wrap;max-height:50vh;overflow:auto}nav{display:flex;gap:20px}#artifacts{display:flex;gap:12px;flex-wrap:wrap}p{color:#a3aebd}
</style>
<nav><a href="/">Home</a><a href="/control">Test console</a><a href="/api/v1/runs">Run JSON</a></nav>
<h1>Test evidence</h1><p>Recent attempts, exact build identity, intervention flags, and bounded log tails. A zero process exit is not a hardware-correctness assertion.</p>
<button id="refresh">Refresh runs</button><span id="error" role="status"></span>
<table><thead><tr><th>Run</th><th>Job</th><th>Outcome</th><th>Comparison</th></tr></thead><tbody id="runs"></tbody></table>
<h2 id="selected">Select a run</h2><div id="artifacts"></div>
<p><select id="log"><option>stdout.log</option><option>stderr.log</option><option>operator-events.jsonl</option></select> <button id="tail">Read tail</button> <label><input type="checkbox" id="follow"> Follow (1 second)</label></p>
<pre id="text">No run selected.</pre>
<script>
const $=id=>document.getElementById(id);let run=null,busy=false;
async function json(url){const r=await fetch(url,{cache:'no-store'});if(!r.ok)throw new Error(await r.text());return r.json()}
async function list(){try{const rows=await json('/api/v1/runs');$('runs').replaceChildren();for(const row of rows){const tr=document.createElement('tr'),r=row.Result||{};for(const value of [row.RunId,r.job||r.jobPackage||'-',r.status||'active/incomplete',r.comparisonStatus||'not evaluated']){const td=document.createElement('td');td.textContent=value;tr.append(td)}tr.firstChild.style.cursor='pointer';tr.firstChild.onclick=()=>select(row.RunId);$('runs').append(tr)}$('error').textContent=''}catch(e){$('error').textContent=e.message}}
async function select(id){try{run=id;$('selected').textContent=id;const data=await json('/api/v1/runs/'+encodeURIComponent(id));$('artifacts').replaceChildren();for(const f of data.Artifacts){const a=document.createElement('a');a.textContent=f.Path+' ('+f.Bytes+' B)';a.href='/api/v1/runs/'+encodeURIComponent(id)+'/artifacts/'+f.Path.split('/').map(encodeURIComponent).join('/');$('artifacts').append(a)}await tail()}catch(e){$('error').textContent=e.message}}
async function tail(){if(!run||busy||document.hidden)return;busy=true;const id=run;try{const data=await json('/api/v1/runs/'+encodeURIComponent(id)+'/tail?file='+encodeURIComponent($('log').value)+'&bytes=32768');if(id===run)$('text').textContent='Bytes '+data.Offset+'..'+(data.Offset+data.Bytes)+' / '+data.FileBytes+'\n\n'+data.Text}catch(e){$('text').textContent=e.message}finally{busy=false}}
$('refresh').onclick=list;$('tail').onclick=tail;$('log').onchange=tail;setInterval(()=>{if($('follow').checked)tail()},1000);list();
</script></html>
""";
}
