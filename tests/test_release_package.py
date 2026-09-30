import importlib.util
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('release_package', Path(__file__).resolve().parents[1] / 'scripts/release_package.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class ReleaseChecks(unittest.TestCase):
    def test_identity_rejects_unsafe_or_incomplete_values(self):
        for version, sha, rid in [('..', 'a'*40, 'linux-x64'), ('0.2.0','main','linux-x64'), ('0.2.0','a'*40,'../win')]:
            with self.assertRaises(ValueError): module.identity(version, sha, rid)

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
