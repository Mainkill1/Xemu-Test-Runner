namespace XemuTestRunner.Networking;

internal static class TestConfigurationPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Test workbench</title>
<style>
*{box-sizing:border-box}body{font:15px/1.5 system-ui,sans-serif;color:#172b3a;background:#f5f7fa;margin:0;padding:20px}main{max-width:1200px;margin:auto}nav,.actions{display:flex;flex-wrap:wrap;align-items:center;gap:10px}nav a{color:#174d72}h1{font-size:28px;margin-bottom:4px}h2{font-size:20px;margin-top:0}section{background:white;border:1px solid #cad4df;border-radius:8px;padding:18px;margin:18px 0;min-width:0}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,270px),1fr));gap:14px}.field{display:flex;flex-direction:column;gap:5px;min-width:0}label{font-weight:600}button,input,select,textarea{font:inherit;max-width:100%;padding:9px;border:1px solid #718496;border-radius:4px}input,select,textarea{width:100%;background:white;color:#172b3a}button{background:#193b54;color:white;cursor:pointer}button:disabled{opacity:.55;cursor:not-allowed}button.secondary{background:white;color:#193b54}button:focus-visible,a:focus-visible,input:focus-visible,select:focus-visible,textarea:focus-visible{outline:3px solid #b86800;outline-offset:3px}.note,.status{color:#40596c}.status{margin:6px 0;font-size:14px}.table-wrap{overflow:auto}table{border-collapse:collapse;width:100%;margin:12px 0}th,td{padding:10px;text-align:left;vertical-align:top;border-bottom:1px solid #dce3ea;overflow-wrap:anywhere}th{font-size:13px}code,pre,textarea{font-family:ui-monospace,monospace}pre{padding:14px;background:#eff3f7;max-height:50vh;overflow:auto;white-space:pre-wrap;overflow-wrap:anywhere}textarea{min-height:200px}#error{background:#fff0f0;color:#761b1b;border:1px solid #b53030;padding:14px;white-space:pre-wrap;overflow-wrap:anywhere}#notice{white-space:pre-wrap;overflow-wrap:anywhere}.request{border-bottom:1px solid #dce3ea;padding:12px 0;overflow-wrap:anywhere}.request p{margin:4px 0}.request strong{margin-right:10px}.actions{margin-top:12px}a{overflow-wrap:anywhere}details{margin-top:12px}summary{cursor:pointer}.full-hash{overflow-wrap:anywhere}#raw-runs p{overflow-wrap:anywhere}[hidden]{display:none!important}@media(max-width:600px){body{padding:10px}section{padding:12px}th,td{padding:7px}h1{font-size:24px}.actions button{flex-grow:1}}
</style>
</head>
<body><main>
<nav aria-label="Runner navigation"><a href="/">Runner</a><a href="/control">Control</a><a href="/tests" aria-current="page">Tests</a></nav>
<h1>Test workbench</h1>
<p class="note">Select stored applications and immutable tests. Saving or selecting never starts execution. Start is a separate, confirmed request.</p>
<p id="error" role="alert" aria-live="assertive" tabindex="-1" hidden></p><p id="notice" role="status" aria-live="polite"></p>
<section aria-labelledby="catalog-heading">
<h2 id="catalog-heading">Test configurations</h2>
<div class="field"><label for="config-search">Search loaded configurations</label><input id="config-search" type="search" placeholder="Name, description or revision"><span class="note">Load more to include another catalog page in this search.</span></div>
<div class="actions"><button id="config-refresh" class="secondary">Refresh configurations</button><button id="config-more" class="secondary" hidden>Load more configurations</button></div>
<p id="config-status" class="status" role="status"></p>
<div class="table-wrap"><table><thead><tr><th scope="col">Name</th><th scope="col">Revision</th><th scope="col">Steps</th><th scope="col">Description</th><th scope="col">Inspect</th></tr></thead><tbody id="config-rows"></tbody></table></div>
<h3 id="selected">Configuration viewer</h3><pre id="config">Select Inspect to read the complete saved definition.</pre>
<div class="actions"><a id="download" hidden download="test-definition.json">Download full definition JSON</a><button id="edit-copy" class="secondary" disabled>Copy JobDefinition to editor</button></div>
</section>
<section aria-labelledby="edit-heading">
<h2 id="edit-heading">Save a new named configuration</h2>
<p class="note">The editor contains JobDefinition JSON, not the enclosing saved definition. The existing source package supplies fixed assets. Old revisions are retained.</p>
<div class="grid"><div class="field"><label for="name">New configuration name</label><input id="name" placeholder="vulkan-smoke-copy"></div><div class="field"><label for="source">Asset source job</label><input id="source" list="source-jobs" placeholder="Select or enter a retained source job"><datalist id="source-jobs"></datalist></div><div class="field"><label for="description">Description</label><input id="description" maxlength="240"></div></div>
<div class="actions"><label for="file">Read JobDefinition JSON from a file</label><input id="file" type="file" accept=".json,application/json"></div>
<div class="field"><label for="json">JobDefinition JSON</label><textarea id="json" spellcheck="false" placeholder='{"executable":"xemu.exe","plan":[]}'></textarea></div>
<div class="field"><label for="builds">Replaceable build files (optional, comma-separated)</label><input id="builds" placeholder="xemu.exe,dependency.dll"></div>
<div class="actions"><button id="save" data-action="save">Save configuration only</button></div>
</section>
<section aria-labelledby="select-heading">
<h2 id="select-heading">Select a test without starting it</h2>
<p class="note">Upload application packages with the HTTP/Python upload workflow first. The server validates readiness; this page does not launch a second executor.</p>
<div class="grid"><div class="field"><label for="application-select">Stored application</label><select id="application-select"><option value="">Select an application</option></select></div><div class="field"><label for="config-select">Pinned test revision</label><select id="config-select"><option value="">Select a configuration</option></select></div><div class="field"><label for="request-id">Request ID for this attempt</label><input id="request-id" autocomplete="off"></div></div>
<div class="actions"><button id="applications-refresh" class="secondary">Refresh applications</button><button id="applications-more" class="secondary" hidden>Load more applications</button><button id="new-request" class="secondary">New attempt ID</button><button id="select-request" data-action="select">Save selection only</button><button id="recover-request" class="secondary" data-action="recover">Inspect this request ID</button></div>
<p id="app-status" class="status" role="status"></p>
</section>
<section aria-labelledby="requests-heading">
<h2 id="requests-heading">Requested tests</h2>
<p class="note">Statuses are observations, not correctness verdicts. Refresh to inspect current state. After a disconnected Start, inspect the same ID instead of creating another attempt.</p>
<div class="actions"><button id="requests-refresh" class="secondary">Refresh requests</button><button id="requests-more" class="secondary" hidden>Load more requests</button></div>
<p id="requests-status" class="status" role="status"></p><div id="requests"></div>
</section>
<section aria-labelledby="results-heading">
<h2 id="results-heading">Stored build results</h2>
<p id="baseline" class="full-hash">Reading the baseline pin…</p>
<div class="grid"><div class="field"><label for="build-a">Reference A</label><select id="build-a"><option value="">Use the explicitly pinned baseline</option></select></div><div class="field"><label for="build-b">Candidate B</label><select id="build-b"><option value="">Select an indexed build</option></select></div></div>
<div class="actions"><button id="builds-refresh" class="secondary">Refresh builds</button><button id="builds-more" class="secondary" hidden>Load more builds</button><button id="baseline-refresh" class="secondary" data-action="baseline-read">Inspect baseline</button><button id="compare" data-action="compare">Compare A/B</button><button id="show-build" class="secondary" data-action="report">Show build B</button><button id="pin" data-action="pin">Pin B as baseline…</button></div>
<p id="build-status" class="status" role="status"></p><pre id="result">Select a stored build to request a server-generated report. No raw measurements are downloaded automatically.</pre>
<a id="comparison-download" hidden download="comparison.csv">Download full comparison CSV</a>
<div class="actions"><button id="raw-toggle" class="secondary" data-action="raw">List stored runs and raw CSV links for B</button><button id="raw-more" class="secondary" data-action="raw" hidden>More stored runs</button></div><div id="raw-runs"></div>
</section>
<script>
'use strict';
const $ = id => document.getElementById(id);
const pending = new Set(), uncertainStarts = new Set();
let uncertainPin = false, inspected = null, inspectionVersion = 0, inspectionController = null;
let reportVersion = 0, rawVersion = 0, rawOffset = 0, objectUrl = null, baselineVersion = 0;
const catalogs = {
  configs: {path:'/api/v1/test-configs', items:[], next:0, status:'config-status', more:'config-more', refresh:'config-refresh'},
  apps: {path:'/api/v1/jobs', items:[], next:0, status:'app-status', more:'applications-more', refresh:'applications-refresh'},
  requests: {path:'/api/v1/test-runs', items:[], next:0, status:'requests-status', more:'requests-more', refresh:'requests-refresh'},
  builds: {path:'/api/v1/builds', items:[], next:0, status:'build-status', more:'builds-more', refresh:'builds-refresh'}
};
for (const c of Object.values(catalogs)) Object.assign(c, {version:0, loading:false, controller:null});
function text(value) { return value == null ? '' : String(value); }
function element(tag, value) { const e=document.createElement(tag); if(value!==undefined)e.textContent=text(value); return e; }
function notice(value) { $('notice').textContent=value; }
function clearError() { $('error').hidden=true; $('error').textContent=''; }
function fail(error) {
  if(error.name==='AbortError')return;
  $('error').textContent=error.message || String(error); $('error').hidden=false; $('error').focus();
}
function storedId(value) {
  try { if(value!==undefined)sessionStorage.setItem('xemu-request-id',value); return sessionStorage.getItem('xemu-request-id'); }
  catch { return null; }
}
function newId() {
  const words=new Uint32Array(2); crypto.getRandomValues(words);
  return 'human-'+Date.now().toString(36)+'-'+Array.from(words,n=>n.toString(16).padStart(8,'0')).join('');
}
function safeApiPath(value) { return typeof value==='string' && value.startsWith('/api/v1/') && !/[\\\r\n]/.test(value) ? value : null; }
async function api(path, options={}) {
  const method=options.method || 'GET', controller=new AbortController();
  const abort=()=>controller.abort(); options.signal?.addEventListener('abort',abort,{once:true});
  if(options.signal?.aborted)controller.abort();
  const timer=setTimeout(abort,20000);
  try {
    const response=await fetch(path,{method,signal:controller.signal,cache:'no-store',
      headers:options.body===undefined?{}:{'Content-Type':'application/json'},
      body:options.body===undefined?undefined:typeof options.body==='string'?options.body:JSON.stringify(options.body)});
    if(!response.ok) {
      let value={}; try { value=await response.json(); } catch { /* Retain status if a proxy returned non-JSON. */ }
      const field=value.details?.field ? '\nField: '+text(value.details.field) : '';
      const reference=value.requestId || response.headers.get('X-Request-Id');
      const error=new Error(text(value.code || response.status)+': '+text(value.error || 'Request failed')+'\n'+text(value.hint)+field+(reference?'\nRequest: '+reference:''));
      error.status=response.status; throw error;
    }
    return options.text ? await response.text() : await response.json();
  } catch(error) {
    if(options.signal?.aborted)throw new DOMException('Superseded read','AbortError');
    if(error.status)throw error;
    const failure=new Error(method==='GET' ? 'Connection interrupted. Refresh to inspect current server state.' :
      'Connection interrupted. The mutation outcome is unknown. Inspect the same request ID or baseline before trying again.');
    failure.network=true; throw failure;
  } finally { clearTimeout(timer); options.signal?.removeEventListener('abort',abort); }
}
function syncButtons() {
  for(const button of document.querySelectorAll('[data-action]'))button.disabled=pending.has(button.dataset.action);
  $('select-request').disabled=pending.has('select') || !$('application-select').value || !$('config-select').value;
  for(const id of ['compare','show-build','raw-toggle'])$(id).disabled=$(id).disabled || !$('build-b').value;
  $('pin').disabled=pending.has('pin') || uncertainPin || !$('build-b').value;
  for(const button of document.querySelectorAll('[data-kind="start"]'))
    button.disabled=pending.has(button.dataset.action) || uncertainStarts.has(button.dataset.requestId);
}
async function perform(key, operation) {
  if(pending.has(key))return;
  pending.add(key); clearError(); syncButtons();
  try { await operation(); } catch(error) { fail(error); }
  finally { pending.delete(key); syncButtons(); }
}
function catalogKey(kind,item) { return kind==='configs' ? item.id+'@'+item.revision : kind==='builds' ? item.sha256 : item.id; }
function mergeItems(kind, first, second) {
  const values=new Map(first.map(item=>[catalogKey(kind,item),item]));
  for(const item of second)values.set(catalogKey(kind,item),item);
  return Array.from(values.values());
}
async function load(kind, reset=true) {
  const c=catalogs[kind]; if(!reset && (c.loading || c.next===null))return;
  const version=++c.version; c.controller?.abort(); c.controller=new AbortController(); c.loading=true;
  $(c.more).disabled=true; $(c.status).textContent='Loading…';
  const offset=reset?0:c.next;
  try {
    const page=await api(c.path+'?limit=25&offset='+offset,{signal:c.controller.signal});
    if(version!==c.version)return;
    if(!Array.isArray(page.items) || (page.nextOffset!==null && (!Number.isInteger(page.nextOffset) || page.nextOffset<=offset)))throw Error('Invalid catalog pagination response.');
    c.items=mergeItems(kind,reset?[]:c.items,page.items); c.next=page.nextOffset;
    render(kind); $(c.status).textContent=c.items.length ? c.items.length+' loaded. '+(c.next===null?'No more pages.':'More entries are available.') : 'No stored entries yet.';
    $(c.more).hidden=c.next===null;
  } catch(error) {
    if(version!==c.version)return;
    $(c.status).textContent='Unavailable. Previously loaded entries are not a fresh server observation.'; fail(error);
  } finally { if(version===c.version){c.loading=false;$(c.more).disabled=false;syncButtons();} }
}
function options(select, values, empty) {
  const previous=select.value, old=select.selectedOptions[0]?.cloneNode(true);
  select.replaceChildren(new Option(empty,''));
  for(const value of values)select.add(new Option(value.label,value.value));
  if(previous && !values.some(value=>value.value===previous) && old)select.add(old);
  select.value=previous;
}
function render(kind) {
  if(kind==='configs') {
    const query=$('config-search').value.toLowerCase(); $('config-rows').replaceChildren();
    for(const item of catalogs.configs.items) {
      const row=element('tr'); row.hidden=![item.id,item.revision,item.description].some(v=>text(v).toLowerCase().includes(query));
      for(const value of [item.id,text(item.revision).slice(0,12),item.stepCount,item.description])row.append(element('td',value));
      const cell=element('td'), button=element('button','Inspect'); button.className='secondary';
      button.onclick=()=>inspect(item); cell.append(button); row.append(cell); $('config-rows').append(row);
    }
    options($('config-select'),catalogs.configs.items.map(item=>({value:item.id+'@'+item.revision,label:item.id+' · '+text(item.revision).slice(0,12)})),'Select a configuration');
  } else if(kind==='apps') {
    const stable=catalogs.apps.items.filter(item=>['draft','tested','cancelled'].includes(item.state));
    options($('application-select'),stable.map(item=>({value:item.id,label:item.id+' · '+item.state})),'Select an application');
    $('source-jobs').replaceChildren(...stable.map(item=>new Option(item.id,item.id)));
  } else if(kind==='builds') {
    const values=catalogs.builds.items.map(item=>({value:item.sha256,label:item.reference || text(item.sha256).slice(0,12)}));
    options($('build-a'),values,'Use the explicitly pinned baseline'); options($('build-b'),values,'Select an indexed build');
  } else renderRequests();
}
async function inspect(item) {
  const version=++inspectionVersion; inspectionController?.abort(); inspectionController=new AbortController();
  $('selected').textContent='Loading '+item.id+'…';
  try {
    const value=await api('/api/v1/test-configs/'+encodeURIComponent(item.id)+'/'+item.revision,{signal:inspectionController.signal});
    if(version!==inspectionVersion)return;
    inspected=value; $('selected').textContent=item.id+' @ '+value.revision; $('config').textContent=JSON.stringify(value,null,2);
    if(objectUrl)URL.revokeObjectURL(objectUrl);
    objectUrl=URL.createObjectURL(new Blob([JSON.stringify(value,null,2)],{type:'application/json'}));
    $('download').href=objectUrl; $('download').download=item.id+'-definition.json'; $('download').hidden=false; $('edit-copy').disabled=false;
  } catch(error) { if(version===inspectionVersion)fail(error); }
}
function upsertRequest(value) {
  catalogs.requests.items=mergeItems('requests',catalogs.requests.items,[value]);
  uncertainStarts.delete(value.id); renderRequests(); syncButtons();
}
function renderRequests() {
  const labels={uploaded:'Not started',queued:'Queued',preparing:'Preparing',queuedForExecution:'Queued for execution',running:'Running',held:'Held — inspect before proceeding',tested:'Completed — inspect the result',failed:'Failed',cancelled:'Cancelled'};
  $('requests').replaceChildren();
  if(!catalogs.requests.items.length){$('requests').append(element('p','No requested tests on this page.'));return;}
  for(const item of catalogs.requests.items) {
    const row=element('div'); row.className='request';
    const title=element('p'); title.append(element('strong',item.id),element('span',labels[item.state] || item.state)); row.append(title);
    row.append(element('p',text(item.testId)+' · '+text(item.revision).slice(0,12)+' · '+text(item.applicationJobId)));
    if(item.error || item.errorCode)row.append(element('p',text(item.errorCode)+' '+text(item.error)));
    const actions=element('div'); actions.className='actions';
    if(item.state==='uploaded' && !item.startRequested) {
      const button=element('button','Start'); button.dataset.kind='start';button.dataset.requestId=item.id;button.dataset.action='start:'+item.id;
      button.onclick=()=>{
        if(pending.has('start:'+item.id) || uncertainStarts.has(item.id) || !confirm('Start '+item.id+'? This authorizes execution and queues behind existing work.'))return;
        $('request-id').value=item.id;storedId(item.id);
        perform('start:'+item.id,async()=>{
          try {upsertRequest(await api('/api/v1/test-runs/'+encodeURIComponent(item.id)+'/start',{method:'POST',body:{}}));notice('Start acknowledged for '+item.id+'.');}
          catch(error){if(error.network)uncertainStarts.add(item.id);throw error;}
        });
      };actions.append(button);
    }
    if(['uploaded','queued'].includes(item.state)) {
      const button=element('button','Cancel request');button.className='secondary';button.dataset.action='cancel:'+item.id;
      button.onclick=()=>{if(confirm('Cancel request '+item.id+'? This does not delete its uploaded application.'))perform('cancel:'+item.id,async()=>upsertRequest(await api('/api/v1/test-runs/'+encodeURIComponent(item.id),{method:'DELETE'})));};actions.append(button);
    }
    const href=safeApiPath(item.result);if(href){const link=element('a','Inspect result');link.href=href;actions.append(link);}
    row.append(actions);$('requests').append(row);
  }
  syncButtons();
}
async function readBaseline() {
  const version=++baselineVersion,value=await api('/api/v1/baseline');if(version!==baselineVersion)return value;uncertainPin=false;
  $('baseline').textContent=value.configured?'Pinned baseline: '+value.sha256+' · '+value.runCount+' attempts · revision '+value.revision:'No known baseline is pinned. Select A explicitly or deliberately pin a qualified build.';
  syncButtons();return value;
}
function comparisonPath(format) {
  const query=new URLSearchParams({B:$('build-b').value,format});if($('build-a').value)query.set('A',$('build-a').value);
  return '/api/v1/compare?'+query;
}
async function rawRuns(reset) {
  const build=$('build-b').value;if(!build)return;const version=++rawVersion;
  const offset=reset?0:rawOffset;if(offset===null)return;
  const page=await api('/api/v1/build-results/'+build+'/runs?limit=20&offset='+offset);
  if(version!==rawVersion || build!==$('build-b').value)return;
  if(reset)$('raw-runs').replaceChildren();
  for(const item of page.items) {
    const row=element('p',text(item.runId)+' · '+text(item.test)+' · '+text(item.outcome)+' '),path=safeApiPath(item.rawCsv);
    if(path){const link=element('a','Download raw metrics CSV');link.href=path;link.download='metrics.csv';row.append(link);}else row.append(element('span','No raw CSV link supplied.'));
    $('raw-runs').append(row);
  }
  rawOffset=page.nextOffset;$('raw-more').hidden=rawOffset===null;
}
for(const [kind,c] of Object.entries(catalogs)) {
  $(c.refresh).onclick=()=>load(kind,true);$(c.more).onclick=()=>load(kind,false);
}
$('config-search').oninput=()=>render('configs');
$('application-select').onchange=$('config-select').onchange=syncButtons;
$('request-id').onchange=()=>storedId($('request-id').value.trim());
$('new-request').onclick=()=>{$('request-id').value=newId();storedId($('request-id').value);notice('New attempt ID created. No selection was saved or started.');};
$('select-request').onclick=()=>perform('select',async()=>{
  const id=$('request-id').value.trim(), [testId,revision]=$('config-select').value.split('@');
  if(!id || !testId || !revision || !$('application-select').value)throw Error('Choose an application, a pinned test, and an attempt ID.');
  storedId(id);
  const value=await api('/api/v1/test-runs',{method:'POST',body:{id,applicationJobId:$('application-select').value,testId,revision}});
  upsertRequest(value);notice(value.state==='uploaded' && !value.startRequested ? 'Selection saved for '+id+'. It has not been started.' : 'Existing request recovered for '+id+'. Inspect its current state; no new Start was sent.');
});
$('recover-request').onclick=()=>perform('recover',async()=>{
  const id=$('request-id').value.trim();if(!id)throw Error('Enter the original request ID.');
  upsertRequest(await api('/api/v1/test-runs/'+encodeURIComponent(id)));notice('Read the current state for '+id+'. No start was sent.');
});
$('edit-copy').onclick=()=>{
  if(!inspected)return;const value=JSON.stringify(inspected.definition.job,null,2);
  if($('json').value && $('json').value!==value && !confirm('Replace the unsaved editor text with a copy of this definition?'))return;
  $('json').value=value;$('name').value=inspected.definition.id+'-copy';$('source').value=inspected.definition.sourceJobId;
  $('description').value=inspected.definition.description || '';$('builds').value=(inspected.definition.buildFiles || []).join(',');$('name').focus();
};
$('file').onchange=async()=>{
  try {const file=$('file').files[0];if(!file)return;if(file.size>1048576)throw Error('Configuration files must be at most 1 MiB.');
    const previous=$('json').value,value=await file.text();if($('json').value!==previous && !confirm('Replace the editor changes made while this file was being read?'))return;$('json').value=value;
  }catch(error){fail(error);}
};
$('save').onclick=()=>perform('save',async()=>{
  const name=$('name').value.trim(),sourceJobId=$('source').value.trim(),raw=$('json').value;
  if(!name || !sourceJobId)throw Error('Enter a new configuration name and a retained asset source job.');
  if(catalogs.configs.items.some(item=>item.id===name))throw Error('Choose a new name; this name is already in the loaded catalog.');
  const job=JSON.parse(raw);if(!job || typeof job!=='object' || Array.isArray(job))throw Error('JobDefinition must be a JSON object.');
  const fields={sourceJobId,description:$('description').value};const paths=$('builds').value.split(',').map(v=>v.trim()).filter(Boolean);if(paths.length)fields.buildFiles=paths;
  // Keep the original job JSON text so duplicate keys still reach strict server validation.
  const body=JSON.stringify(fields).slice(0,-1)+',"job":'+raw+'}';
  const saved=await api('/api/v1/test-configs/'+encodeURIComponent(name),{method:'POST',body});
  notice('Configuration '+saved.id+' saved. No test was started.');await load('configs',true);await inspect(saved);
});
$('build-a').onchange=()=>{reportVersion++;$('comparison-download').hidden=true;syncButtons();};
$('build-b').onchange=()=>{reportVersion++;rawVersion++;rawOffset=0;$('raw-runs').replaceChildren();$('raw-more').hidden=true;$('comparison-download').hidden=true;syncButtons();};
$('compare').onclick=()=>perform('compare',async()=>{
  const version=++reportVersion,path=comparisonPath('markdown'),csv=comparisonPath('csv');
  const value=await api(path,{text:true});if(version!==reportVersion)return;
  $('result').textContent=value;$('comparison-download').href=csv;$('comparison-download').hidden=false;
});
$('show-build').onclick=()=>perform('report',async()=>{
  const version=++reportVersion,build=$('build-b').value;const value=await api('/api/v1/build-results/'+build+'?format=markdown',{text:true});
  if(version!==reportVersion)return;$('result').textContent=value;$('comparison-download').hidden=true;
});
$('baseline-refresh').onclick=()=>perform('baseline-read',readBaseline);
$('pin').onclick=()=>{
  const sha256=$('build-b').value;if(!sha256 || uncertainPin || pending.has('pin') || !confirm('Replace the saved baseline with build '+sha256+'? This changes future default comparisons, not test execution.'))return;
  baselineVersion++;
  perform('pin',async()=>{
    try {await api('/api/v1/baseline',{method:'PUT',body:{sha256}});await readBaseline();reportVersion++;$('comparison-download').hidden=true;notice('Baseline pin acknowledged. Request a fresh comparison against this pin.');}
    catch(error){if(error.network)uncertainPin=true;throw error;}
  });
};
$('raw-toggle').onclick=()=>perform('raw',()=>rawRuns(true));$('raw-more').onclick=()=>perform('raw',()=>rawRuns(false));
$('request-id').value=storedId() || newId();
syncButtons();for(const kind of Object.keys(catalogs))load(kind,true);readBaseline().catch(fail);
</script>
</main></body></html>
""";
}
