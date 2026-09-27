"""Test-only pipe producer; never implements an OS gamepad or fake input result."""
import asyncio
import json
import subprocess
import threading
import time


class Producer:
    def __init__(self, proc):
        self.proc = proc
        self.state = (0,) * 7
        self.sequence = 0
        self.failure = None
        self.last_receipt = None
        self.receipt_times = []
        self.details = ''
        self.pulse = None
        self._stopped = threading.Event()

    @classmethod
    async def start(cls, executable):
        command = executable if isinstance(executable, list) else [executable]
        proc = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE)
        producer = cls(proc)
        try:
            line = await asyncio.wait_for(asyncio.to_thread(proc.stdout.readline), 15)
            if not line:
                await producer.dispose()
                raise AssertionError(producer.details or 'Worker exited before ready')
            producer.ready = json.loads(line)
            assert producer.ready['type'] == 'ready' and not producer.ready['additionalDriver'], producer.ready
            producer.pulse = threading.Thread(target=producer.heartbeat, name='controller-fixture-sender')
            producer.pulse.start()
            return producer
        except BaseException:
            if proc.poll() is None:
                proc.kill()
            await producer.dispose()
            raise

    def heartbeat(self):
        # SDL must stay on the consumer's main thread. Its hotplug/open calls
        # must not block the independent input producer's 25ms refresh cadence.
        try:
            while not self._stopped.is_set():
                self.sequence += 1
                state = tuple(self.state)
                line = 'state ' + ' '.join(map(str, (self.sequence,) + state)) + '\n'
                self.proc.stdin.write(line.encode())
                self.proc.stdin.flush()
                response = self.proc.stdout.readline()
                assert response, 'Input worker exited without an applied receipt'
                self.last_receipt = json.loads(response)
                self.receipt_times.append(time.monotonic())
                self.receipt_times = self.receipt_times[-32:]
                assert self.last_receipt['type'] == 'applied' and self.last_receipt['sequence'] == self.sequence, self.last_receipt
                self._stopped.wait(0.025)
        except BaseException as error:
            self.failure = error

    async def stop_heartbeat(self):
        self._stopped.set()
        if self.pulse:
            await asyncio.to_thread(self.pulse.join, 3)
            if self.pulse.is_alive():
                self.proc.kill()
                await asyncio.to_thread(self.proc.wait, 3)
                await asyncio.to_thread(self.pulse.join, 3)
                raise AssertionError('Input producer did not finish its outstanding receipt')
            self.pulse = None

    async def dispose(self):
        await self.stop_heartbeat()
        if self.proc.poll() is None:
            try:
                self.proc.stdin.write(b'stop\n')
                self.proc.stdin.flush()
                await asyncio.to_thread(self.proc.wait, 3)
            except (BrokenPipeError, OSError, subprocess.TimeoutExpired):
                if self.proc.poll() is None:
                    self.proc.kill()
                await asyncio.to_thread(self.proc.wait, 3)
        if not self.proc.stderr.closed:
            self.details = self.proc.stderr.read().decode(errors='replace')
        for pipe in (self.proc.stdin, self.proc.stdout, self.proc.stderr):
            # Buffered stdin can fail a second flush when the child exited.
            # Preserve its original startup/apply error instead of masking it.
            try:
                pipe.close()
            except OSError:
                pass
