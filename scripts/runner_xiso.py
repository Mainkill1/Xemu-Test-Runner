#!/usr/bin/env python3
"""Select XISO categories or individual tests through the runner HTTP API.

Run on the agent/build machine with runner_transport.py beside this file.
Applications are uploaded once using runner_tests.py. Selection creates an
unstarted campaign; only start or --start authorizes execution.
"""
from __future__ import annotations

import argparse
import http.client
import json
import os
import sys
import time
import urllib.error
import urllib.parse
from runner_transport import ClientError, RunnerApi


class Parser(argparse.ArgumentParser):
    def error(self, message):
        raise ClientError("arguments_invalid", message, "Use --help for XISO categories, selection and execution commands.")


def bounded_int(low: int, high: int):
    def parse(value: str) -> int:
        number = int(value)
        if not low <= number <= high:
            raise argparse.ArgumentTypeError(f"expected an integer from {low} to {high}")
        return number
    return parse


def build_parser():
    parser = Parser(description=__doc__)
    parser.add_argument("--url", default=os.environ.get("XEMU_RUNNER_URL"))
    parser.add_argument("--pretty", action="store_true", help="Indent explicit plan/catalog inspection output.")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("targets", help="Read known artifact pins; candidate does not mean qualified baseline.")
    suites = commands.add_parser("suites", help="List installed immutable XISO suites.")
    page_options(suites)
    register = commands.add_parser("register", help="Register a saved base template once; never start it.")
    register.add_argument("test", help="Existing saved base test ID.")
    register.add_argument("--revision", required=True, help="Full saved-test revision, pinned once.")
    register.add_argument("--id", required=True, help="New immutable suite name.")
    register.add_argument("--target", help="Optional known target pin, e.g. shader-pilot-51bc23d.")
    settings_options(register)
    categories = commands.add_parser("categories", help="Show available subsystem categories and counts.")
    categories.add_argument("suite")
    tests = commands.add_parser("tests", help="Browse stable individual test IDs.")
    tests.add_argument("suite")
    tests.add_argument("--category")
    tests.add_argument("--search")
    page_options(tests)
    select = commands.add_parser("select", help="Freeze selected work without starting; defaults stay on the tester.")
    select.add_argument("application", help="Already-uploaded application ID.")
    select.add_argument("--id", required=True, help="New campaign ID; reuse it only for an identical request.")
    select.add_argument("--suite", help="May be omitted only when exactly one suite is installed.")
    select.add_argument("--category", action="append", default=[], help="Repeat to union subsystem categories.")
    select.add_argument("--test", action="append", default=[], help="Repeat to union exact stable leaf IDs.")
    select.add_argument("--mode", choices=("smoke", "sections", "full", "monolithic"))
    select.add_argument("--reference", help="Optional reference application for a frozen per-chunk ABBA schedule.")
    select.add_argument("--start", action="store_true", help="Explicitly authorize the campaign after creation succeeds.")
    settings_options(select)
    for name, help_text in (("start", "Explicitly start an existing campaign."),
                            ("status", "Read one compact progress/result summary."),
                            ("plan", "Inspect all resolved defaults, selections and child IDs."),
                            ("cancel", "Cancel remaining work without killing an owned target.")):
        command = commands.add_parser(name, help=help_text)
        command.add_argument("id")
    attempts = commands.add_parser("attempts", help="Read detailed child outcomes without raw downloads.")
    attempts.add_argument("id")
    page_options(attempts)
    wait = commands.add_parser("wait", help="Follow until terminal or attention; no overall time limit.")
    wait.add_argument("id")
    wait.add_argument("--updates", action="store_true", help="Print optional compact server heartbeat replies.")
    return parser


def page_options(parser):
    parser.add_argument("--offset", type=bounded_int(0, 1000000), default=0)
    parser.add_argument("--limit", type=bounded_int(1, 100), default=25)


def settings_options(parser):
    parser.add_argument("--warmups", type=bounded_int(0, 100000), help="Optional override; otherwise inherit the pinned suite.")
    parser.add_argument("--multiplier", type=bounded_int(1, 100000), help="Optional fixed-work override shared by A and B.")
    parser.add_argument("--completion", choices=("enqueue", "batch_complete", "per_iteration"))


def settings(args):
    return {key: value for key, value in {
        "warmup_iterations": args.warmups,
        "measurement_iterations_multiplier": args.multiplier,
        "gpu_completion_mode": args.completion
    }.items() if value is not None}


def quoted(value: str) -> str:
    return urllib.parse.quote(value, safe="")


