"""Offline UI fixture checks. Does not run the C# server, QMP, or xemu.

Requires Python Playwright and Node.js. Use --browser to select Chromium,
or install the Playwright browser separately. No test dependency is added to
normal runner publishing. Detailed artifact viewers are tested by check-viewers.py.
"""
from pathlib import Path
import argparse
import re
import subprocess
import tempfile
from playwright.sync_api import sync_playwright

parser = argparse.ArgumentParser()
parser.add_argument("--browser", help="Path to a Chromium executable")
parser.add_argument("--screenshot", help="Optional evidence-page screenshot output path")
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
networking = root / "src/XemuTestRunner/Networking"
raw = re.findall(r'"""\n(.*?)\n"""', (networking / "WebPages.cs").read_text(), re.S)
assert len(raw) == 6, "Update the fixture extractor when WebPages changes structure."
style, stats, common, control_a, control_b, control_script = raw
header = '<header><h1>Runner fixture</h1><nav><a href="/">Home</a><a href="/control">Test console</a><a href="/results">Evidence</a></nav><span id="phase"></span></header>'

def html_page(body: str, script: str) -> str:
    return '<!doctype html><html><head><meta charset="utf-8">' + style + '</head><body>' + header + '<main class="wrap"><div id="error"></div>' + body + '</main><script>' + script + '</script></body></html>'

home = html_page(stats, 'const refreshMs=500;' + common + 'refresh();')
control = html_page(control_a + stats + control_b, 'const refreshMs=500;const previewMs=750;const previewAllowed=true;' + common + control_script)
evidence = re.findall(r'"""\n(.*?)\n"""', (networking / "EvidencePage.cs").read_text(), re.S)[0]
diagnostics = re.findall(r'"""\n(.*?)\n"""', (networking / "DiagnosticsPage.cs").read_text(), re.S)[0]
viewer = re.findall(r'"""\n(.*?)\n"""', (networking / "ArtifactViewerPage.cs").read_text(), re.S)[0]
with tempfile.TemporaryDirectory(prefix="runner-ui-check-") as directory:
    for name, html in [("home", home), ("control", control), ("evidence", evidence), ("diagnostics", diagnostics), ("viewer", viewer)]:
        script = Path(directory) / (name + ".js")
        script.write_text(re.search(r'<script>(.*?)</script>', html, re.S)[1])
        subprocess.run(["node", "--check", str(script)], check=True)

state = {
    'CurrentJob': 'build-fixture', 'RunId': 'run-1', 'Phase': 'running', 'ProcessId': 123,
    'JobStartedUtc': '2026-09-21T09:30:00Z', 'Queue': {'Pending': 2, 'Testing': 1, 'Tested': 4},
    'LastJob': 'build-old', 'LastResult': 'completed', 'UptimeMs': 12000,
    'LatestMetric': {'TimestampUtc': '2026-09-21T09:30:00Z', 'HostCpuPercent': 32.5,
                     'ProcessCpuPercent': 101.2, 'HostMemoryUsedBytes': 1024**3,
                     'HostMemoryTotalBytes': 16*1024**3, 'CollectorDurationMs': 1.1557,
                     'CollectorDutyPercent': 0.23113999999999998}}
fixtures = {
    '/api/v1/status': state,
    '/api/v1/quality': {'RunId': 'run-1', 'Activity': {'Intervened': True, 'ManualInputs': 1,
        'Pauses': 0, 'Screenshots': 0, 'PreviewCaptures': 2, 'BulkTransfers': 0}},
    '/api/v1/control': {'Active': True, 'InputProvider': 'fixture', 'InputAvailable': True},
    '/api/v1/runs': [{'RunId': 'run-1', 'Result': {'job': 'build-fixture',
        'status': 'completed', 'comparisonStatus': 'operator_intervened',
        'workload': {'Measurements': [{'Name':'duration','Value':1.1557,'Unit':'ms'}]}}}],
    '/api/v1/runs/run-1': {'outcome': {'execution':'completed','correctness':'passed','evidence':'complete','comparison':'ineligible'}, 'reasons':['operator_intervened']},
    '/api/v1/runs/run-1/artifacts': {'items': [
        {'path': 'result.json', 'bytes': 123}, {'path': 'stdout.log', 'bytes': 5000000}], 'nextCursor': None, 'complete': True, 'excluded': 0},
    '/api/v1/runs/run-1/tail': {'Offset': 4999996, 'Bytes': 4, 'FileBytes': 5000000, 'Text': 'END!'},
    '/api/v1/diagnostics/tools': [
        {'Name':'perf','Available':True,'ResolvedPath':'/usr/bin/perf','Detail':'Available.'},
        {'Name':'RenderDoc Python','Available':False,'ResolvedPath':None,'Detail':'module unavailable'}
    ],
    '/api/v1/diagnostics/recipes': [{'Id':'cpu-window','Type':'perf','DurationMs':30000}],
    '/api/v1/diagnostics': {'Active':True,'RunId':'run-1','CurrentDiagnostic':None,'StartedUtc':None,'Completed':[]}}

