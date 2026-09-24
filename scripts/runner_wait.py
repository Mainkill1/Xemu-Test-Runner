"""Completion-only HTTP waits. No target control, submission or artifact download."""
from __future__ import annotations

import argparse
import json
import time
import urllib.parse
from runner_transport import ClientError, RunnerApi


def bounded_int(minimum: int, maximum: int):
    def parse(text: str) -> int:
        try:
            value = int(text)
        except ValueError:
            raise argparse.ArgumentTypeError(f"must be an integer from {minimum} to {maximum}") from None
        if not minimum <= value <= maximum:
            raise argparse.ArgumentTypeError(f"must be from {minimum} to {maximum}")
        return value
    return parse


def register(sub) -> None:
    wait = sub.add_parser("wait", help="Wait for completion; quiet by default, --updates prints heartbeat lines.")
    wait.add_argument("id")
    duration = wait.add_mutually_exclusive_group()
    duration.add_argument("--max-wait", type=bounded_int(0, 86400), default=30, metavar="SECONDS")
    duration.add_argument("--follow", action="store_true", help="Repeat bounded reads until finished or attention is needed.")
    wait.add_argument("--interval", type=bounded_int(1, 120), default=20, metavar="1..120",
                      help="Maximum seconds held by each HTTP request (default 20).")
    wait.add_argument("--updates", action="store_true", help="Print compact JSON heartbeats as they arrive.")
    wait.add_argument("--job", action="store_true", help="ID is an ordinary API job, not a requested test ID.")


def execute(api: RunnerApi, args) -> dict:
    api.require("completionWait")
    prefix = "/api/v1/jobs/" if args.job else "/api/v1/test-runs/"
    path = prefix + urllib.parse.quote(args.id, safe="") + "/wait"
    deadline = None if args.follow else time.monotonic() + args.max_wait
    original_timeout = api.timeout
    try:
        while True:
            remaining = None if deadline is None else max(0.0, deadline - time.monotonic())
            seconds = args.interval if remaining is None else min(args.interval, int(remaining))
            # The transport deadline must outlive the server's held response.
            api.timeout = max(original_timeout, seconds + 10)
            began = time.monotonic()
            value = api.json(path + "?wait=" + str(seconds))
            if (not isinstance(value, dict) or value.get("id") != args.id or
                    value.get("event") not in ("heartbeat", "finished", "attention") or
                    not isinstance(value.get("terminal"), bool) or
                    value["terminal"] != (value["event"] == "finished")):
                raise ClientError("wait_response_invalid", "The runner returned an inconsistent wait response.",
                                  "Inspect the SAME ID; do not recreate or restart the test.")
            if value["event"] != "heartbeat" or (deadline is not None and deadline - time.monotonic() < 1):
                return value
            if args.updates:
                print(json.dumps(value, separators=(",", ":")), flush=True)
            # A server/proxy returning immediate heartbeats must not create a
            # tight retry loop. Normally the held request already consumed this.
            pause = max(0.0, min(1.0, seconds) - (time.monotonic() - began))
            if deadline is not None:
                pause = min(pause, max(0.0, deadline - time.monotonic()))
            if pause:
                time.sleep(pause)
    finally:
        api.timeout = original_timeout
