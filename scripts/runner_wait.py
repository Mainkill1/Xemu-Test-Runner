"""Follow one test until completion. No user-selected deadline or polling interval."""
from __future__ import annotations

import http.client
import json
import time
import urllib.error
import urllib.parse
from runner_transport import ClientError, RunnerApi

# These limit individual connections/retries, never the logical completion wait.
_RETRYABLE_HTTP_STATUSES = {408, 429, 500, 502, 503, 504}
_MINIMUM_SOCKET_TIMEOUT = 60
_MAXIMUM_RETRY_DELAY = 5


def register(sub) -> None:
    wait = sub.add_parser(
        "wait",
        help="Stay with this test until completion or attention is needed; no overall time limit.",
        description="Follow the same test until it finishes or needs attention. No duration estimate is needed.",
    )
    wait.add_argument("id", help="Test request ID returned by upload/select.")
    wait.add_argument("--updates", action="store_true",
                      help="Print compact heartbeat replies; otherwise show only the final reply.")
    wait.add_argument("--job", action="store_true",
                      help="ID is an ordinary API job instead of a requested test.")


def _validate_reply(reply: object, expected_id: str) -> dict:
    """Reject a different attempt or inconsistent completion flag, not a failed test."""
    valid = isinstance(reply, dict) and reply.get("id") == expected_id
    if valid:
        event = reply.get("event")
        terminal = reply.get("terminal")
        valid = (
            event in ("heartbeat", "finished", "attention")
            and isinstance(terminal, bool)
            and terminal == (event == "finished")
        )
    if not valid:
        raise ClientError(
            "wait_response_invalid",
            "The runner returned an inconsistent wait response.",
            "Inspect the SAME ID; do not recreate or restart the test.",
        )
    return reply


def _pause_before_retry(consecutive_failures: int) -> None:
    """Back off repeated connection failures without imposing an overall deadline."""
    time.sleep(min(_MAXIMUM_RETRY_DELAY, consecutive_failures))


def execute(api: RunnerApi, args) -> dict:
    """Observe only: reconnecting this GET must never replay a start or submit."""
    prefix = "/api/v1/jobs/" if args.job else "/api/v1/test-runs/"
    wait_url = prefix + urllib.parse.quote(args.id, safe="") + "/wait"
    original_timeout = api.timeout
    capability_checked = False
    consecutive_failures = 0
    try:
        api.timeout = max(original_timeout, _MINIMUM_SOCKET_TIMEOUT)
        while True:
            read_started = time.monotonic()
            try:
                if not capability_checked:
                    api.require("completionWait")
                    capability_checked = True
                reply = api.json(wait_url)
            except ClientError as error:
                # Unknown IDs, authorization and schema errors require attention.
                if error.status not in _RETRYABLE_HTTP_STATUSES:
                    raise
                consecutive_failures += 1
                _pause_before_retry(consecutive_failures)
                continue
            except (urllib.error.URLError, TimeoutError, ConnectionError, http.client.HTTPException):
                consecutive_failures += 1
                _pause_before_retry(consecutive_failures)
                continue

            consecutive_failures = 0
            reply = _validate_reply(reply, args.id)
            if reply["event"] != "heartbeat":
                return reply
            if args.updates:
                print(json.dumps(reply, separators=(",", ":")), flush=True)

            # Normal reads were already held by the server. An eager proxy must
            # not create a tight read loop by returning immediate heartbeats.
            pause = max(0.0, 1.0 - (time.monotonic() - read_started))
            if pause:
                time.sleep(pause)
    finally:
        api.timeout = original_timeout
