"""Real browser interactions against an offline HTTP-contract fixture, not a live xemu rig."""
import json
from pathlib import Path
import re
import unittest

from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
HTML = re.findall(r'"""\n(.*?)\n"""', (ROOT / 'src/XemuTestRunner/Networking/TestConfigurationPage.cs').read_text(), re.S)[0]
FIXTURE = r"""() => {
  const A='a'.repeat(64), B='b'.repeat(64), C='c'.repeat(64), D='d'.repeat(64);
  window.calls=[]; window.requests={}; window.pinValue={configured:false};
  window.failSave=false; window.loseStart=false; window.delayInspect=false; window.inspections={};
  const configs=[{id:'smoke',revision:A,description:'<img src=x onerror=alert(1)>',sourceJobId:'seed',stepCount:1},
    {id:'second',revision:B,description:'Second configuration',sourceJobId:'seed',stepCount:2}];
  const json=(value,status=200)=>new Response(JSON.stringify(value),{status,headers:{'Content-Type':'application/json','X-Request-Id':'fixture-request'}});
  window.fetch=async(url,options={})=>{
    const target=new URL(String(url),'http://runner.invalid');
    const path=target.pathname, method=options.method||'GET';
    const body=options.body ? JSON.parse(options.body) : null;
    window.calls.push({path,method,body,query:target.search});
    if(path==='/api/v1/test-configs') {
      return json({items:target.searchParams.get('offset')==='1'?[configs[1]]:[configs[0]],nextOffset:target.searchParams.get('offset')==='1'?null:1});
    }
    if(path.startsWith('/api/v1/test-configs/')) {
      if(method==='POST') {
        if(window.failSave)return json({ok:false,status:400,code:'request_invalid',error:'Invalid field',hint:'Correct the field.',details:{field:'$.job.timeuotSeconds'},requestId:'fixture-request',recovery:'correct'},400);
        return json({id:path.split('/')[4],revision:A,description:body.description,sourceJobId:body.sourceJobId,stepCount:1});
      }
      const id=path.split('/')[4];
      const value={revision:id==='second'?B:A,definition:{id,sourceJobId:'seed',description:'Saved configuration',buildFiles:['xemu.bin'],job:{executable:'xemu.bin',plan:[]}}};
      if(window.delayInspect)return new Promise(resolve=>{window.inspections[id]=()=>resolve(json(value));});
      return json(value);
    }
    if(path==='/api/v1/jobs')return json({items:[{id:'application',state:'draft'},{id:'seed',state:'tested'}],nextOffset:null});
    if(path==='/api/v1/test-runs') {
      if(method==='POST') {window.requests[body.id]={...body,state:'uploaded',startRequested:false};return json(window.requests[body.id]);}
      return json({items:Object.values(window.requests),nextOffset:null});
    }
    if(path.startsWith('/api/v1/test-runs/')) {
      const id=path.split('/')[4];
      if(path.endsWith('/start')) {
        window.requests[id].state='queued';window.requests[id].startRequested=true;
        if(window.loseStart){window.loseStart=false;throw Error('Disconnected after start was stored');}
      }
      if(method==='DELETE')window.requests[id].state='cancelled';
      return window.requests[id]?json(window.requests[id]):json({code:'test_request_not_found',error:'Unknown request',hint:'Inspect the same ID.',requestId:'fixture-request'},404);
    }
    if(path==='/api/v1/builds')return json({items:[{sha256:C,reference:C.slice(0,12)},{sha256:D,reference:D.slice(0,12)}],nextOffset:null});
    if(path==='/api/v1/baseline') {
      if(method==='PUT')window.pinValue={configured:true,sha256:body.sha256,revision:'pinned-revision',runCount:3};
      return json(window.pinValue);
    }
    if(path==='/api/v1/compare')return new Response('Test | A | B | Change %\nsmoke | 100 | 80 | -20',{headers:{'Content-Type':'text/markdown'}});
    if(path.startsWith('/api/v1/build-results/')) {
      if(path.endsWith('/runs'))return json({items:[{runId:'attempt',test:'smoke',outcome:'completed',eligible:true,rawCsv:'/api/v1/runs/attempt/artifacts/metrics.csv'}],nextOffset:null});
      return new Response('Build summary: 3 qualified attempts',{headers:{'Content-Type':'text/markdown'}});
    }
    return json({code:'route_not_found',error:'Unknown fixture route',hint:path},404);
  };
}"""


class HumanWorkbenchChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.playwright = sync_playwright().start()
        cls.browser = cls.playwright.chromium.launch(headless=True)

    @classmethod
    def tearDownClass(cls):
        cls.browser.close()
        cls.playwright.stop()

    def setUp(self):
        self.page = self.browser.new_page(viewport={'width': 1280, 'height': 1000})
        self.page.set_default_timeout(2500)
        self.errors = []
        self.page.on('pageerror', lambda error: self.errors.append(str(error)))
        self.page.evaluate(FIXTURE)
        self.page.set_content(HTML)
        self.page.wait_for_selector('#config-rows tr')
        self.page.wait_for_selector('#application-select option[value="application"]', state='attached')

    def tearDown(self):
        try:
            self.assertFalse(self.errors, self.errors)
        finally:
            self.page.close()

    def select_request(self):
        self.page.locator('#config-select').select_option('smoke@' + 'a' * 64)
        self.page.locator('#application-select').select_option('application')
        self.page.locator('#request-id').fill('human-fixture')
        self.page.locator('#select-request').click()
        self.page.wait_for_function("window.requests['human-fixture']?.state==='uploaded'")
        self.page.wait_for_selector('#requests button[data-kind="start"]')

    def test_selection_is_not_start_and_keeps_full_identity(self):
        self.select_request()
        calls = self.page.evaluate('window.calls')
        self.assertFalse(any(c['path'].endswith('/start') for c in calls))
        selection = next(c for c in calls if c['method'] == 'POST' and c['path'] == '/api/v1/test-runs')
        self.assertEqual('a' * 64, selection['body']['revision'])
        expect(self.page.locator('#requests')).to_contain_text('Not started')

    def test_start_requires_confirmation(self):
        self.select_request()
        self.page.once('dialog', lambda dialog: dialog.dismiss())
        self.page.locator('#requests button[data-kind="start"]').click()
        self.assertEqual('uploaded', self.page.evaluate("window.requests['human-fixture'].state"))
        self.page.once('dialog', lambda dialog: dialog.accept())
        self.page.locator('#requests button[data-kind="start"]').click()
        self.page.wait_for_function("window.requests['human-fixture'].state==='queued'")
        self.assertEqual(1, self.page.evaluate("window.calls.filter(c=>c.path.endsWith('/start')).length"))

    def test_lost_start_response_recovers_same_id_without_retry(self):
        self.select_request()
        self.page.evaluate('window.loseStart=true')
        self.page.once('dialog', lambda dialog: dialog.accept())
        self.page.locator('#requests button[data-kind="start"]').click()
        expect(self.page.locator('#error')).to_be_visible()
        self.assertEqual('human-fixture', self.page.locator('#request-id').input_value())
        self.page.locator('#recover-request').click()
        expect(self.page.locator('#requests')).to_contain_text('Queued')
        self.assertEqual(1, self.page.evaluate("window.calls.filter(c=>c.path.endsWith('/start')).length"))

    def test_config_save_retains_input_and_shows_field_error(self):
        self.page.evaluate('window.failSave=true')
        self.page.locator('#name').fill('new-config')
        self.page.locator('#source').fill('seed')
        text='{"executable":"xemu.bin","timeuotSeconds":5}'
        self.page.locator('#json').fill(text)
        self.page.locator('#save').click()
        expect(self.page.locator('#error')).to_contain_text('timeuotSeconds')
        self.assertEqual(text, self.page.locator('#json').input_value())
        self.assertEqual('new-config', self.page.locator('#name').input_value())
        self.assertEqual('error', self.page.evaluate('document.activeElement.id'))
        self.assertFalse(self.page.evaluate("window.calls.some(c=>c.path.endsWith('/start'))"))

    def test_inspection_ignores_out_of_order_responses(self):
        self.page.locator('#config-more').click()
        self.page.wait_for_function("document.querySelectorAll('#config-rows tr').length===2")
        self.page.evaluate('window.delayInspect=true')
        self.page.locator('#config-rows button').nth(0).click()
        self.page.wait_for_function("typeof window.inspections.smoke==='function'")
        self.page.locator('#config-rows button').nth(1).click()
        self.page.wait_for_function("typeof window.inspections.second==='function'")
        self.page.evaluate('window.inspections.second()')
        expect(self.page.locator('#selected')).to_contain_text('second')
        self.page.evaluate('window.inspections.smoke()')
        expect(self.page.locator('#selected')).to_contain_text('second')

    def test_catalog_pagination_search_and_mobile_text_safety(self):
        self.page.set_viewport_size({'width': 390, 'height': 844})
        self.page.locator('#config-more').click()
        self.page.wait_for_function("document.querySelectorAll('#config-rows tr').length===2")
        self.page.locator('#config-search').fill('second')
        expect(self.page.locator('#config-rows tr:visible')).to_have_count(1)
        self.page.locator('#config-search').fill('')
        self.assertEqual(0, self.page.locator('#config-rows img').count())
        self.assertTrue(self.page.evaluate('document.documentElement.scrollWidth<=window.innerWidth+1'))

    def test_baseline_pin_is_confirmed_and_full_hash_is_sent(self):
        self.page.locator('#build-b').select_option('d' * 64)
        self.page.once('dialog', lambda dialog: dialog.dismiss())
        self.page.locator('#pin').click()
        self.assertFalse(self.page.evaluate('window.pinValue.configured'))
        self.page.once('dialog', lambda dialog: dialog.accept())
        self.page.locator('#pin').click()
        self.page.wait_for_function('window.pinValue.configured===true')
        self.assertEqual('d' * 64, self.page.evaluate('window.pinValue.sha256'))

    def test_comparison_is_server_generated_and_raw_csv_is_secondary(self):
        self.page.locator('#build-b').select_option('d' * 64)
        self.page.locator('#compare').click()
        expect(self.page.locator('#result')).to_contain_text('-20')
        self.assertFalse(self.page.evaluate("window.calls.some(c=>c.path.includes('/artifacts/'))"))
        self.page.locator('#raw-toggle').click()
        expect(self.page.locator('#raw-runs a')).to_have_count(1)
        self.assertTrue(self.page.locator('#raw-runs a').get_attribute('href').endswith('metrics.csv'))

    def test_duplicate_start_is_disabled_while_reply_is_pending(self):
        self.select_request()
        self.page.evaluate("""() => {const original=window.fetch;window.fetch=async(...args)=>{
          if(String(args[0]).endsWith('/start'))return new Promise(resolve=>{window.releaseStart=async()=>resolve(await original(...args));});
          return original(...args);};}""")
        self.page.once('dialog', lambda dialog: dialog.accept())
        self.page.locator('#requests button[data-kind="start"]').click()
        expect(self.page.locator('#requests button[data-kind="start"]')).to_be_disabled()
        self.page.evaluate('window.releaseStart()')
        self.page.wait_for_function("window.requests['human-fixture'].state==='queued'")

    def test_refresh_does_not_replace_unsaved_editor_text(self):
        self.page.locator('#json').fill('unsaved work')
        self.page.locator('#config-refresh').click()
        expect(self.page.locator('#config-rows tr')).to_have_count(1)
        self.assertEqual('unsaved work', self.page.locator('#json').input_value())


if __name__ == '__main__':
    unittest.main()
