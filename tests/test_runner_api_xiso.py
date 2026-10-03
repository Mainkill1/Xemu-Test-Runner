"""XISO requests stay small; the runner owns planning, defaults and waiting."""
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
from urllib.parse import urlsplit
from test_runner_api import fixture

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts' / 'runner_xiso.py'


def responder():
    polls = 0
    def respond(method, path, body, headers):
        nonlocal polls
        route = urlsplit(path).path
        if route == '/api/v1/help':
            return 200, {'capability': 'xisoCampaigns'}, {}
        if route == '/api/v1/xiso-campaigns' and method == 'POST':
            return 200, {'id': body['id'], 'startRequested': False, 'state': 'uploaded', 'revision': 'a' * 64}, {}
        if route.endswith('/start'):
            return 202, {'state': 'queued', 'startRequested': True}, {}
        if route.endswith('/wait'):
            polls += 1
            done = polls >= 3
            return 200, {'id': 'example', 'event': 'finished' if done else 'heartbeat', 'terminal': done,
                         'state': 'passed' if done else 'running', 'next': '/api/v1/xiso-campaigns/example/wait'}, {}
        if route.endswith('/categories'):
            return 200, {'selectorVersion': 1, 'idBase': 0, 'catalogId': 'sha256:' + 'b' * 64,
                         'items': [{'id': 'shaders', 'name': 'Shaders and pipelines', 'count': 6}]}, {}
        return 404, {'code': 'unexpected', 'error': path}, {}
    return respond


class XisoClientChecks(unittest.TestCase):
    def invoke(self, origin, *args):
        return subprocess.run([sys.executable, str(SCRIPT), *args],
            env={**os.environ, 'XEMU_RUNNER_URL': origin, 'SSH_CONNECTION': ''}, capture_output=True, text=True, timeout=20)

    def test_category_request_omits_unused_settings_and_never_starts(self):
        with fixture(responder()) as (origin, calls):
            result = self.invoke(origin, 'select', 'build-a', '--id', 'example', '--category', 'shaders')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        body = next(body for method, path, body, _ in calls if method == 'POST')
        self.assertEqual(body, {'id': 'example', 'application': 'build-a', 'categories': ['shaders']})
        self.assertLess(len(json.dumps(body)), 150)
        self.assertFalse(any('/start' in path for _, path, _, _ in calls))

    def test_individual_selection_and_start_are_explicit(self):
        with fixture(responder()) as (origin, calls):
            result = self.invoke(origin, 'select', 'build-a', '--id', 'example', '--suite', 'pilot',
                                 '--test', 'shader_lifecycle.pipeline_train', '--start')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        body = next(body for method, path, body, _ in calls if path == '/api/v1/xiso-campaigns' and method == 'POST')
        self.assertEqual(body['tests'], ['shader_lifecycle.pipeline_train'])
        self.assertEqual(body['suite'], 'pilot')
        self.assertEqual(sum(path.endswith('/start') for _, path, _, _ in calls), 1)

    def test_application_configuration_source_is_explicit(self):
        with fixture(responder()) as (origin, calls):
            result = self.invoke(origin, 'select', 'build-a', '--id', 'example', '--category', 'cpu',
                                 '--configuration-source', 'application')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        body = next(body for method, path, body, _ in calls if method == 'POST')
        self.assertEqual(body['configurationSource'], 'application')

    def test_wait_follows_heartbeats_quietly_without_timer_option(self):
        with fixture(responder()) as (origin, calls):
            result = self.invoke(origin, 'wait', 'example')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(result.stdout.strip().splitlines()), 1)
        self.assertEqual(json.loads(result.stdout)['event'], 'finished')
        self.assertEqual(sum(path.endswith('/wait') for _, path, _, _ in calls), 3)
        self.assertTrue(all(method == 'GET' for method, _, _, _ in calls))

    def test_wait_help_does_not_ask_for_time_estimates(self):
        result = subprocess.run([sys.executable, str(SCRIPT), 'wait', '--help'], capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0)
        for option in ('--timeout', '--max-wait', '--interval', '--follow'):
            self.assertNotIn(option, result.stdout)
        self.assertIn('--updates', result.stdout)

    def test_optional_work_override_is_only_supplied_when_requested(self):
        with fixture(responder()) as (origin, calls):
            result = self.invoke(origin, 'select', 'build-a', '--id', 'example', '--category', 'cpu', '--multiplier', '4')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        body = next(body for method, path, body, _ in calls if method == 'POST')
        self.assertEqual(body['settings'], {'measurement_iterations_multiplier': 4})

    def test_paired_default_leaves_full_selection_to_the_runner(self):
        with fixture(responder()) as (origin, calls):
            result = self.invoke(origin, 'select', 'build-b', '--id', 'example', '--suite', 'pilot', '--reference', 'build-a')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        body = next(body for method, path, body, _ in calls if method == 'POST')
        self.assertEqual(body, {'id': 'example', 'application': 'build-b', 'suite': 'pilot', 'referenceApplication': 'build-a'})
        self.assertFalse(any('/start' in path for _, path, _, _ in calls))

    def test_report_returns_pr_ready_markdown_without_starting_work(self):
        def respond(method, path, body, headers):
            if urlsplit(path).path == '/api/v1/help':
                return 200, {'capability': 'xisoCampaigns'}, {}
            if urlsplit(path).path == '/api/v1/xiso-campaigns/example/report':
                return 200, b'## XISO campaign example\n| cpu.direct | +2.00% |\n', {'Content-Type': 'text/markdown'}
            return 404, {'code': 'unexpected', 'error': path}, {}
        with fixture(respond) as (origin, calls):
            result = self.invoke(origin, 'report', 'example', '--format', 'markdown')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('cpu.direct', result.stdout)
        self.assertTrue(all(method == 'GET' for method, _, _, _ in calls))

    def test_cross_host_plan_check_rejects_different_iso(self):
        def plan(iso):
            def respond(method, path, body, headers):
                if urlsplit(path).path == '/api/v1/help':
                    return 200, {'capability': 'xisoCampaigns'}, {}
                if urlsplit(path).path == '/api/v1/xiso-campaigns/example':
                    return 200, {'isoSha256': iso, 'catalogId': 'catalog', 'mode': 'full',
                                 'settings': {'warmup_iterations': 0},
                                 'configurationSource': 'suite', 'configurationPath': 'xemu.toml',
                                 'tests': ['cpu.direct'],
                                 'addedDependencies': [], 'chunks': [{'tests': ['cpu.direct'], 'categories': ['cpu']}],
                                 'attempts': [{'label': 'A1', 'variant': 'reference', 'chunk': 1,
                                               'configurationSha256': 'c' * 64}]}, {}
                return 404, {'code': 'unexpected', 'error': path}, {}
            return respond
        with fixture(plan('a' * 64)) as (first, _), fixture(plan('b' * 64)) as (second, _):
            result = self.invoke(first, 'check-pair', 'example', '--other-url', second)
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn('isoSha256', json.loads(result.stdout)['mismatches'])

    def test_updates_are_opt_in_short_json_lines(self):
        with fixture(responder()) as (origin, calls):
            result = self.invoke(origin, 'wait', 'example', '--updates')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        values = [json.loads(line) for line in result.stdout.strip().splitlines()]
        self.assertEqual([value['event'] for value in values], ['heartbeat', 'heartbeat', 'finished'])
        self.assertEqual(result.stderr, '')
