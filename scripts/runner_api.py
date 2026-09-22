#!/usr/bin/env python3
"""Operate an already-running tester through HTTP from the agent/build machine.

Normal commands print one compact JSON document. No SSH or tester-side process
launch is available. Use --help for commands and --detail only for explicit inspection.
"""
from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
import sys
import urllib.error
import urllib.parse
from runner_transport import ClientError, RunnerApi, job_url, run_url
from runner_workflows import Workflows, gate


class Parser(argparse.ArgumentParser):
    def error(self, message):
        raise ClientError("arguments_invalid", message, "Run this command with --help.")


def json_input(args, required=False):
    value = getattr(args, "inline_json", None)
    source = getattr(args, "json_file", None) or getattr(args, "plan", None)
    if getattr(args, "stdin", False):
        value = sys.stdin.read(1024 * 1024 + 1)
    elif source is not None:
        if source.stat().st_size > 1024 * 1024:
            raise ClientError("request_too_large", "JSON input exceeds 1 MiB.")
        value = source.read_text(encoding="utf-8-sig")
    if value is None:
        if required:
            raise ClientError("body_required", "Provide --json, --json-file, --stdin or a plan file.")
        return None
    if len(value.encode()) > 1024 * 1024:
        raise ClientError("request_too_large", "JSON input exceeds 1 MiB.")
    return json.loads(value)


def add_json(parser):
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--json", dest="inline_json")
    group.add_argument("--json-file", type=Path)
    group.add_argument("--stdin", action="store_true")


def add_wait(parser, optional=True):
    if optional:
        parser.add_argument("--wait", action="store_true")
    parser.add_argument("--max-wait", type=float, default=30)
    parser.add_argument("--require", choices=("correctness", "eligible"))


def build_parser():
    parser = Parser(description=__doc__)
    parser.add_argument("--url", default=os.environ.get("XEMU_RUNNER_URL"))
    parser.add_argument("--timeout", type=float, default=60)
    parser.add_argument("--wait-seconds", type=float, default=1800, help="Preparation/validation operation deadline; not the test wait window.")
    parser.add_argument("--progress", action="store_true")
    parser.add_argument("--pretty", action="store_true")
    sub = parser.add_subparsers(dest="command", required=True)
    discover = sub.add_parser("discover")
    discover.add_argument("--detail", action="store_true")
    for name in ("jobs", "tests"):
        item = sub.add_parser(name)
        item.add_argument("--offset", type=int, default=0)
        item.add_argument("--limit", type=int, default=10)
    status = sub.add_parser("status")
    status.add_argument("id")
    status.add_argument("--detail", action="store_true")
    result = sub.add_parser("result")
    result.add_argument("id")
    result.add_argument("--require", choices=("correctness", "eligible"))
    result.add_argument("--section", choices=("summary", "failures"), default="summary")
    wait = sub.add_parser("wait")
    wait.add_argument("id")
    add_wait(wait, False)
    for name in ("withdraw", "cancel", "submit-draft"):
        item = sub.add_parser(name)
        item.add_argument("id")
        if name == "submit-draft":
            add_wait(item)
    submit = sub.add_parser("submit")
    submit.add_argument("package", type=Path)
    submit.add_argument("--id", required=True)
    submit.add_argument("--reuse")
    submit.add_argument("--chunk-mib", type=int, choices=range(1, 65), default=8)
    add_wait(submit)
    bake = sub.add_parser("bake")
    bake.add_argument("test")
    bake.add_argument("--from-job", required=True)
    bake.add_argument("--description")
    bake.add_argument("--build-file", action="append")
    run = sub.add_parser("run")
    run.add_argument("test")
    run.add_argument("--revision", required=True)
    run.add_argument("--id", required=True)
    run.add_argument("--build", type=Path)
    run.add_argument("--experiment")
    run.add_argument("--variant")
    run.add_argument("--reference")
    run.add_argument("--chunk-mib", type=int, choices=range(1, 65), default=8)
    add_wait(run)
    retry = sub.add_parser("retry")
    retry.add_argument("source")
    retry.add_argument("--id", required=True)
    add_wait(retry)
    clone = sub.add_parser("clone")
    clone.add_argument("source")
    clone.add_argument("id")
    edit = sub.add_parser("edit")
    edit.add_argument("id")
    edit.add_argument("plan", nargs="?", type=Path)
    add_json(edit)
    logs = sub.add_parser("logs")
    logs.add_argument("id")
    logs.add_argument("--stream", choices=("stdout", "stderr", "operator-events", "segments"), default="stderr")
    logs.add_argument("--cursor")
    logs.add_argument("--max-bytes", type=int, default=4096, choices=range(1, 16385))
    collect = sub.add_parser("collect")
    collect.add_argument("id")
    collect.add_argument("output", type=Path)
    choice = collect.add_mutually_exclusive_group()
    choice.add_argument("--only", action="append", default=[])
    choice.add_argument("--all", action="store_true")
    raw = sub.add_parser("request")
    raw.add_argument("method", choices=("GET", "POST", "PUT", "DELETE"))
    raw.add_argument("path")
    add_json(raw)
    return parser


