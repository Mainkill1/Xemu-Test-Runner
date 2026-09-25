#!/usr/bin/env python3
"""Browser contract checks for optional settings and explicit XISO execution."""
import json
from pathlib import Path
import unittest
from urllib.parse import urlsplit
from playwright.sync_api import sync_playwright

SOURCE = Path(__file__).resolve().parents[1] / 'src/XemuTestRunner/Networking/XisoPage.cs'
HTML = SOURCE.read_text(encoding='utf-8').split('"""')[1]


class XisoViewerChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.playwright = sync_playwright().start()
        cls.browser = cls.playwright.chromium.launch(headless=True)

    @classmethod
    def tearDownClass(cls):
        cls.browser.close()
        cls.playwright.stop()

    def setUp(self):
        self.calls = []
        self.page = self.browser.new_page()
        self.page.set_default_timeout(5000)
        self.page.route('**/*', self.route)
        self.page.goto('http://runner/xiso')
        self.page.locator('#categories input').first.wait_for()
        self.page.locator('#tests tr').first.wait_for()

    def tearDown(self):
        self.page.close()

    def route(self, route):
        request = route.request
        path = urlsplit(request.url).path
        body = request.post_data_json if request.post_data else None
        self.calls.append((request.method, path, body))
        if path == '/xiso':
            route.fulfill(status=200, body=HTML, content_type='text/html')
            return
        suite = {'id': 'pilot', 'leafCount': 7, 'qualification': 'candidate',
                 'isoSha256': 'a' * 64, 'catalogId': 'sha256:' + 'b' * 64}
        if path == '/api/v1/xiso-suites':
            value = {'items': [suite], 'nextOffset': None}
        elif path == '/api/v1/xiso-suites/pilot':
            value = suite
        elif path.endswith('/categories'):
            value = {'items': [{'id': 'cpu', 'name': 'CPU and translation', 'count': 1},
                               {'id': 'shaders', 'name': 'Shaders and pipelines', 'count': 6}]}
        elif path.endswith('/tests'):
            value = {'items': [{'id': 'cpu.direct', 'category': 'cpu', 'freshProcess': False},
                               {'id': 'shader_lifecycle.pipeline_train', 'category': 'shaders', 'freshProcess': True}], 'nextOffset': None}
        elif path == '/api/v1/xiso-campaigns':
            value = {'id': body['id'], 'state': 'uploaded', 'startRequested': False}
        elif path.endswith('/start'):
            value = {'state': 'queued', 'startRequested': True}
        else:
            value = {'state': 'uploaded'}
        route.fulfill(status=200, body=json.dumps(value), content_type='application/json')

    def create(self):
        self.page.locator('#application').fill('candidate')
        self.page.locator('#campaign').fill('browser-campaign')
        self.page.locator('#create').click()
        self.page.wait_for_function("document.getElementById('result').textContent.includes('uploaded')")
        return next(body for method, path, body in self.calls if method == 'POST' and path == '/api/v1/xiso-campaigns')

    def test_category_selection_does_not_submit_unused_settings_or_start(self):
        self.page.locator('#categories input[value=shaders]').check()
        body = self.create()
        self.assertEqual(body, {'id': 'browser-campaign', 'application': 'candidate', 'suite': 'pilot', 'categories': ['shaders']})
        self.assertFalse(any(path.endswith('/start') for _, path, _ in self.calls))

    def test_individual_selection_exposes_fresh_process_isolation(self):
        row = self.page.locator('#tests tr').filter(has_text='shader_lifecycle.pipeline_train')
        self.assertIn('Separate process', row.inner_text())
        row.locator('input').check()
        body = self.create()
        self.assertEqual(body['tests'], ['shader_lifecycle.pipeline_train'])
        self.assertNotIn('categories', body)

    def test_start_is_a_separate_intent_after_creation(self):
        self.create()
        self.assertFalse(any(path.endswith('/start') for _, path, _ in self.calls))
        self.page.locator('#start').click()
        self.page.wait_for_function("document.getElementById('result').textContent.includes('queued')")
        self.assertEqual(sum(path.endswith('/start') for _, path, _ in self.calls), 1)

    def test_optional_overrides_and_candidate_identity_are_visible(self):
        self.assertIn('candidate', self.page.locator('#identity').inner_text())
        self.page.locator('summary').click()
        self.page.locator('#multiplier').fill('4')
        body = self.create()
        self.assertEqual(body['settings'], {'measurement_iterations_multiplier': 4})


if __name__ == '__main__':
    unittest.main(verbosity=2)