def campaign_path(identity: str) -> str:
    return "/api/v1/xiso-campaigns/" + quoted(identity)


def emit(value, pretty=False):
    print(json.dumps(value, indent=2 if pretty else None,
                     separators=None if pretty else (",", ":"), allow_nan=False), flush=True)


def follow(api: RunnerApi, identity: str, updates: bool):
    """Retry only this read. Never replay a creation/start request on reconnect."""
    path = campaign_path(identity) + "/wait"
    backoff = 0.5
    while True:
        try:
            value = api.json(path)
        except ClientError as error:
            if error.status not in (429, 502, 503, 504):
                raise
            time.sleep(backoff)
            backoff = min(backoff * 2, 5)
            continue
        except (urllib.error.URLError, TimeoutError, ConnectionError,
                http.client.IncompleteRead, http.client.RemoteDisconnected):
            time.sleep(backoff)
            backoff = min(backoff * 2, 5)
            continue
        if not isinstance(value, dict) or value.get("event") not in ("heartbeat", "finished", "attention"):
            raise ClientError("wait_response_invalid", "The runner returned an invalid campaign observation.")
        if value.get("id") != identity or value.get("next") != path:
            raise ClientError("wait_identity_invalid", "The wait response changed campaign identity or next endpoint.")
        if not isinstance(value.get("terminal"), bool) or (value["event"] == "finished") != value["terminal"]:
            raise ClientError("wait_state_invalid", "The wait response disagrees about terminal state.")
        if value["event"] != "heartbeat":
            return value
        if updates:
            emit(value)
        backoff = 0.5


def execute(args):
    # Registration may verify/copy a large retained asset; its internal socket
    # budget is not exposed as an agent-estimated execution/wait deadline.
    api = RunnerApi(args.url, timeout=1800 if args.command == "register" else 60)
    info = api.json("/api/v1/help?topic=xiso")
    if not isinstance(info, dict) or info.get("capability") != "xisoCampaigns":
        raise ClientError("capability_missing", "This tester lacks XISO campaign support.", "Deploy the matching runner; do not replace the API with guest networking or SSH.")
    command = args.command
    if command == "targets":
        return api.json("/api/v1/xiso-targets")
    if command == "register":
        body = {"id": args.id, "testId": args.test, "revision": args.revision}
        if args.target:
            body["target"] = args.target
        overrides = settings(args)
        if overrides:
            body["settings"] = overrides
        return api.json("/api/v1/xiso-suites", "POST", body)
    if command in ("suites", "tests", "attempts"):
        query = {"offset": args.offset, "limit": args.limit}
        if command == "tests":
            if args.category:
                query["category"] = args.category
            if args.search:
                query["q"] = args.search
            path = "/api/v1/xiso-suites/" + quoted(args.suite) + "/tests"
        else:
            path = "/api/v1/xiso-suites" if command == "suites" else campaign_path(args.id) + "/attempts"
        return api.json(path + "?" + urllib.parse.urlencode(query))
    if command == "categories":
        return api.json("/api/v1/xiso-suites/" + quoted(args.suite) + "/categories")
    if command == "select":
        body = {"id": args.id, "application": args.application}
        for field, value in (("suite", args.suite), ("categories", args.category), ("tests", args.test),
                             ("mode", args.mode), ("referenceApplication", args.reference), ("settings", settings(args))):
            if value:
                body[field] = value
        result = api.json("/api/v1/xiso-campaigns", "POST", body)
        return api.json(campaign_path(args.id) + "/start", "POST", {}) if args.start else result
    if command == "wait":
        return follow(api, args.id, args.updates)
    if command == "start":
        return api.json(campaign_path(args.id) + "/start", "POST", {})
    if command == "cancel":
        return api.json(campaign_path(args.id), "DELETE")
    return api.json(campaign_path(args.id) + ("?view=plan" if command == "plan" else ""))


def main():
    try:
        args = build_parser().parse_args()
        value = execute(args)
        emit(value, args.pretty)
        return 0  # A successful API operation is not a passing guest result.
    except ClientError as error:
        emit(error.document())
        return 1
    except KeyboardInterrupt:
        emit({"ok": False, "code": "observer_interrupted", "hint": "The server retains execution intent. Read the same campaign ID; observation interruption does not cancel it."})
        return 130
    except (OSError, ValueError, TypeError, KeyError, http.client.HTTPException) as error:
        emit({"ok": False, "code": "xiso_client_error", "error": str(error)[:1024],
              "hint": "Inspect the same campaign ID. A start whose response was lost may already be authorized."})
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
