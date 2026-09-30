"""Publish verified platform archives atomically via a draft; never replace assets."""
import json
import os
from pathlib import Path
import subprocess
from release_package import collect, digest


def gh(*arguments, check=True):
    return subprocess.run(['gh', *arguments], check=check, capture_output=True, text=True)


def read_release(repository, tag):
    result = gh('api', f'repos/{repository}/releases/tags/{tag}', check=False)
    if result.returncode:
        if 'HTTP 404' in result.stderr:
            return None
        raise RuntimeError(result.stderr)
    return json.loads(result.stdout)


def main():
    if os.environ.get('GITHUB_REF') != 'refs/heads/main' or os.environ.get('GITHUB_EVENT_NAME') not in ('push', 'workflow_dispatch'):
        raise RuntimeError('Publishing is restricted to an explicit main-branch release run')
    root = Path(__file__).resolve().parents[1]
    version = (root / 'release-version.txt').read_text().strip()
    sha = os.environ['GITHUB_SHA']
    repository = os.environ['GITHUB_REPOSITORY']
    directory = root / 'dist'
    entries = collect(directory, version, sha)
    provenance = directory / 'build-provenance.json'
    provenance.write_text(json.dumps({'sourceCommit': sha, 'version': version, 'packages': entries}, indent=2) + '\n', encoding='utf-8')
    files = [directory / e['archive'] for e in entries] + [provenance]
    checksums = directory / 'SHA256SUMS.txt'
    checksums.write_text(''.join(f'{digest(p)}  {p.name}\n' for p in files), encoding='utf-8')
    files.append(checksums)
    expected = {p.name: {'size': p.stat().st_size, 'digest': 'sha256:' + digest(p)} for p in files}
    tag = f'runner-{version}-{sha[:8]}'
    release = read_release(repository, tag)
    if release is None:
        gh('release', 'create', tag, '--repo', repository, '--target', sha,
           '--title', f'Xemu Test Runner {version}+{sha[:8]}', '--draft',
           '--notes-file', str(root / 'docs/releases' / f'{version}.md'))
        release = read_release(repository, tag)
    if release['target_commitish'] != sha:
        raise RuntimeError('Existing release targets a different source commit')
    existing = {asset['name']: asset for asset in release['assets']}
    if set(existing) - set(expected):
        raise RuntimeError('Existing release contains unexpected assets; refusing to modify it')
    for path in files:
        old = existing.get(path.name)
        if old:
            if old.get('size') != expected[path.name]['size'] or old.get('digest') != expected[path.name]['digest']:
                raise RuntimeError('Existing asset differs; refusing replacement: ' + path.name)
        else:
            if not release['draft']:
                raise RuntimeError('Published release is incomplete; refusing to mutate it')
            gh('release', 'upload', tag, str(path), '--repo', repository)
    release = read_release(repository, tag)
    actual = {asset['name']: {'size': asset['size'], 'digest': asset.get('digest')} for asset in release['assets']}
    if actual != expected:
        raise RuntimeError('GitHub release assets did not match uploaded checksums')
    if release['draft']:
        gh('release', 'edit', tag, '--repo', repository, '--draft=false', '--latest')
    release = read_release(repository, tag)
    if release['draft'] or len(release['assets']) != len(files):
        raise RuntimeError('Release publication did not complete')
    print(release['html_url'])
    summary = os.environ.get('GITHUB_STEP_SUMMARY')
    if summary:
        with open(summary, 'a', encoding='utf-8') as stream:
            stream.write(f"Published [{tag}]({release['html_url']}) from `{sha}` with both platform packages and verified SHA-256 digests.\n")


if __name__ == '__main__':
    main()
