import importlib.util
import json
import sys
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest import mock
from types import SimpleNamespace
import zipfile

spec = importlib.util.spec_from_file_location('release_package', Path(__file__).resolve().parents[1] / 'scripts/release_package.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
sys.modules['release_package'] = module

publish_spec = importlib.util.spec_from_file_location('publish_release', Path(__file__).resolve().parents[1] / 'scripts/publish_release.py')
publish_module = importlib.util.module_from_spec(publish_spec)
publish_spec.loader.exec_module(publish_module)

class ReleaseChecks(unittest.TestCase):
    def test_identity_rejects_unsafe_or_incomplete_values(self):
        for version, sha, rid in [('..', 'a'*40, 'linux-x64'), ('0.2.0','main','linux-x64'), ('0.2.0','a'*40,'../win')]:
            with self.assertRaises(ValueError): module.identity(version, sha, rid)

    def test_draft_release_is_found_when_tag_endpoint_returns_404(self):
        draft = {
            'tag_name': 'runner-0.2.0-deadbeef',
            'target_commitish': 'deadbeef' * 5,
            'draft': True,
            'assets': []
        }
        responses = [
            SimpleNamespace(returncode=1, stderr='gh: Not Found (HTTP 404)', stdout=''),
            SimpleNamespace(returncode=0, stderr='', stdout=json.dumps([draft]))
        ]
        with mock.patch.object(publish_module, 'gh', side_effect=responses) as gh:
            release = publish_module.read_release('Mainkill1/Xemu-Test-Runner', draft['tag_name'])
        self.assertEqual(release, draft)
        self.assertEqual(
            gh.call_args_list[1],
            mock.call('api', 'repos/Mainkill1/Xemu-Test-Runner/releases?per_page=100', check=False))

    def test_wait_release_retries_until_draft_becomes_visible(self):
        draft = {
            'tag_name': 'runner-0.2.0-deadbeef',
            'target_commitish': 'deadbeef' * 5,
            'draft': True,
            'assets': []
        }
        with mock.patch.object(
                publish_module, 'read_release',
                side_effect=[None, None, draft]) as read_release, \
             mock.patch('time.sleep') as sleep:
            release = publish_module.wait_release(
                'Mainkill1/Xemu-Test-Runner',
                draft['tag_name'],
                attempts=3,
                delay=0.01)
        self.assertEqual(release, draft)
        self.assertEqual(read_release.call_count, 3)
        self.assertEqual(sleep.call_count, 2)

    def test_wait_release_can_require_converged_asset_state(self):
        partial = {'draft': True, 'assets': []}
        complete = {'draft': True, 'assets': [{'name': 'runner.zip'}]}
        with mock.patch.object(
                publish_module, 'read_release',
                side_effect=[partial, complete]), \
             mock.patch('time.sleep'):
            release = publish_module.wait_release(
                'Mainkill1/Xemu-Test-Runner',
                'runner-0.2.0-deadbeef',
                predicate=lambda value: len(value['assets']) == 1,
                attempts=2,
                delay=0.01)
        self.assertEqual(release, complete)

    def test_package_requires_both_runner_and_helper(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary); published=root/'published';published.mkdir()
            (published/'XemuTestRunner').write_bytes(b'runner')
            with self.assertRaises(ValueError): module.package(published,root/'dist','0.2.0','a'*40,'linux-x64')

    def test_both_archives_preserve_manifest_and_native_helper(self):
        for rid in ('linux-x64','win-x64'):
            with self.subTest(rid=rid), tempfile.TemporaryDirectory() as temporary:
                root=Path(temporary);published=root/'published';(published/'tools').mkdir(parents=True)
                suffix='.exe' if rid=='win-x64' else ''
                (published/('XemuTestRunner'+suffix)).write_bytes(b'runner')
                (published/'tools'/('xemu-gamepad'+suffix)).write_bytes(b'helper')
                if rid=='linux-x64': (published/'tools/linux-sdl-mapping.txt').write_text('mapping')
                archive=module.package(published,root/'dist','0.2.0','a'*40,rid)
                if rid=='linux-x64':
                    with tarfile.open(archive) as data:
                        member=next(x for x in data.getmembers() if x.name.endswith('/tools/xemu-gamepad'))
                        self.assertEqual(member.mode,0o755)
                        manifest=json.load(data.extractfile(next(x for x in data.getmembers() if x.name.endswith('/release.json'))))
                else:
                    with zipfile.ZipFile(archive) as data:
                        self.assertTrue(any(x.endswith('/tools/xemu-gamepad.exe') for x in data.namelist()))
                        manifest=json.loads(data.read(next(x for x in data.namelist() if x.endswith('/release.json'))))
                self.assertEqual(manifest['sourceCommit'],'a'*40)
                self.assertEqual(manifest['runtimeIdentifier'],rid)
                module.collect(root/'dist','0.2.0','a'*40,required_rids=[rid])
                archive.write_bytes(archive.read_bytes()+b'tampered')
                with self.assertRaises(ValueError): module.collect(root/'dist','0.2.0','a'*40,required_rids=[rid])

if __name__=='__main__': unittest.main()