with sync_playwright() as playwright:
    options = {'headless': True}
    if args.browser:
        options['executable_path'] = args.browser
    browser = playwright.chromium.launch(**options)
    errors = []

    def fixture_page(html: str):
        page = browser.new_page(viewport={'width': 1400, 'height': 1100})
        page.on('pageerror', lambda error: errors.append(str(error)))
        page.evaluate("""fixtures => {
          const record={Recording:false,Plan:[],SavedFile:null}; window.testPosts=[]; window.testGets=[];
          window.fetch=async(url,options={})=>{
            const path=String(url).split('?')[0];
            if(options.method==='POST'){
              window.testPosts.push(path);
              if(path.endsWith('/start'))record.Recording=true;
              if(path==='/api/v1/input/press')record.Plan.push({Type:'button',Button:JSON.parse(options.body).Button,DurationMs:100});
            } else window.testGets.push(path);
            if(path==='/api/v1/preview'){
              const png=Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jYhUAAAAASUVORK5CYII='),c=>c.charCodeAt(0));
              return new Response(png,{headers:{'Content-Type':'image/png','X-Run-Id':'run-1','X-Captured-Utc':'fixture'}});
            }
            const data=fixtures[path]??(path.includes('/record')?record:{accepted:true});
            return new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});
          };
        }""", fixtures)
        page.set_content(html)
        return page

    page = fixture_page(home)
    page.wait_for_function("document.getElementById('job').textContent==='build-fixture'")
    assert page.locator('#stats .card').count() == 12
    assert 'Operator-intervened' in page.locator('#quality').inner_text()
    assert '1.16 ms / 0.23% duty' in page.locator('#stats').inner_text()
    page.close()
    page = fixture_page(control)
    page.wait_for_function("document.getElementById('keys').children.length===24")
    page.locator('#record').click()
    page.locator('#keys button').first.click()
    page.wait_for_function("document.getElementById('plan').value.includes('button')")
    assert '/api/v1/input/press' in page.evaluate('window.testPosts')
    page.wait_for_function("document.getElementById('preview').naturalWidth===1")
    page.close()
    page = fixture_page(evidence)
    page.wait_for_selector('#runs td')
    page.locator('#runs td').first.click()
    page.wait_for_function("document.querySelectorAll('#artifacts a').length===2")
    assert 'duration=1.16 ms' in page.locator('#summary').inner_text()
    assert '/results/view?' in page.locator('#artifacts a').first.get_attribute('href')
    assert not any('/tail' in path or '/artifacts/' in path for path in page.evaluate('window.testGets'))
    page.locator('#tail').click()
    page.wait_for_function("document.getElementById('text').textContent.includes('END!')")
    page.locator('#artifactFilter').fill('result.json')
    assert page.locator('#artifacts a').count() == 1
    if args.screenshot:
        page.screenshot(path=args.screenshot)
    page.close()
    page = fixture_page(diagnostics)
    page.wait_for_function("document.querySelectorAll('#tools tr').length===2")
    page.wait_for_function("document.querySelectorAll('#recipes tr').length===1")
    assert page.locator('#viewRun').get_attribute('href') == '/results#run-1'
    page.locator('#recipes button').click()
    page.wait_for_function("window.testPosts.includes('/api/v1/diagnostics/run')")
    assert 'RenderDoc Python' in page.locator('#tools').inner_text()
    page.close()
    assert not errors, errors
    browser.close()
print('PASS: embedded JavaScript syntax, two-decimal telemetry, evidence viewer links, optional tails, input, recorder, preview and diagnostics. No C# server or xemu was executed.')
