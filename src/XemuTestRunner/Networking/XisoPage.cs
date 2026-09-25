namespace XemuTestRunner.Networking;

internal static class XisoPage
{
    public const string Html = """
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>XISO campaigns</title><style>body{font:15px system-ui;margin:24px;max-width:1180px}nav{display:flex;gap:18px}input,button,select{font:inherit;padding:7px}button{cursor:pointer}.cards{display:flex;flex-wrap:wrap;gap:12px;margin:18px 0}.cards label{border:1px solid #bbb;padding:12px;border-radius:6px}table{width:100%;border-collapse:collapse}td,th{text-align:left;padding:8px;border-bottom:1px solid #ddd}pre{white-space:pre-wrap;background:#f3f5f7;padding:15px}#error{color:#a00}small{color:#555}</style>
<nav><a href="/">Runner</a><a href="/tests">Saved configs</a><a href="/xiso">XISO campaigns</a></nav>
<h1>XISO campaigns</h1><p>Select system categories, individual tests, or both. Preparing a campaign does not start it.</p>
<label>Suite <select id="suite"></select></label> <small id="identity"></small><div id="categories" class="cards"></div>
<details><summary>Choose individual tests</summary><table><thead><tr><th>Select</th><th>Stable ID</th><th>Test</th><th>Category</th></tr></thead><tbody id="tests"></tbody></table><button id="more" hidden>More tests</button></details>
<h2>Prepare</h2><p><label>Application upload ID <input id="application" required></label> <label>Campaign ID <input id="id" maxlength="40" required></label></p>
<p><label>Mode <select id="mode"><option value="focused">Focused / smoke default</option><option value="qualification">Shards + final monolithic gate</option><option value="full">One full process</option></select></label> <label>Reference application (optional ABBA) <input id="reference"></label></p>
<button id="prepare">Prepare without starting</button> <button id="start" hidden>Start this campaign</button> <button id="refresh" hidden>Refresh result</button><p id="error" role="alert"></p><pre id="result">No campaign prepared.</pre>
<script>
const $=id=>document.getElementById(id);let offset=0,current=null;const selected=new Set();
async function api(path,body){const r=await fetch(path,{method:body===undefined?'GET':'POST',headers:body===undefined?{}:{'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body)});const v=await r.json();if(!r.ok)throw Error((v.code||r.status)+': '+(v.error||'')+' '+(v.hint||''));return v}
function fail(e){$('error').textContent=e.message}function output(v){$('error').textContent='';$('result').textContent=JSON.stringify(v,null,2)}
async function tests(reset){if(reset){offset=0;selected.clear();$('tests').replaceChildren()}const p=await api('/api/v1/xiso-suites/'+encodeURIComponent($('suite').value)+'/tests?limit=100&offset='+offset);if(reset){$('identity').textContent=p.suite.qualification+' | '+p.suite.leafCount+' leaves | '+p.suite.catalogId;$('categories').replaceChildren();for(const c of p.suite.categories){const label=document.createElement('label'),box=document.createElement('input');box.type='checkbox';box.value=c.id;label.append(box,document.createTextNode(' '+c.name+' ('+c.tests+')'));$('categories').append(label)}}for(const t of p.items){const row=document.createElement('tr'),cell=document.createElement('td'),box=document.createElement('input');box.type='checkbox';box.onchange=()=>box.checked?selected.add(t.id):selected.delete(t.id);cell.append(box);row.append(cell);for(const value of [t.id,t.name,t.category]){const td=document.createElement('td');td.textContent=value;row.append(td)}$('tests').append(row)}offset=p.nextOffset;$('more').hidden=offset===null}
$('suite').onchange=()=>tests(true).catch(fail);$('more').onclick=()=>tests(false).catch(fail);
$('prepare').onclick=async()=>{try{const body={id:$('id').value.trim(),application:$('application').value.trim(),suite:$('suite').value,mode:$('mode').value};const categories=[...$('categories').querySelectorAll('input:checked')].map(x=>x.value);if(categories.length)body.categories=categories;if(selected.size)body.tests=[...selected];if($('reference').value.trim())body.reference=$('reference').value.trim();const v=await api('/api/v1/xiso-campaigns',body);current=v.id;output(v);$('start').hidden=v.startRequested;$('refresh').hidden=false}catch(e){fail(e)}};
$('start').onclick=async()=>{try{const v=await api('/api/v1/xiso-campaigns/'+encodeURIComponent(current)+'/start',{});output(v);$('start').hidden=true}catch(e){fail(e)}};
$('refresh').onclick=()=>api('/api/v1/xiso-campaigns/'+encodeURIComponent(current)).then(output).catch(fail);
(async()=>{const p=await api('/api/v1/xiso-suites?limit=100');for(const s of p.items){const option=document.createElement('option');option.value=s.id;option.textContent=s.id+' — '+s.qualification;$('suite').append(option)}if(p.items.length)await tests(true);else output({message:'Register one exact suite bundle and a saved boot template first.'})})().catch(fail);
</script></html>
""";
}
