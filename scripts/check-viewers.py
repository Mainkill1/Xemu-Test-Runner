"""Exercise embedded viewers in Chromium without launching xemu or a C# listener.

Artifact requests use the same ranged download URLs as production. Fixtures
cover rendering, bounds, hostile content, original precision and explicit export.
"""
from pathlib import Path
import base64
import re
import unittest
from urllib.parse import quote, urlsplit
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
UI = ROOT / 'src/XemuTestRunner/Networking'
PNG = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jYhUAAAAASUVORK5CYII=')


def embedded(name):
    path = UI / name
    if not path.is_file():
        raise AssertionError('Missing embedded viewer: ' + name)
    return re.findall(r'"""\n(.*?)\n"""', path.read_text(encoding='utf-8'), re.S)[0]


class ViewerChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.playwright = sync_playwright().start()
        cls.browser = cls.playwright.chromium.launch()

    @classmethod
    def tearDownClass(cls):
        cls.browser.close()
        cls.playwright.stop()

    def setUp(self):
        self.context = self.browser.new_context(viewport={'width': 1440, 'height': 1000})
        self.page = self.context.new_page()
        self.errors, self.calls, self.downloads = [], [], []
        self.page.on('pageerror', lambda error: self.errors.append(str(error)))
        self.page.on('download', lambda download: self.downloads.append(download))
        self.addCleanup(self.context.close)

    def open(self, filename, content, blocked=False, ignore_range=False):
        document = embedded('ArtifactViewerPage.cs')
        content = content.encode() if isinstance(content, str) else content
        def route(request):
            url = urlsplit(request.request.url)
            self.calls.append((request.request.method, url.path, request.request.headers))
            if url.path == '/results/view':
                return request.fulfill(body=document, content_type='text/html')
            if url.path.startswith('/api/v1/runs/run-1/artifacts/'):
                if blocked:
                    return request.fulfill(status=409, json={'code':'operation_blocked', 'error':'Benchmark transfer policy blocks preview.'})
                headers = {'Accept-Ranges':'bytes', 'Content-Disposition':'attachment; filename="fixture"'}
                value = request.request.headers.get('range')
                if value and not ignore_range:
                    if not content:
                        # Match the existing C# FileRange contract for size zero.
                        return request.fulfill(status=416, json={
                            'code':'range_invalid', 'error':'The requested byte range cannot be satisfied.',
                            'details': {'fileLength': 0}})
                    first, last = map(int, re.fullmatch(r'bytes=(\d+)-(\d+)', value).groups())
                    last = min(last, len(content) - 1)
                    headers['Content-Range'] = f'bytes {first}-{last}/{len(content)}'
                    return request.fulfill(status=206, body=content[first:last+1], headers=headers,
                                           content_type='application/octet-stream')
                return request.fulfill(body=content, headers=headers, content_type='application/octet-stream')
            return request.fulfill(status=404, body='Not found')
        self.page.route('**/*', route)
        self.page.goto('http://runner.test/results/view?run=run-1&file=' + quote(filename, safe=''))
        self.page.wait_for_function("document.getElementById('status').dataset.state !== 'loading'")
        self.assertFalse(self.errors, self.errors)

    def test_measurement_display_uses_two_decimals(self):
        raw = re.findall(r'"""\n(.*?)\n"""', (UI / 'WebPages.cs').read_text(), re.S)
        self.page.set_content('<html><body></body></html>')
        self.page.add_script_tag(content='const refreshMs=1000;' + raw[2])
        self.assertEqual(self.page.evaluate('pct(0.23113999999999998)'), '0.23%')
        self.assertEqual(self.page.evaluate('number(1.1557)'), '1.16')
        self.assertEqual(self.page.evaluate('number(null)'), '-')
        self.assertEqual(self.page.evaluate('number(-0.00001)'), '0.00')
        self.assertIn('number(m.CollectorDurationMs)', raw[2])
        self.assertIn('pct(m.CollectorDutyPercent)', raw[2])

    def test_csv_grid_handles_quoted_fields_sort_filter_and_exact_values(self):
        self.open('metrics.csv', 'name,value,note\r\nalpha,1.1557,"quoted, field"\r\nbeta,10.231139999999998,"line one\nline two"\r\ngamma,2.3456,"say ""hello"""\r\n')
        self.assertEqual(self.page.locator('#grid tbody tr').count(), 3)
        self.assertIn('1.16', self.page.locator('#grid tbody').inner_text())
        self.assertIn('quoted, field', self.page.locator('#grid').inner_text())
        self.page.locator('#grid tbody td').nth(1).click()
        self.assertIn('1.1557', self.page.locator('#cellValue').input_value())
        self.page.locator('#grid thead button').nth(1).click()
        self.assertEqual(self.page.locator('#grid tbody tr').nth(1).locator('td').first.inner_text(), 'gamma')
        self.page.locator('#filter').fill('beta')
        self.assertEqual(self.page.locator('#grid tbody tr').count(), 1)
        self.assertIn('line two', self.page.locator('#grid').inner_text())
        self.assertEqual(self.downloads, [])
        self.assertTrue(all(method == 'GET' for method, _, _ in self.calls))

    def test_csv_paging_and_identifiers_are_not_rounded(self):
        rows = ['id,value'] + [f'{i:06},0.23113999999999998' for i in range(120)]
        self.open('metrics.csv', '\n'.join(rows))
        self.assertEqual(self.page.locator('#grid tbody tr').count(), 50)
        self.assertEqual(self.page.locator('#grid tbody td').first.inner_text(), '000000')
        self.page.locator('#next').click()
        self.assertEqual(self.page.locator('#grid tbody td').first.inner_text(), '000050')
        self.page.locator('#rawNumbers').check()
        self.assertIn('0.23113999999999998', self.page.locator('#grid tbody').inner_text())

    def test_pretty_json_preserves_large_integer_and_original_fraction(self):
        self.open('report.json', '{"id":9007199254740993,"duty":0.23113999999999998,"text":"<img src=x onerror=alert(1)>","nested":{"a":[1,2]}}')
        pretty = self.page.locator('#text').inner_text()
        self.assertIn('9007199254740993', pretty)
        self.assertIn('0.23113999999999998', pretty)
        self.assertIn('\n  "id":', pretty)
        self.assertEqual(self.page.locator('#text img').count(), 0)
        self.assertEqual(self.downloads, [])

    def test_image_viewer_supports_fit_and_zoom_without_download(self):
        self.open('screenshots/failure.png', PNG)
        self.page.wait_for_function("document.getElementById('image').naturalWidth === 1")
        self.page.locator('#zoomIn').click()
        self.assertIn('125%', self.page.locator('#zoomValue').inner_text())
        self.page.locator('#fit').click()
        self.assertIn('Fit', self.page.locator('#zoomValue').inner_text())
        self.assertEqual(self.downloads, [])
        self.assertIn('/artifacts/screenshots/failure.png', self.page.locator('#download').get_attribute('href'))

    def test_html_and_formulas_are_displayed_only_as_text(self):
        self.open('diagnostics/report.html', '<script>window.compromised=true</script><img src=x onerror=alert(1)>')
        self.assertIn('<script>', self.page.locator('#text').inner_text())
        self.assertIsNone(self.page.evaluate('window.compromised'))
        self.open('data.csv', 'field,value\n"=HYPERLINK(""bad"")",5\n')
        self.assertIn('=HYPERLINK("bad")', self.page.locator('#grid').inner_text())
        self.assertIsNone(self.page.evaluate('window.compromised'))

    def test_truncated_text_is_explicit_and_download_remains_optional(self):
        self.open('stdout.log', 'log line\n' * 150000)
        self.assertIn('partial', self.page.locator('#status').inner_text().lower())
        self.assertLessEqual(len(self.page.locator('#text').inner_text().encode()), 1024*1024 + 16)
        self.assertTrue(any('range' in headers for _, path, headers in self.calls if '/artifacts/' in path))
        self.assertEqual(self.downloads, [])

    def test_policy_error_is_visible_without_retry_or_fallback(self):
        self.open('metrics.csv', 'value\n1\n', blocked=True)
        self.assertIn('operation_blocked', self.page.locator('#status').inner_text())
        self.assertEqual(sum('/artifacts/' in path for _, path, _ in self.calls), 1)
        self.assertEqual(self.downloads, [])

    def test_binary_dump_is_not_fetched_or_treated_as_an_image(self):
        self.open('xemu.dmp', b'binary dump')
        self.assertIn('download', self.page.locator('#status').inner_text().lower())
        self.assertEqual(sum('/artifacts/' in path for _, path, _ in self.calls), 0)

    def test_empty_and_malformed_files_do_not_claim_valid_pretty_json(self):
        self.open('empty.json', '')
        self.assertIn('json', self.page.locator('#status').inner_text().lower())
        self.open('invalid.json', '{"unfinished":')
        self.assertIn('invalid', self.page.locator('#status').inner_text().lower())
        self.assertIn('unfinished', self.page.locator('#text').inner_text())

    def test_empty_log_uses_explicit_zero_length_range_receipt_without_retry(self):
        self.open('stdout.log', b'')
        self.assertEqual(self.page.locator('#status').get_attribute('data-state'), 'ready')
        self.assertIn('0 bytes of 0', self.page.locator('#status').inner_text())
        self.assertEqual(self.page.locator('#text').inner_text(), '')
        self.assertEqual(sum('/artifacts/' in path for _, path, _ in self.calls), 1)

    def test_proxy_ignoring_range_still_has_a_bounded_partial_preview(self):
        self.open('stdout.log', 'x' * (2 * 1024 * 1024), ignore_range=True)
        self.assertIn('partial', self.page.locator('#status').inner_text().lower())
        self.assertLessEqual(len(self.page.locator('#text').inner_text()), 1024*1024)
        self.assertEqual(self.downloads, [])

    def test_original_download_is_explicit_and_byte_exact(self):
        original = b'name,value\r\nexample,1.155700000000001\r\n'
        self.open('metrics.csv', original)
        self.assertEqual(self.downloads, [])
        with self.page.expect_download() as pending:
            self.page.locator('#download').click()
        download = pending.value
        self.assertEqual(Path(download.path()).read_bytes(), original)
        self.assertEqual(len(self.downloads), 1)

    def test_traversal_is_rejected_before_an_artifact_read(self):
        self.open('../outside.json', '{}')
        self.assertEqual(self.page.locator('#status').get_attribute('data-state'), 'error')
        self.assertEqual(sum('/artifacts/' in path for _, path, _ in self.calls), 0)

    def test_row_limit_remains_bounded_without_a_header(self):
        self.open('many.csv', 'value\n' + '1.1557\n' * 10001)
        self.page.locator('#headers').uncheck()
        self.assertIn('10000 matching loaded rows', self.page.locator('#pageInfo').inner_text())
        self.assertIn('row limit', self.page.locator('#status').inner_text())

    def test_evidence_selection_reads_once_and_missing_assessment_is_not_completed(self):
        document = embedded('EvidencePage.cs')
        def route(request):
            path = urlsplit(request.request.url).path
            self.calls.append((request.request.method, path, request.request.headers))
            if path == '/results':
                return request.fulfill(body=document, content_type='text/html')
            if path == '/api/v1/runs':
                return request.fulfill(json=[{'RunId':'run-1', 'Result':{'job':'fixture', 'status':'completed'}}])
            if path == '/api/v1/runs/run-1':
                return request.fulfill(json={'available':False, 'outcome':None, 'code':'assessment_missing'})
            if path == '/api/v1/runs/run-1/artifacts':
                return request.fulfill(json={'items':[{'path':'report.json','bytes':2}], 'nextCursor':None, 'complete':True})
            return request.fulfill(status=404)
        self.page.route('**/*', route)
        self.page.goto('http://runner.test/results')
        self.page.locator('#runs button').click()
        self.page.wait_for_selector('#artifacts a')
        self.assertEqual(sum(path == '/api/v1/runs/run-1' for _, path, _ in self.calls), 1)
        self.assertEqual(sum(path == '/api/v1/runs/run-1/artifacts' for _, path, _ in self.calls), 1)
        self.assertIn('unavailable', self.page.locator('#summary').inner_text())
        self.assertNotIn('completed', self.page.locator('#summary').inner_text())
        self.assertFalse(self.errors, self.errors)


if __name__ == '__main__':
    unittest.main(verbosity=2)
