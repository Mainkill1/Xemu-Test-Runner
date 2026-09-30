"""Package tested self-contained outputs; verify both platform artifacts before release."""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile
import zipfile

RIDS = ('linux-x64', 'win-x64')


def identity(version, sha, rid):
    if not re.fullmatch(r'\d+\.\d+\.\d+', version) or not re.fullmatch(r'[0-9a-f]{40}', sha) or rid not in RIDS:
        raise ValueError('Release requires numeric semantic version, full source SHA, and supported RID')


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def package(published, destination, version, sha, rid):
    identity(version, sha, rid)
    suffix = '.exe' if rid == 'win-x64' else ''
    required = ['XemuTestRunner' + suffix, 'tools/xemu-gamepad' + suffix]
    if rid == 'linux-x64':
        required.append('tools/linux-sdl-mapping.txt')
    for name in required:
        if not (published / name).is_file() or not (published / name).stat().st_size:
            raise ValueError('Required packaged file missing: ' + name)
    if any(path.is_symlink() for path in published.rglob('*')):
        raise ValueError('Release payload must not contain symlinks')
    destination.mkdir(parents=True, exist_ok=True)
    stem = f'XemuTestRunner-{version}-{rid}'
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary) / stem
        shutil.copytree(published, root)
        manifest = {'version': version, 'sourceCommit': sha, 'runtimeIdentifier': rid,
                    'files': {str(p.relative_to(root)).replace('\\', '/'):
                              {'sha256': digest(p), 'bytes': p.stat().st_size}
                              for p in sorted(root.rglob('*')) if p.is_file()}}
        (root / 'release.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
        if rid == 'win-x64':
            archive = destination / (stem + '.zip')
            with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as output:
                for path in sorted(root.rglob('*')):
                    if path.is_file():
                        info = zipfile.ZipInfo(path.relative_to(root.parent).as_posix(), (2020, 1, 1, 0, 0, 0))
                        info.compress_type = zipfile.ZIP_DEFLATED
                        info.external_attr = 0o100644 << 16
                        output.writestr(info, path.read_bytes())
        else:
            archive = destination / (stem + '.tar.gz')
            def normalize(info):
                info.uid = info.gid = 0
                info.uname = info.gname = ''
                info.mtime = 0
                info.mode = 0o755 if info.isdir() or info.name.endswith(('/XemuTestRunner', '/tools/xemu-gamepad')) else 0o644
                return info
            with archive.open('wb') as file, gzip.GzipFile(filename='', fileobj=file, mode='wb', mtime=0) as compressed:
                with tarfile.open(fileobj=compressed, mode='w') as output:
                    output.add(root, arcname=stem, filter=normalize)
    provenance = {'version': version, 'sourceCommit': sha, 'runtimeIdentifier': rid,
                  'archive': archive.name, 'sha256': digest(archive), 'bytes': archive.stat().st_size}
    (destination / f'provenance-{rid}.json').write_text(json.dumps(provenance, indent=2) + '\n', encoding='utf-8')
    return archive


def collect(directory, version, sha, required_rids=RIDS):
    entries = []
    for rid in required_rids:
        identity(version, sha, rid)
        entry = json.loads((directory / f'provenance-{rid}.json').read_text(encoding='utf-8'))
        name = f'XemuTestRunner-{version}-{rid}' + ('.zip' if rid == 'win-x64' else '.tar.gz')
        if (entry.get('version'), entry.get('sourceCommit'), entry.get('runtimeIdentifier'), entry.get('archive')) != (version, sha, rid, name):
            raise ValueError('Mixed source/version/platform release artifacts')
        archive = directory / name
        if entry.get('sha256') != digest(archive) or entry.get('bytes') != archive.stat().st_size:
            raise ValueError('Release archive checksum or size mismatch: ' + name)
        entries.append(entry)
    return entries


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--rid', choices=RIDS, required=True)
    parser.add_argument('--sha', required=True)
    arguments = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    version = (root / 'release-version.txt').read_text().strip()
    identity(version, arguments.sha, arguments.rid)
    published = root / 'publish' / arguments.rid
    executable = published / ('XemuTestRunner.exe' if arguments.rid == 'win-x64' else 'XemuTestRunner')
    actual = subprocess.check_output([str(executable), '--version'], text=True, timeout=30).strip()
    expected = version + '+' + arguments.sha
    if actual != expected:
        raise ValueError(f'Packaged executable version mismatch: {actual!r} != {expected!r}')
    # Package operator guidance and HTTP clients, not tests or local run data.
    shutil.copy2(root / 'README.md', published)
    shutil.copytree(root / 'docs', published / 'docs', dirs_exist_ok=True)
    clients = published / 'scripts'
    clients.mkdir(exist_ok=True)
    for script in (root / 'scripts').glob('runner_*.py'):
        shutil.copy2(script, clients)
    print(package(published, root / 'dist', version, arguments.sha, arguments.rid))


if __name__ == '__main__':
    main()
