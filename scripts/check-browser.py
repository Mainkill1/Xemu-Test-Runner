"""Offline UI fixture checks. Does not run the C# server, QMP, or xemu.

Requires Python Playwright and Node.js. Use --browser to select Chromium,
or install the Playwright browser separately. No test dependency is added to
normal runner publishing.
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
with tempfile.TemporaryDirectory(prefix="runner-ui-check-") as directory:
    for name, html in [("home", home), ("control", control), ("evidence", evidence)]:
        script = Path(directory) / (name + ".js")
        script.write_text(re.search(r'<script>(.*?)</script>', html, re.S)[1])
        subprocess.run(["node", "--check", str(script)], check=True)

state = {
    'CurrentJob': 'build-fixture', 'RunId': 'run-1', 'Phase': 'running', 'ProcessId': 123,
    'JobStartedUtc': '2026-09-21T09:30:00Z', 'Queue': {'Pending': 2, 'Testing': 1, 'Tested': 4},
    'LastJob': 'build-old', 'LastResult': 'completed', 'UptimeMs': 12000,
    'LatestMetric': {'TimestampUtc': '2026-09-21T09:30:00Z', 'HostCpuPercent': 32.5,
                     'ProcessCpuPercent': 101.2, 'HostMemoryUsedBytes': 1024**3,
                     'HostMemoryTotalBytes': 16*1024**3, 'CollectorDurationMs': 1.4}}
fixtures = {
    '/api/v1/status': state,
    '/api/v1/quality': {'RunId': 'run-1', 'Activity': {'Intervened': True, 'ManualInputs': 1,
        'Pauses': 0, 'Screenshots': 0, 'PreviewCaptures': 2, 'BulkTransfers': 0}},
    '/api/v1/control': {'Active': True, 'InputProvider': 'fixture', 'InputAvailable': True},
    '/api/v1/runs': [{'RunId': 'run-1', 'Result': {'job': 'build-fixture',
        'status': 'completed', 'comparisonStatus': 'operator_intervened'}}],
    '/api/v1/runs/run-1': {'RunId': 'run-1', 'Artifacts': [
        {'Path': 'result.json', 'Bytes': 123}, {'Path': 'stdout.log', 'Bytes': 5000000}]},
    '/api/v1/runs/run-1/tail': {'Offset': 4999996, 'Bytes': 4, 'FileBytes': 5000000, 'Text': 'END!'}}

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
          const record={Recording:false,Plan:[],SavedFile:null}; window.testPosts=[];
          window.fetch=async(url,options={})=>{
            const path=String(url).split('?')[0];
            if(options.method==='POST'){
              window.testPosts.push(path);
              if(path.endsWith('/start'))record.Recording=true;
              if(path==='/api/v1/input/press')record.Plan.push({Type:'button',Button:JSON.parse(options.body).Button,DurationMs:100});
            }
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
    page.wait_for_function("document.getElementById('text').textContent.includes('END!')")
    assert page.locator('#artifacts a').count() == 2
    if args.screenshot:
        page.screenshot(path=args.screenshot)
    assert not errors, errors
    browser.close()
print('PASS: JavaScript syntax and offline browser fixtures for stats, quality, input, recorder, preview display and evidence. No C# server or xemu was executed.')
