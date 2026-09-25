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
            return 200, {'items': [{'id': 'shaders', 'name': 'Shaders and pipelines', 'count': 6}]}, {}
        return 404, {'code': 'unexpected', 'error': path}, {}
    return respond


class XisoClientChecks(unittest.TestCase):
    def invoke(self, origin, *args):
        return subprocess.run([sys.executable, str(SCRIPT), *args],
            env={**os.environ, 'XEMU_RUNNER_URL': origin}, capture_output=True, text=True, timeout=20)

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

    def test_updates_are_opt_in_short_json_lines(self):
        with fixture(responder()) as (origin, calls):
            result = self.invoke(origin, 'wait', 'example', '--updates')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        values = [json.loads(line) for line in result.stdout.strip().splitlines()]
        self.assertEqual([value['event'] for value in values], ['heartbeat', 'heartbeat', 'finished'])
        self.assertEqual(result.stderr, '')
