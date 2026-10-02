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
        suite = {'id': 'pilot', 'leafCount': 30, 'qualification': 'candidate',
                 'isoSha256': 'a' * 64, 'catalogId': 'sha256:' + 'b' * 64}
        if path == '/api/v1/xiso-suites':
            value = {'items': [suite], 'nextOffset': None}
        elif path == '/api/v1/xiso-suites/pilot':
            value = suite
        elif path.endswith('/categories'):
            value = {
                'selectorVersion': 1,
                'idBase': 0,
                'items': [
                    {'group_id': 0, 'id': 'cpu', 'name': 'CPU and translation', 'count': 1, 'available': True},
                    {'group_id': 2, 'id': 'shaders', 'name': 'Shaders and pipelines', 'count': 6, 'available': True},
                    {'group_id': 5, 'id': 'surfaces', 'name': 'Surfaces and memory', 'count': 0, 'available': False},
                ],
            }
        elif path.endswith('/tests'):
            value = {
                'selectorVersion': 1,
                'idBase': 0,
                'items': [
                    {'test_id': 0, 'id': 'cpu.direct', 'category': 'cpu', 'freshProcess': False},
                    {'test_id': 29, 'id': 'shader_lifecycle.pipeline_train', 'category': 'shaders', 'freshProcess': True},
                ],
                'nextOffset': None,
            }
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

    def test_group_selection_submits_compact_numeric_array_without_start(self):
        self.page.locator('#categories input[value="2"]').check()
        body = self.create()
        self.assertEqual(body, {'id': 'browser-campaign', 'application': 'candidate',
                               'suite': 'pilot', 'test_groups': [2]})
        self.assertFalse(any(path.endswith('/start') for _, path, _ in self.calls))

    def test_individual_selection_submits_global_numeric_test_id(self):
        row = self.page.locator('#tests tr').filter(has_text='shader_lifecycle.pipeline_train')
        self.assertIn('Separate process', row.inner_text())
        self.assertEqual(row.locator('td').first.inner_text(), '29')
        row.locator('input').check()
        body = self.create()
        self.assertEqual(body['test_ids'], [29])
        self.assertNotIn('tests', body)
        self.assertNotIn('categories', body)

    def test_selector_ids_are_visible_and_empty_groups_are_disabled(self):
        categories = self.page.locator('#categories').inner_text()
        self.assertIn('#0 CPU and translation', categories)
        self.assertIn('#2 Shaders and pipelines', categories)
        self.assertTrue(self.page.locator('#categories input[value="5"]').is_disabled())

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
