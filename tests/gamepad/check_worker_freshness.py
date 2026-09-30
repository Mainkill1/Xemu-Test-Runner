"""Exercise the actual native worker with a non-OS fake PadDevice."""
import json
from pathlib import Path
import subprocess
import sys
import time


def main():
    worker = Path(sys.argv[1]).resolve()
    process = subprocess.Popen([str(worker)], stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, bufsize=1)
    try:
        ready = json.loads(process.stdout.readline())
        assert ready['backend'] == 'fixture-no-os-device'

        def apply(sequence, buttons):
            process.stdin.write(f'state {sequence} {buttons} 0 0 0 0 0 0\n')
            process.stdin.flush()
            receipt = json.loads(process.stdout.readline())
            assert receipt['type'] == 'applied' and receipt['sequence'] == sequence

        apply(1, 0)
        time.sleep(0.55)
        assert process.poll() is None, 'Neutral idle unexpectedly removed the controller'
        apply(2, 0x1000)
        process.wait(timeout=3)
        assert process.returncode == 3, 'A held button did not expire after freshness loss'
        assert 'Controller heartbeat expired' in process.stderr.read()
        print('PASS neutral idle survives; held input still expires')
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=3)
        process.stdin.close()
        process.stdout.close()
        process.stderr.close()


if __name__ == '__main__':
    main()
