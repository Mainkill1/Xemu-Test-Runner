namespace XemuTestRunner.Networking;

/// <summary>Read-only, bounded browser inspection using the existing policy-controlled artifact route.</summary>
internal static class ArtifactViewerPage
{
    public const string Html = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src blob:; base-uri 'none'; form-action 'none'; object-src 'none'">
<title>Artifact viewer</title>
<style>
:root{color-scheme:dark;--bg:#10141b;--panel:#19212d;--line:#344255;--muted:#a8b7c9;--accent:#8cc5ff}*{box-sizing:border-box}[hidden]{display:none!important}body{margin:0;background:var(--bg);color:#edf3fc;font:14px/1.5 system-ui}header{padding:16px 24px;border-bottom:1px solid var(--line)}nav,.toolbar{display:flex;align-items:center;gap:12px;flex-wrap:wrap}a{color:var(--accent)}main{padding:20px 24px;max-width:1800px;margin:auto}h1{font-size:21px;margin:0 0 4px;overflow-wrap:anywhere}.muted{color:var(--muted)}button,input,select,textarea{font:inherit;color:inherit;background:var(--panel);border:1px solid var(--line);border-radius:5px;padding:7px 10px}button{cursor:pointer}button:hover,a:hover{color:#b8dbff}button:focus-visible,a:focus-visible,input:focus-visible,td:focus-visible{outline:2px solid var(--accent);outline-offset:2px}button:disabled{opacity:.45;cursor:default}input[type=checkbox]{accent-color:#73b6fa}#status{margin:14px 0;padding:10px 14px;border-left:3px solid #6aaef5;background:var(--panel);white-space:pre-wrap;overflow-wrap:anywhere}#status[data-state=error]{border-color:#f29595;color:#ffc3c3}.toolbar{padding:10px 0}.sheet{border:1px solid var(--line);border-radius:6px;overflow:auto;max-height:62vh;background:#151c26}table{border-collapse:separate;border-spacing:0;min-width:100%;font:13px/1.45 ui-monospace,monospace}td,th{border-right:1px solid #293748;border-bottom:1px solid #293748;padding:7px 11px;text-align:left;white-space:pre-wrap;min-width:115px;max-width:400px;overflow-wrap:anywhere}thead th{position:sticky;top:0;background:#253347;z-index:2}thead button{background:transparent;border:0;padding:0;width:100%;text-align:left}tbody th{position:sticky;left:0;background:#253347;color:var(--muted);min-width:55px;z-index:1}tbody tr:nth-child(even){background:#1b2532}tbody td:hover{background:#293c52}td.numeric{text-align:right;font-variant-numeric:tabular-nums}#cellValue{width:100%;height:65px;font-family:ui-monospace,monospace;resize:vertical}pre{margin:0;background:#131b25;padding:18px;max-height:72vh;overflow:auto;border:1px solid var(--line);border-radius:6px;font:13px/1.6 ui-monospace,monospace;tab-size:2;white-space:pre}pre.wrap{white-space:pre-wrap;overflow-wrap:anywhere}#imageStage{height:70vh;overflow:auto;display:flex;align-items:flex-start;justify-content:center;border:1px solid var(--line);background:repeating-conic-gradient(#1b2532 0% 25%,#131b25 0% 50%) 0/24px 24px;padding:16px}#image{display:block;flex:none}#image.fit{max-width:100%;max-height:100%;object-fit:contain;width:auto;height:auto}#zoomValue{min-width:55px;font-variant-numeric:tabular-nums}#imageContext{margin:10px 0;padding:12px 14px;background:var(--panel);border:1px solid var(--line);border-radius:6px;display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:8px 18px}#imageContext strong{display:block;color:#fff}#imageContext .context-note{grid-column:1/-1;color:var(--muted);font-size:12px}.footnote{margin:10px 0;color:var(--muted);font-size:12px}@media(max-width:700px){header,main{padding:14px}.sheet{max-height:55vh}td,th{padding:6px}}
</style></head><body>
<header><nav><strong>Artifact viewer</strong><a href="/results" id="back">Back to run</a><a href="/diagnostics">Diagnostics</a><a href="/">Home</a></nav></header>
<main><h1 id="name">Select an artifact from Evidence</h1><div id="identity" class="muted"></div>
<div class="toolbar"><button id="reload" type="button">Refresh preview</button><a id="download" hidden download>Download original</a><span class="muted">Read-only. Original evidence is never modified.</span></div>
<div id="status" role="status" aria-live="polite" data-state="loading">Loading preview...</div>
<section id="csvPanel" hidden aria-label="CSV spreadsheet viewer">
<div class="toolbar"><label>Filter rows <input id="filter" type="search" placeholder="Search loaded cells"></label><label>Delimiter <select id="delimiter"><option value=",">Comma</option><option value=";">Semicolon</option><option value="tab">Tab</option></select></label><label><input id="headers" type="checkbox" checked> First row is header</label><label><input id="rawNumbers" type="checkbox"> Exact numbers</label></div>
<div class="sheet"><table id="grid" aria-label="Read-only CSV data"><thead></thead><tbody></tbody></table></div>
<div class="toolbar"><button id="previous">Previous</button><span id="pageInfo"></span><button id="next">Next</button></div>
<label for="cellValue">Selected cell — exact original value</label><textarea id="cellValue" readonly spellcheck="false"></textarea>
<p class="footnote">Click a column to sort the loaded rows. Decimal measurements display two places; select a cell or enable Exact numbers to see full precision. IDs and large integers remain unchanged. No formulas are evaluated.</p></section>
<section id="textPanel" hidden aria-label="Text and JSON viewer"><div class="toolbar"><label><input id="pretty" type="checkbox" checked> Pretty JSON</label><label><input id="wrap" type="checkbox"> Wrap lines</label><button id="copy">Copy displayed text</button><span id="copyStatus" class="muted"></span></div><pre id="text" tabindex="0"></pre></section>
<section id="imagePanel" hidden aria-label="Image viewer"><div id="imageContext" hidden></div><div class="toolbar"><button id="fit">Fit</button><button id="actual">100%</button><button id="zoomOut" aria-label="Zoom out">−</button><span id="zoomValue">Fit</span><button id="zoomIn" aria-label="Zoom in">+</button><span id="dimensions" class="muted"></span></div><div id="imageStage"><img id="image" class="fit" alt="Selected diagnostic image"></div></section>
</main><script>
'use strict';
const $=id=>document.getElementById(id);
const TEXT_BYTES=1024*1024, IMAGE_BYTES=16*1024*1024, PAGE_ROWS=50, MAX_ROWS=10000, MAX_COLUMNS=128;
const imageTypes={png:'image/png',jpg:'image/jpeg',jpeg:'image/jpeg',gif:'image/gif',webp:'image/webp',bmp:'image/bmp'};
const textTypes=new Set(['json','jsonl','ndjson','csv','tsv','txt','log','md','toml','ini','cfg','xml','html','htm','svg','yaml','yml','out','err','report']);
let controller=null,objectUrl=null,requestNumber=0,sourceText='',sourcePartial=false,baseMessage='',extension='',zoom=1;
let csvRows=[],gridRows=[],columnNames=[],pageIndex=0,sortColumn=-1,sortDirection=1;
function status(message,state='ready'){$('status').textContent=message;$('status').dataset.state=state;}
function twoDecimals(value){const text=value.toFixed(2);return text==='-0.00'?'0.00':text;}
function safePath(value){return !!value&&!/[\\:\u0000-\u001f\u007f]/.test(value)&&value.split('/').every(part=>part&&part!=='.'&&part!=='..');}
function selection(){
 const query=new URLSearchParams(location.search),run=query.get('run'),file=query.get('file');
 if(!safePath(run)||run.includes('/')||!safePath(file))throw Error('Use a run ID and relative artifact path from Evidence.');
 return {run,file,url:'/api/v1/runs/'+encodeURIComponent(run)+'/artifacts/'+file.split('/').map(encodeURIComponent).join('/')};
}
function releaseImage(){if(objectUrl){URL.revokeObjectURL(objectUrl);objectUrl=null;}$('image').removeAttribute('src');$('imageContext').hidden=true;$('imageContext').replaceChildren();}
function contextField(label,value){const box=document.createElement('div'),strong=document.createElement('strong'),text=document.createElement('span');strong.textContent=label;text.textContent=value;box.append(strong,text);return box;}
function guestText(point){if(!point)return 'Unavailable';return 'frame '+Number(point.frame).toLocaleString()+' · guest '+twoDecimals(Number(point.timestampUs)/1e6)+' s';}
async function loadImageContext(selected,signal){
 try{
  const response=await fetch(selected.url+'.context.json',{cache:'no-store',redirect:'error',signal});
  if(response.status===404)return;
  if(!response.ok)return;
  const raw=await response.text();if(raw.length>65536)return;
  const value=JSON.parse(raw),panel=$('imageContext');panel.replaceChildren();
  panel.append(contextField('Purpose',(value.purpose||'diagnostic').replace(/^./,c=>c.toUpperCase())));
  panel.append(contextField('Segment',value.segment||'None'));
  const before=value.guestBefore,after=value.guestAfter;
  panel.append(contextField('Guest around capture',before&&after&&before.frame!==after.frame?guestText(before)+' → '+guestText(after):guestText(after||before)));
  if(value.lastInput){
   const delta=value.sinceLastInput||{};
   const parts=[value.lastInput.button];
   if(Number.isFinite(delta.guestFrames))parts.push(Number(delta.guestFrames).toLocaleString()+' guest frames earlier');
   else if(Number.isFinite(delta.hostMs))parts.push(twoDecimals(Number(delta.hostMs))+' host ms earlier');
   panel.append(contextField('Last input',parts.join(' · ')));
  }else panel.append(contextField('Last input','None recorded'));
  const note=document.createElement('div');note.className='context-note';note.textContent=value.guestSemantics||'Guest timing is diagnostic context, not an identical-frame requirement.';panel.append(note);
  if(value.captured===false&&value.error){const failed=contextField('Capture error',String(value.error).slice(0,512));panel.append(failed);}
  panel.hidden=false;
 }catch(error){if(error.name!=='AbortError')$('imageContext').hidden=true;}
}

// A Range request avoids transferring huge evidence just to display a preview.
// Stream limits also protect the browser if a proxy ignores the Range header.
async function boundedRead(response,limit){
 if(!response.body)throw Error('This browser cannot stream an artifact preview. Use Download original.');
 const reader=response.body.getReader(),parts=[];let received=0,ended=false;
 try{
  while(received<=limit){const item=await reader.read();if(item.done){ended=true;break;}const take=Math.min(item.value.length,limit+1-received);parts.push(item.value.slice(0,take));received+=take;if(take<item.value.length||received>limit)break;}
 }finally{await reader.cancel();reader.releaseLock();}
 const bytes=new Uint8Array(received);let offset=0;for(const part of parts){bytes.set(part,offset);offset+=part.length;}
 return {bytes,ended};
}
async function fetchPreview(url,limit,signal){
 const response=await fetch(url,{cache:'no-store',redirect:'error',signal,headers:{Range:'bytes=0-'+limit}});
 if(!response.ok){
  const failure=await boundedRead(response,65536),text=new TextDecoder().decode(failure.bytes);let error=null;
  try{error=JSON.parse(text);}catch{}
  // The existing server rejects all ranges on an empty file. Its explicit
  // fileLength=0 receipt is an empty preview, not a retry or download request.
  if(response.status===416&&error?.code==='range_invalid'&&error.details?.fileLength===0)
   return {bytes:new Uint8Array(),partial:false,total:0};
  const message=error?(error.code||response.status)+': '+(error.error||error.hint||'Preview unavailable.'):text;
  throw Error(message.slice(0,2048));
 }
 const range=response.headers.get('Content-Range');let total=null,expected=null;
 if(response.status===206){const match=/^bytes 0-(\d+)\/(\d+)$/.exec(range||'');if(!match){await response.body?.cancel();throw Error('Invalid partial-content response; preview was not trusted.');}expected=Number(match[1])+1;total=Number(match[2]);if(expected>total||expected>limit+1){await response.body?.cancel();throw Error('Unexpected artifact range.');}}
 else{const length=response.headers.get('Content-Length');if(length!==null)total=expected=Number(length);}
 const read=await boundedRead(response,limit);
 if(read.ended&&expected!==null&&read.bytes.length<expected)throw Error('Artifact transfer ended early. Refresh the same artifact; no complete preview was claimed.');
 return {bytes:read.bytes.slice(0,limit),partial:read.bytes.length>limit||(total!==null&&total>limit),total};
}

// Validate JSON, but reformat its original tokens rather than stringify a parsed
// object. That preserves large integer IDs, fractional digits and duplicate keys.
function prettyJson(text){
 JSON.parse(text);let output='',depth=0,inString=false,escaped=false;
 const indent=()=>{if(depth>128)throw Error('JSON nesting exceeds the preview limit.');return '\n'+'  '.repeat(depth);};
 for(let i=0;i<text.length;i++){
  const c=text[i];
  if(inString){output+=c;if(escaped)escaped=false;else if(c==='\\')escaped=true;else if(c==='"')inString=false;}
  else if(c==='"'){inString=true;output+=c;}
  else if(/\s/.test(c))continue;
  else if(c==='{'||c==='['){output+=c;let j=i+1;while(j<text.length&&/\s/.test(text[j]))j++;if(text[j]===(c==='{'?'}':']')){output+=text[j];i=j;}else{depth++;output+=indent();}}
  else if(c==='}'||c===']'){depth--;output+=indent()+c;}
  else if(c===',')output+=','+indent();
  else if(c===':')output+=': ';
  else output+=c;
  if(output.length>4*TEXT_BYTES)throw Error('Pretty JSON expansion exceeds the preview limit.');
 }
 return output;
}
function showText(){
 $('textPanel').hidden=false;let rendered=sourceText,message=baseMessage;
 const isJson=['json','jsonl','ndjson'].includes(extension);$('pretty').disabled=!isJson;
 if(isJson&&$('pretty').checked){
  if(sourcePartial)message+=' Partial JSON is shown as raw text; it is not a complete document.';
  else try{rendered=extension==='json'?prettyJson(sourceText):sourceText.split(/\r?\n/).filter(line=>line.trim()).map(prettyJson).join('\n\n');}
  catch(error){message+=' Invalid JSON or preview limit: '+error.message+' Original text shown.';}
 }
 $('text').textContent=rendered;$('text').classList.toggle('wrap',$('wrap').checked);status(message);
}

// RFC 4180-style quoted fields, including embedded newlines and escaped quotes.
// A byte-truncated preview discards the unfinished record rather than inventing it.
function parseCsv(text,separator,partial){
 const rows=[];let row=[],field='',quoted=false,closedQuote=false,limited=false;
 function finishField(){if(row.length>=MAX_COLUMNS)throw Error('CSV exceeds 128 columns; use the original file.');row.push(field);field='';closedQuote=false;}
 function finishRow(){finishField();rows.push(row);row=[];if(rows.length>MAX_ROWS){limited=true;return false;}return true;}
 for(let i=0;i<text.length;i++){
  const c=text[i];
  if(quoted){if(c==='"'){if(text[i+1]==='"'){field+='"';i++;}else{quoted=false;closedQuote=true;}}else field+=c;}
  else if(c===separator)finishField();
  else if(c==='\n'||c==='\r'){if(c==='\r'&&text[i+1]==='\n')i++;if(!finishRow())break;}
  else if(c==='"'){if(field||closedQuote)throw Error('Invalid CSV quote. Original text is available.');quoted=true;}
  else{if(closedQuote)throw Error('Unexpected text after a quoted CSV field.');field+=c;}
  if(field.length>65536)throw Error('CSV cell exceeds the 64 KiB preview limit.');
 }
 if(!limited&&!partial){if(quoted)throw Error('Invalid CSV: unterminated quoted field.');if(field||row.length||closedQuote)finishRow();}
 return {rows,limited};
}
function columnLetter(index){let name='';for(index++;index;index=Math.floor((index-1)/26))name=String.fromCharCode(65+(index-1)%26)+name;return name;}
function numeric(value){return /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$/.test(value)&&Number.isFinite(Number(value))&&Math.abs(Number(value))<=Number.MAX_SAFE_INTEGER;}
function displayCell(value,header){
 if($('rawNumbers').checked||!/[.eE]/.test(value)||!numeric(value)||/(^|[_\s-])(id|hash|sha256|timestamp)([_\s-]|$)/i.test(header))return value;
 return twoDecimals(Number(value));
}
function showCell(value,row,col){$('cellValue').value=value;$('cellValue').setAttribute('aria-label','Row '+row+', column '+columnLetter(col)+', exact original value');}
function renderGrid(){
 const query=$('filter').value.toLowerCase();
 const rows=gridRows.filter(row=>!query||row.values.some(value=>value.toLowerCase().includes(query)));
 if(sortColumn>=0)rows.sort((a,b)=>{const x=a.values[sortColumn]??'',y=b.values[sortColumn]??'';const compared=numeric(x)&&numeric(y)?Number(x)-Number(y):x.localeCompare(y);return compared*sortDirection||a.index-b.index;});
 const pages=Math.max(1,Math.ceil(rows.length/PAGE_ROWS));pageIndex=Math.min(pageIndex,pages-1);
 const head=$('grid').tHead,body=$('grid').tBodies[0];head.replaceChildren();body.replaceChildren();
 const header=document.createElement('tr'),corner=document.createElement('th');corner.textContent='#';header.append(corner);
 columnNames.forEach((name,col)=>{const th=document.createElement('th'),button=document.createElement('button');th.scope='col';th.setAttribute('aria-sort',sortColumn===col?(sortDirection>0?'ascending':'descending'):'none');button.textContent=columnLetter(col)+' · '+name;button.title=name;button.onclick=()=>{sortDirection=sortColumn===col?-sortDirection:1;sortColumn=col;pageIndex=0;renderGrid();};th.append(button);header.append(th);});head.append(header);
 for(const row of rows.slice(pageIndex*PAGE_ROWS,(pageIndex+1)*PAGE_ROWS)){
  const tr=document.createElement('tr'),index=document.createElement('th');index.scope='row';index.textContent=row.index;tr.append(index);
  columnNames.forEach((name,col)=>{const value=row.values[col]??'',td=document.createElement('td'),shown=displayCell(value,name);td.textContent=shown.length>512?shown.slice(0,512)+'…':shown;td.title=value.length>2048?value.slice(0,2048)+'… (select for full value)':value;td.tabIndex=0;if(numeric(value))td.className='numeric';td.onclick=()=>showCell(value,row.index,col);td.onkeydown=event=>{if(event.key==='Enter'||event.key===' '){event.preventDefault();showCell(value,row.index,col);}};tr.append(td);});body.append(tr);
 }
 $('pageInfo').textContent='Page '+(pageIndex+1)+' / '+pages+' · '+rows.length+' matching loaded rows';$('previous').disabled=pageIndex===0;$('next').disabled=pageIndex+1>=pages;
}
function showCsv(){
 try{
  const parsed=parseCsv(sourceText,$('delimiter').value==='tab'?'\t':$('delimiter').value,sourcePartial);csvRows=parsed.rows;
  const header=$('headers').checked;const width=Math.max(0,...csvRows.map(row=>row.length));
  columnNames=Array.from({length:width},(_,i)=>header?(csvRows[0]?.[i]||'Column '+columnLetter(i)):'Column '+columnLetter(i));
  const dataRows=csvRows.slice(header?1:0);
  gridRows=dataRows.slice(0,MAX_ROWS).map((values,index)=>({values,index:index+(header?2:1)}));
  $('textPanel').hidden=true;$('csvPanel').hidden=false;pageIndex=0;sortColumn=-1;
  let message=baseMessage+' CSV grid: '+gridRows.length+' loaded data rows.';
  if(parsed.limited||dataRows.length>MAX_ROWS)message+=' Partial row preview: row limit reached.';
  if(sourcePartial)message+=' Any unfinished trailing record was omitted.';
  if(gridRows.some(row=>row.values.length!==width))message+=' Uneven row widths; missing cells are displayed empty.';
  status(message);renderGrid();
 }catch(error){$('csvPanel').hidden=true;showText();status(baseMessage+' '+error.message+' Raw text shown.');}
}
function setZoom(value){zoom=value;const image=$('image');image.classList.remove('fit');image.style.width=(image.naturalWidth*zoom)+'px';image.style.height='auto';$('zoomValue').textContent=Math.round(zoom*100)+'%';}
function fitImage(){zoom=1;$('image').classList.add('fit');$('image').style.width='';$('image').style.height='';$('zoomValue').textContent='Fit';}
async function load(){
 const token=++requestNumber;controller?.abort();controller=new AbortController();releaseImage();
 for(const id of ['csvPanel','textPanel','imagePanel'])$(id).hidden=true;
 $('download').hidden=true;$('filter').value='';$('cellValue').value='';status('Loading preview...','loading');
 try{
  const selected=selection();$('name').textContent=selected.file;$('identity').textContent='Run '+selected.run;$('back').href='/results#'+encodeURIComponent(selected.run);
  $('download').href=selected.url;$('download').download=selected.file.split('/').pop();$('download').hidden=false;
  extension=selected.file.split('.').pop().toLowerCase();const imageType=Object.hasOwn(imageTypes,extension)?imageTypes[extension]:null;
  if(!imageType&&!textTypes.has(extension)){status('No inline decoder for this binary artifact. Use Download original, or open its generated text/JSON diagnostic report.');return;}
  const preview=await fetchPreview(selected.url,imageType?IMAGE_BYTES:TEXT_BYTES,controller.signal);if(token!==requestNumber)return;
  baseMessage=(preview.partial?'Partial preview':'Complete preview')+' · '+preview.bytes.length+' bytes'+(preview.total===null?'':' of '+preview.total)+' · original retained on tester.';
  if(imageType){
   if(preview.partial)throw Error('Image exceeds the 16 MiB preview limit. Download original is available; no partial image was decoded.');
   $('imagePanel').hidden=false;objectUrl=URL.createObjectURL(new Blob([preview.bytes],{type:imageType}));
   const image=$('image');image.onload=()=>{if(token!==requestNumber)return;$('dimensions').textContent=image.naturalWidth+' × '+image.naturalHeight+' pixels';fitImage();status(baseMessage);loadImageContext(selected,controller.signal);};image.onerror=()=>{if(token!==requestNumber)return;releaseImage();status('Image could not be decoded. Original artifact is unchanged.','error');};image.src=objectUrl;
  }else{
   sourceText=new TextDecoder('utf-8').decode(preview.bytes);sourcePartial=preview.partial;
   if(extension==='csv'||extension==='tsv'){$('delimiter').value=extension==='tsv'?'tab':',';showCsv();}else showText();
  }
 }catch(error){if(token===requestNumber&&error.name!=='AbortError')status(error.message,'error');}
}
$('reload').onclick=load;$('filter').oninput=()=>{pageIndex=0;renderGrid();};$('headers').onchange=showCsv;$('delimiter').onchange=showCsv;$('rawNumbers').onchange=renderGrid;
$('previous').onclick=()=>{pageIndex--;renderGrid();};$('next').onclick=()=>{pageIndex++;renderGrid();};$('pretty').onchange=showText;$('wrap').onchange=showText;
$('copy').onclick=async()=>{try{await navigator.clipboard.writeText($('text').textContent);$('copyStatus').textContent='Copied displayed text.';}catch{const range=document.createRange();range.selectNodeContents($('text'));const selection=window.getSelection();selection.removeAllRanges();selection.addRange(range);$('copyStatus').textContent='Text selected; press Ctrl+C to copy.';}};
$('fit').onclick=fitImage;$('actual').onclick=()=>setZoom(1);$('zoomIn').onclick=()=>setZoom(Math.min(8,zoom*1.25));$('zoomOut').onclick=()=>setZoom(Math.max(.1,zoom/1.25));
window.addEventListener('pagehide',()=>{controller?.abort();releaseImage();});load();
</script></body></html>
""";
}