def execute(args):
    api = RunnerApi(args.url, args.timeout)
    if not math.isfinite(args.wait_seconds) or args.wait_seconds <= 0:
        raise ClientError("arguments_invalid", "--wait-seconds must be positive and finite.")
    maximum = getattr(args, "max_wait", 30)
    if not math.isfinite(maximum) or maximum < 0:
        raise ClientError("arguments_invalid", "--max-wait must be non-negative and finite.")
    work = Workflows(api, args.wait_seconds, args.progress)
    command = args.command
    if command == "request":
        return api.json(args.path, args.method, json_input(args)), 0
    api.require("jobSummaries")
    if command in ("result", "wait") or getattr(args, "wait", False) or getattr(args, "require", None):
        api.require("resultSummaries")
    if command in ("run", "bake", "tests"):
        api.require("pinnedTests")
    if command == "discover":
        value = api.json("/api/v1/agent") if args.detail else api.discovery
    elif command in ("jobs", "tests"):
        value = api.json(f"/api/v1/{command}?offset={args.offset}&limit={args.limit}")
    elif command == "status":
        value = api.json(job_url(args.id)) if args.detail else work.status(args.id)
    elif command == "result":
        value = work.result(args.id)
    elif command == "wait":
        value = work.wait(args.id, args.max_wait)
    elif command == "submit":
        value = work.submit(args.package.resolve(), args.id, args.reuse, args.chunk_mib * 1024 * 1024)
    elif command == "submit-draft":
        value = work.submit_draft(args.id)
    elif command == "run":
        labels = {key: value for key, value in {"experimentId": args.experiment, "variant": args.variant, "reference": args.reference}.items() if value is not None}
        value = work.run_test(args.test, args.revision, args.id, args.build.resolve() if args.build else None,
                              labels, args.chunk_mib * 1024 * 1024)
    elif command == "bake":
        body = {"sourceJobId": args.from_job}
        if args.description is not None:
            body["description"] = args.description
        if args.build_file is not None:
            body["buildFiles"] = args.build_file
        value = api.json("/api/v1/tests/" + urllib.parse.quote(args.test, safe="") + "/bake", "POST", body)
    elif command in ("retry", "clone"):
        value = work.clone(args.source, args.id, command == "retry")
    elif command in ("withdraw", "cancel"):
        api.json(job_url(args.id) + ("/withdraw" if command == "withdraw" else ""), "POST" if command == "withdraw" else "DELETE")
        value = work.status(args.id)
    elif command == "edit":
        if args.plan and (args.inline_json is not None or args.json_file or args.stdin):
            raise ClientError("arguments_invalid", "Choose one plan input source.")
        plan = json_input(args, True)
        if not isinstance(plan, dict):
            raise ClientError("plan_invalid", "The replacement plan must be an object.")
        for key in list(plan):
            if key.lower() == "id":
                del plan[key]
        plan["Id"] = args.id
        current = api.json(job_url(args.id))
        api.json(job_url(args.id) + "/plan", "PUT", plan, {"If-Match": current["revision"]})
        value = work.status(args.id)
    elif command == "logs":
        api.require("logCursors")
        job = work.status(args.id)
        if not job.get("runId"):
            raise ClientError("run_not_started", "This job has no run yet.")
        file = args.stream + (".log" if args.stream in ("stdout", "stderr") else ".jsonl")
        query = {"file": file, "bytes": args.max_bytes}
        if args.cursor:
            query["cursor"] = args.cursor
        value = api.json(run_url(job["runId"]) + "/log?" + urllib.parse.urlencode(query))
    elif command == "collect":
        value = work.collect(args.id, args.output, args.only, args.all)
    else:
        raise ClientError("command_invalid", "Unsupported command.")
    if getattr(args, "wait", False):
        value = work.wait(args.id, args.max_wait)
    elif getattr(args, "require", None) and command not in ("result", "wait"):
        value = work.result(args.id)
    code = gate(value, getattr(args, "require", None))
    if command == "result" and args.section == "failures":
        value = {key: item for key, item in value.items() if key in
                 ("ok", "jobId", "runId", "state", "available", "code", "outcome", "failures", "moreFailures", "truncated", "detail")}
    return value, code


def main() -> int:
    pretty = False
    try:
        args = build_parser().parse_args()
        pretty = args.pretty
        value, code = execute(args)
    except ClientError as error:
        value, code = error.document(), 1
    except KeyboardInterrupt:
        value, code = {"ok": False, "code": "client_interrupted", "hint": "The server may still own the operation. Inspect the SAME job ID."}, 130
    except (OSError, ValueError, KeyError, TypeError, urllib.error.URLError) as error:
        value, code = {"ok": False, "code": "client_error", "error": str(error)[:1024],
                       "hint": "Check the response/local input. Keep the same ID after an interrupted request."}, 1
    print(json.dumps(value, indent=2 if pretty else None, separators=None if pretty else (",", ":"), allow_nan=False))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
