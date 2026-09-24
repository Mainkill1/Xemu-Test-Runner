"""Follow one test until completion. No user-selected deadline or polling interval."""
from __future__ import annotations

import http.client
import json
import time
import urllib.error
import urllib.parse
from runner_transport import ClientError, RunnerApi


def register(sub) -> None:
    wait = sub.add_parser("wait", help="Stay with this test until completion or attention is needed; no overall time limit.")
    wait.add_argument("id")
    wait.add_argument("--updates", action="store_true", help="Print the runner's compact heartbeat replies; otherwise show only the final reply.")
    wait.add_argument("--job", action="store_true", help="ID is an ordinary API job instead of a requested test.")


def execute(api: RunnerApi, args) -> dict:
    prefix = "/api/v1/jobs/" if args.job else "/api/v1/test-runs/"
    path = prefix + urllib.parse.quote(args.id, safe="") + "/wait"
    original_timeout = api.timeout
    discovered = False
    failures = 0
    try:
        # Per-connection bounds are transport details, NOT test deadlines. A
        # heartbeat or lost connection never finishes this observation command.
        api.timeout = max(original_timeout, 60)
        while True:
            began = time.monotonic()
            try:
                if not discovered:
                    api.require("completionWait")
                    discovered = True
                value = api.json(path)
            except ClientError as error:
                if error.status not in (408, 429, 500, 502, 503, 504):
                    raise
                failures += 1
                time.sleep(min(5, failures))
                continue
            except (urllib.error.URLError, TimeoutError, ConnectionError, http.client.HTTPException):
                failures += 1
                time.sleep(min(5, failures))
                continue
            failures = 0
            if (not isinstance(value, dict) or value.get("id") != args.id or
                    value.get("event") not in ("heartbeat", "finished", "attention") or
                    not isinstance(value.get("terminal"), bool) or
                    value["terminal"] != (value["event"] == "finished")):
                raise ClientError("wait_response_invalid", "The runner returned an inconsistent wait response.",
                                  "Inspect the SAME ID; do not recreate or restart the test.")
            if value["event"] != "heartbeat":
                return value
            if args.updates:
                print(json.dumps(value, separators=(",", ":")), flush=True)
            # An incorrectly eager proxy must not trigger a tight read loop.
            pause = max(0.0, 1.0 - (time.monotonic() - began))
            if pause:
                time.sleep(pause)
    finally:
        api.timeout = original_timeout
