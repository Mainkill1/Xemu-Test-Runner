"""Test-only pipe producer; never implements an OS gamepad or fake input result."""
import asyncio
from collections import deque
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
        self.receipts = None
        self._stopped = threading.Event()
        self._changed = threading.Condition()
        self._pending = deque()
        self._sender_done = False

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
            producer.receipts = threading.Thread(target=producer.read_receipts, name='controller-fixture-receipts')
            producer.receipts.start()
            producer.pulse.start()
            return producer
        except BaseException:
            if proc.poll() is None:
                proc.kill()
            await producer.dispose()
            raise

    def _fail(self, error):
        with self._changed:
            if self.failure is None:
                self.failure = error
            self._stopped.set()
            self._changed.notify_all()

    def heartbeat(self):
        # Fresh input has its own cadence. Waiting for a receipt before sending
        # the next state makes consumer/readback scheduling stall a healthy pad.
        try:
            while not self._stopped.is_set():
                started = time.monotonic()
                with self._changed:
                    assert len(self._pending) < 16, 'Fixture receipt backlog exceeded 16 states'
                    self.sequence += 1
                    sequence = self.sequence
                    self._pending.append(sequence)
                    self._changed.notify_all()
                state = tuple(self.state)
                line = 'state ' + ' '.join(map(str, (sequence,) + state)) + '\n'
                self.proc.stdin.write(line.encode())
                self.proc.stdin.flush()
                self._stopped.wait(max(0, 0.025 - (time.monotonic() - started)))
        except BaseException as error:
            self._fail(error)
        finally:
            with self._changed:
                self._sender_done = True
                self._changed.notify_all()

    def read_receipts(self):
        # Check every real receipt in order; do not create acknowledgements or
        # make a successful send count as OS application. Bound pending work.
        try:
            while True:
                with self._changed:
                    self._changed.wait_for(lambda: self._pending or self._sender_done)
                    if not self._pending:
                        return
                    expected = self._pending[0]
                response = self.proc.stdout.readline(4097)
                assert response and len(response) <= 4096, 'Missing or oversized applied receipt'
                receipt = json.loads(response)
                assert receipt['type'] == 'applied' and receipt['sequence'] == expected, receipt
                with self._changed:
                    self._pending.popleft()
                    self.last_receipt = receipt
                    self.receipt_times.append(time.monotonic())
                    self.receipt_times = self.receipt_times[-32:]
        except BaseException as error:
            self._fail(error)

    async def stop_heartbeat(self):
        self._stopped.set()
        # First finish the writer, then drain exactly its outstanding receipts.
        # Neither thread may survive cleanup; a stalled pipe is a fixture error.
        for attribute in ('pulse', 'receipts'):
            thread = getattr(self, attribute)
            if thread:
                await asyncio.to_thread(thread.join, 3)
                if thread.is_alive():
                    if self.proc.poll() is None:
                        self.proc.kill()
                    await asyncio.to_thread(self.proc.wait, 3)
                    await asyncio.to_thread(thread.join, 3)
                    raise AssertionError('Input fixture did not finish its outstanding pipe work')
                setattr(self, attribute, None)

    async def dispose(self):
        await self.stop_heartbeat()
        if self.proc.poll() is None:
            try:
                if self.proc.stdin.closed:
                    await asyncio.to_thread(self.proc.wait, 3)
                else:
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
