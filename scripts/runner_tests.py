#!/usr/bin/env python3
"""Upload once, inspect named configs, select tests, and explicitly request execution.

This is a small HTTP client for test operation. Upload/select never start tests
unless --start is supplied. Keep runner_transport.py beside this file.
"""
from __future__ import annotations

import argparse
import http.client
import json
import os
from pathlib import Path
import sys
import urllib.parse
from runner_transport import ClientError, RunnerApi, declaration, inside, job_url


def catalog(api: RunnerApi) -> list[dict]:
    values, offset = [], 0
    while offset is not None:
        page = api.json(f"/api/v1/test-configs?limit=100&offset={offset}")
        values.extend(page["items"])
        new_offset = page.get("nextOffset")
        if new_offset is not None and new_offset <= offset:
            raise ClientError("catalog_invalid", "Catalog pagination did not advance.")
        offset = new_offset
    return values


def selection(api: RunnerApi, selectors: list[str]) -> list[tuple[str, str]]:
    available = None
    selected = []
    for selector in selectors:
        if "@" in selector:
            name, revision = selector.rsplit("@", 1)
            if len(revision) != 64 or any(ch not in "0123456789abcdefABCDEF" for ch in revision):
                raise ClientError("revision_invalid", "Use a complete SHA-256 test revision after @.")
            # Validate even explicit pins before creating any selection.
            api.json("/api/v1/tests/" + urllib.parse.quote(name, safe="") + "/" + revision)
        else:
            available = catalog(api) if available is None else available
            matches = [item for item in available if item["id"] == selector]
            if len(matches) != 1:
                raise ClientError("test_selection_ambiguous", "Test name is missing or has multiple revisions: " + selector,
                                  "List tests, then use NAME@FULL_REVISION.")
            name, revision = selector, matches[0]["revision"]
        selected.append((name, revision.lower()))
    return selected


def select_tests(api: RunnerApi, application: str, prefix: str, selected: list[tuple[str, str]], start: bool) -> list[dict]:
    if len(prefix) > 54 or len(selected) > 256:
        raise ClientError("selection_limit", "Use a prefix of at most 54 characters and at most 256 tests.")
    values = []
    for number, (name, revision) in enumerate(selected, 1):
        request_id = f"{prefix}-t{number:03}"
        value = api.json("/api/v1/test-runs", "POST", {
            "id": request_id, "applicationJobId": application, "testId": name, "revision": revision})
        values.append({"id": request_id, "test": name, "revision": revision, "state": value["state"]})
    # Publish execution intent only after every selection was accepted. Failures
    # retain IDs for inspection; rerunning identical requests is idempotent.
    if start:
        for value in values:
            reply = api.json("/api/v1/test-runs/" + value["id"] + "/start", "POST", {})
            value["state"] = reply["state"]
    return values


def upload_application(api: RunnerApi, root: Path, executable: str, identity: str) -> str:
    executable = executable.replace("\\", "/").removeprefix("./")
    inside(root, executable)
    files = []
    for directory, children, names in os.walk(root, followlinks=False):
        children[:] = sorted(name for name in children if not name.startswith("."))
        for child in children:
            if (Path(directory) / child).is_symlink():
                raise ClientError("file_path_invalid", "Application directory contains a link.")
        for name in sorted(names):
            relative = (Path(directory) / name).relative_to(root).as_posix()
            if name.startswith(".") or relative == "job.json":
                continue
            files.append(declaration(root, relative, relative == executable))
    chosen = next((file for file in files if file["Path"] == executable), None)
    if chosen is None:
        raise ClientError("executable_missing", "--exe is not a regular application file.")
    # The application container has no test procedure and is never submitted.
    api.json("/api/v1/jobs", "POST", {"id": identity,
        "job": {"id": identity, "executable": executable, "expectedExecutableSha256": chosen["Sha256"], "plan": []},
        "files": files})
    for item in files:
        api.upload(identity, root, item)
    return chosen["Sha256"]


def parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--url", default=os.environ.get("XEMU_RUNNER_URL"))
    p.add_argument("--json", action="store_true", help="Keep machine-readable JSON for config inspection.")
    sub = p.add_subparsers(dest="command", required=True)
    sub.add_parser("list")
    show = sub.add_parser("show")
    show.add_argument("test", help="NAME or NAME@REVISION")
    show.add_argument("--out", type=Path)
    config = sub.add_parser("config-upload")
    config.add_argument("name")
    config.add_argument("file", type=Path)
    config.add_argument("--assets", required=True, help="Retained API job containing the required assets.")
    config.add_argument("--description", default="")
    config.add_argument("--build-file", action="append")
    upload = sub.add_parser("upload")
    upload.add_argument("directory", type=Path)
    upload.add_argument("--exe", required=True)
    upload.add_argument("--id", required=True, help="Stable application upload ID.")
    upload.add_argument("--tests", nargs="+", default=[])
    upload.add_argument("--start", action="store_true")
    choose = sub.add_parser("select")
    choose.add_argument("application")
    choose.add_argument("--id", required=True, help="New selection ID prefix.")
    choose.add_argument("--tests", nargs="+", required=True)
    choose.add_argument("--start", action="store_true")
    start = sub.add_parser("start")
    start.add_argument("ids", nargs="+")
    status = sub.add_parser("status")
    status.add_argument("id")
    return p


def execute(args) -> dict | str:
    api = RunnerApi(args.url)
    api.json("/api/v1/help?topic=test-workflow")
    command = args.command
    if command == "list":
        return {"tests": catalog(api)}
    if command == "show":
        name, revision = selection(api, [args.test])[0]
        value = api.json("/api/v1/test-configs/" + urllib.parse.quote(name, safe="") + "/" + revision)
        job = value["definition"]["job"]
        if args.out:
            with args.out.open("x", encoding="utf-8") as file:
                json.dump(job, file, indent=2)
            return {"id": name, "revision": revision, "file": str(args.out)}
        return value if args.json else json.dumps(job, indent=2)
    if command == "config-upload":
        if args.file.stat().st_size > 1024 * 1024:
            raise ClientError("config_too_large", "Configuration exceeds 1 MiB.")
        body = {"sourceJobId": args.assets, "job": json.loads(args.file.read_text(encoding="utf-8-sig")), "description": args.description}
        if args.build_file:
            body["buildFiles"] = args.build_file
        return api.json("/api/v1/test-configs/" + urllib.parse.quote(args.name, safe=""), "POST", body)
    if command in ("upload", "select"):
        selected = selection(api, args.tests)
        if args.start and not selected:
            raise ClientError("tests_required", "--start requires at least one explicitly selected test.")
        sha = upload_application(api, args.directory.resolve(), args.exe, args.id) if command == "upload" else None
        application = args.id if command == "upload" else args.application
        tests = select_tests(api, application, args.id, selected, args.start)
        return {"application": application, "sha256": sha, "startRequested": args.start, "tests": tests}
    if command == "start":
        return {"tests": [api.json("/api/v1/test-runs/" + urllib.parse.quote(value, safe="") + "/start", "POST", {}) for value in args.ids]}
    return api.json("/api/v1/test-runs/" + urllib.parse.quote(args.id, safe=""))


def main() -> int:
    try:
        value = execute(parser().parse_args())
        print(value if isinstance(value, str) else json.dumps(value, separators=(",", ":")))
        return 0
    except ClientError as error:
        print(json.dumps(error.document(), separators=(",", ":")))
        return 1
    except (OSError, ValueError, KeyError, TypeError, http.client.HTTPException) as error:
        print(json.dumps({"ok": False, "code": "test_client_error", "error": str(error),
                          "hint": "Inspect the same IDs. Upload/select never imply start; --start may already have queued some requests."}, separators=(",", ":")))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
