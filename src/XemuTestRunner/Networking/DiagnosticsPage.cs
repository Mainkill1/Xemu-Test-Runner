namespace XemuTestRunner.Networking;

internal static class DiagnosticsPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Xemu Diagnostics</title>
<style>
:root{color-scheme:dark}*{box-sizing:border-box}body{font:14px/1.45 system-ui;background:#101216;color:#e7ebf0;margin:24px}a{color:#62a8ff;text-decoration:none}nav,.actions{display:flex;gap:12px;flex-wrap:wrap}.panel{background:#171a20;border:1px solid #313945;border-radius:7px;padding:14px;margin:14px 0}table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid #313945;padding:8px;text-align:left}button{background:#232a35;border:1px solid #47556a;color:inherit;border-radius:5px;padding:8px;cursor:pointer}button:hover{border-color:#62a8ff}button:disabled{opacity:.5;cursor:not-allowed}.ok{color:#78d797}.bad{color:#ef8b8b}.muted{color:#a2acba}pre{background:#0d1015;border:1px solid #313945;padding:12px;white-space:pre-wrap;overflow:auto;max-height:40vh}#error{color:#ef8b8b;white-space:pre-wrap}
</style>
</head>
<body>
<nav><a href="/">Home</a><a href="/control">Test console</a><a href="/results">Evidence</a><a href="/diagnostics">Diagnostics</a></nav>
<h1>Diagnostics</h1>
<p class="muted">Recipes run against the current xemu process. Diagnostic runs intentionally mark benchmark evidence as operator/tool intervened.</p>
<p><a id="viewRun" href="/results">View saved diagnostic files</a> — inspect images, CSV, JSON, and logs in the app; downloading the original is optional.</p>
<div id="error" role="status"></div>
<div class="panel"><h2>Tool readiness</h2><table><thead><tr><th>Tool</th><th>Status</th><th>Resolved path</th><th>Detail</th></tr></thead><tbody id="tools"></tbody></table></div>
<div class="panel"><h2>Configured recipes</h2><table><thead><tr><th>ID</th><th>Type</th><th>Duration</th><th>Action</th></tr></thead><tbody id="recipes"></tbody></table></div>
<div class="panel"><h2>Diagnostic state</h2><pre id="state">Loading...</pre></div>
<script>
const $=id=>document.getElementById(id);
async function api(url,options){const r=await fetch(url,{cache:'no-store',...options});if(!r.ok)throw new Error(await r.text());return r.json()}
async function load(){
 try{
  const [tools,recipes,state]=await Promise.all([api('/api/v1/diagnostics/tools'),api('/api/v1/diagnostics/recipes'),api('/api/v1/diagnostics')]);
  $('tools').replaceChildren(...tools.map(t=>{const tr=document.createElement('tr');for(const v of [t.Name,t.Available?'Ready':'Unavailable',t.ResolvedPath||'-',t.Detail]){const td=document.createElement('td');td.textContent=v;tr.append(td)}tr.children[1].className=t.Available?'ok':'bad';return tr}));
  $('recipes').replaceChildren(...recipes.map(r=>{const tr=document.createElement('tr');for(const v of [r.Id,r.Type,Number(r.DurationMs).toFixed(2)+' ms']){const td=document.createElement('td');td.textContent=v;tr.append(td)}const td=document.createElement('td'),b=document.createElement('button');b.textContent='Run';b.disabled=!state.Active||!!state.CurrentDiagnostic;b.onclick=()=>run(r.Id);td.append(b);tr.append(td);return tr}));
  $('viewRun').href=state.RunId?'/results#'+encodeURIComponent(state.RunId):'/results';
  $('state').textContent=JSON.stringify(state,null,2);$('error').textContent='';
 }catch(e){$('error').textContent=e.message}
}
async function run(id){
 try{$('error').textContent='Running '+id+'...';await api('/api/v1/diagnostics/run',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({Id:id})});await load()}
 catch(e){$('error').textContent=e.message}
}
load();setInterval(load,1500);
</script>
</body>
</html>
""";
}
