"""Negative capability check on Windows Server, not a working-controller result."""
import json
import os
from pathlib import Path
import platform
import subprocess
import sys

assert os.name == 'nt' and sys.getwindowsversion().product_type != 1, 'Use a Windows Server host'
result = subprocess.run([str(Path(sys.argv[1]).resolve())], input=b'', capture_output=True, timeout=10)
details = result.stderr.decode(errors='replace')
report = {'platform': platform.platform(), 'available': False, 'readbackQualified': False,
          'reason': 'REGDB_E_CLASSNOTREG', 'exitCode': result.returncode, 'stderr': details}
Path('gamepad-unavailable.json').write_text(json.dumps(report, indent=2))
assert result.returncode == 3 and not result.stdout and 'HRESULT=80040154' in details, (
    'Server capability changed or failed for a different reason; qualify it rather than accepting silently',
    result.returncode, result.stdout, details)
print('PASS explicit missing-OS-capability refusal; this host is NOT controller-qualified')
