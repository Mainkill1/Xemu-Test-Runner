#!/usr/bin/env python3
"""Operate an already-running tester over HTTP from the agent/build machine.

Workflow: list -> show -> upload -> start -> wait -> result.
Upload/select only store inputs unless --start is explicit. Keep
runner_transport.py, runner_test_results.py and runner_wait.py beside this file.
"""
from __future__ import annotations

import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import urllib.parse

from runner_transport import ClientError, RunnerApi, declaration, inside
import runner_test_results
import runner_wait


class Parser(argparse.ArgumentParser):
    def error(self, message):
        raise ClientError("arguments_invalid", message, "Use --help for the supported test workflow.")


# Argument definitions: normal test operation first, catalog maintenance second.
def _command(subparsers, name: str, description: str):
    return subparsers.add_parser(name, help=description, description=description)


def _register_test_commands(subparsers) -> None:
    _command(subparsers, "list", "List saved test names and immutable revisions.")
    show = _command(subparsers, "show", "Inspect a saved test configuration without running it.")
    show.add_argument("test", help="NAME, or NAME@FULL_REVISION when the name is ambiguous.")
    show.add_argument("--out", type=Path, help="Save configuration JSON to a new local file.")

    config = _command(subparsers, "config-upload", "Save a named configuration; does not start a test.")
    config.add_argument("name", help="Name for the saved configuration.")
    config.add_argument("file", type=Path, help="Local JobDefinition JSON file.")
    config.add_argument("--assets", required=True, help="Retained API job containing the required test assets.")
    config.add_argument("--description", default="")
    config.add_argument("--build-file", action="append", help="Replaceable build path; repeat for dependencies.")

    upload = _command(subparsers, "upload", "Upload one application; does not start tests unless --start is given.")
    upload.add_argument("directory", type=Path, help="Application directory, including its build dependencies.")
    upload.add_argument("--exe", required=True, help="Executable path relative to that directory.")
    upload.add_argument("--id", required=True, help="Stable application upload ID; reuse after a lost response.")
    upload.add_argument("--tests", nargs="+", default=[], help="Saved NAME or NAME@FULL_REVISION selections.")
    upload.add_argument("--start", action="store_true", help="Explicitly queue the selected tests after upload.")

    choose = _command(subparsers, "select", "Select more tests for an uploaded application; does not start by default.")
    choose.add_argument("application", help="Existing application upload ID.")
    choose.add_argument("--id", required=True, help="New request-ID prefix for this set of selections.")
    choose.add_argument("--tests", nargs="+", required=True, help="Saved NAME or NAME@FULL_REVISION selections.")
    choose.add_argument("--start", action="store_true", help="Explicitly queue these selections.")

    start = _command(subparsers, "start", "Explicitly queue previously selected test requests.")
    start.add_argument("ids", nargs="+", help="Request IDs returned by upload/select, not executable hashes.")
    status = _command(subparsers, "status", "Read a test request's current state without waiting.")
    status.add_argument("id", help="Test request ID returned by upload/select.")


def _register_disk_commands(subparsers) -> None:
    _command(subparsers, "disk-list", "List one page of shared HDD assets; does not download disks.")
    show = _command(subparsers, "disk-show", "Inspect a shared disk asset and upload state.")
    show.add_argument("id", help="Catalog asset ID.")
    upload = _command(subparsers, "disk-upload", "Upload a shared HDD once; never starts a test.")
    upload.add_argument("id", help="Immutable catalog asset ID.")
    upload.add_argument("file", type=Path, help="Local prepared disk image.")
    upload.add_argument("--kind", choices=("xiso-seed", "snapshot-carrier"), required=True)
    upload.add_argument("--description")
    import_disk = _command(subparsers, "disk-import", "Import an existing tester-side HDD without a network reupload.")
    import_disk.add_argument("id", help="Catalog asset ID.")
    import_disk.add_argument("--from-job", required=True, help="Retained source API job ID.")
    import_disk.add_argument("--path", required=True, help="Declared disk path inside the source package.")
    import_disk.add_argument("--kind", choices=("xiso-seed", "snapshot-carrier"), required=True)
    import_disk.add_argument("--description")
    delete = _command(subparsers, "disk-delete", "Delete an unreferenced shared disk; referenced assets are refused.")
    delete.add_argument("id", help="Catalog asset ID.")


def parser() -> argparse.ArgumentParser:
    root = Parser(description=__doc__)
    root.add_argument("--url", default=os.environ.get("XEMU_RUNNER_URL"),
                      help="Tester origin; defaults to XEMU_RUNNER_URL.")
    root.add_argument("--json", action="store_true", help="Return JSON instead of formatted configs/results.")
    commands = root.add_subparsers(dest="command", required=True, metavar="COMMAND")
    _register_test_commands(commands)
    runner_wait.register(commands)
    runner_test_results.register(commands)
    _register_disk_commands(commands)
    return root


# Dispatch stays short: each family checks its own server capability.
def execute(args: argparse.Namespace) -> dict | str:
    api = RunnerApi(args.url)
    if args.command == "wait":
        return runner_wait.execute(api, args)
    if args.command in runner_test_results.COMMANDS:
        return runner_test_results.execute(api, args)
    if args.command.startswith("disk-"):
        return _execute_disk_command(api, args)
    return _execute_test_command(api, args)


# Test configuration and selection. These helpers do not execute local xemu.
def catalog(api: RunnerApi) -> list[dict]:
    """Read all bounded catalog pages so name resolution never ignores a revision."""
    tests = []
    offset = 0
    while offset is not None:
        page = api.json(f"/api/v1/test-configs?limit=100&offset={offset}")
        tests.extend(page["items"])
        next_offset = page.get("nextOffset")
        if next_offset is not None and next_offset <= offset:
            raise ClientError("catalog_invalid", "Catalog pagination did not advance.")
        if len(tests) > 10000:
            raise ClientError("catalog_limit", "Use the paged API for catalogs larger than 10000 revisions.")
        offset = next_offset
    return tests


def selection(api: RunnerApi, selectors: list[str]) -> list[tuple[str, str]]:
    """Resolve names to pinned revisions before upload or request creation."""
    available = None
    selected = []
    for selector in selectors:
        if "@" in selector:
            name, revision = selector.rsplit("@", 1)
            if len(revision) != 64 or any(ch not in "0123456789abcdefABCDEF" for ch in revision):
                raise ClientError("revision_invalid", "Use a complete SHA-256 test revision after @.")
            api.json("/api/v1/tests/" + urllib.parse.quote(name, safe="") + "/" + revision)
        else:
            available = catalog(api) if available is None else available
            matches = [test for test in available if test["id"] == selector]
            if len(matches) != 1:
                raise ClientError("test_selection_ambiguous", "Test name is missing or has multiple revisions: " + selector,
                                  "List tests, then use NAME@FULL_REVISION.")
            name, revision = selector, matches[0]["revision"]
        selected.append((name, revision.lower()))
    return selected


def select_tests(api: RunnerApi, application: str, prefix: str,
                 selected: list[tuple[str, str]], start: bool) -> list[dict]:
    """Save every selection before publishing any explicitly requested start."""
    if len(prefix) > 54 or len(selected) > 256:
        raise ClientError("selection_limit", "Use a prefix of at most 54 characters and at most 256 tests.")
    requests = []
    for number, (name, revision) in enumerate(selected, 1):
        request_id = f"{prefix}-t{number:03}"
        reply = api.json("/api/v1/test-runs", "POST", {
            "id": request_id, "applicationJobId": application, "testId": name, "revision": revision,
        })
        requests.append({"id": request_id, "test": name, "revision": revision, "state": reply["state"]})
    if start:
        # Starts are separate durable requests, not an all-or-nothing batch.
        for request in requests:
            reply = api.json("/api/v1/test-runs/" + request["id"] + "/start", "POST", {})
            request["state"] = reply["state"]
    return requests


def upload_application(api: RunnerApi, root: Path, executable: str, identity: str) -> str:
    """Declare and upload build files; never submit the application container."""
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
    executable_file = next((file for file in files if file["Path"] == executable), None)
    if executable_file is None:
        raise ClientError("executable_missing", "--exe is not a regular application file.")
    api.json("/api/v1/jobs", "POST", {
        "id": identity,
        "job": {"id": identity, "executable": executable,
                "expectedExecutableSha256": executable_file["Sha256"], "plan": []},
        "files": files,
    })
    for file in files:
        api.upload(identity, root, file)
    return executable_file["Sha256"]


def _show_config(api: RunnerApi, args: argparse.Namespace) -> dict | str:
    name, revision = selection(api, [args.test])[0]
    saved = api.json("/api/v1/test-configs/" + urllib.parse.quote(name, safe="") + "/" + revision)
    job = saved["definition"]["job"]
    if args.out:
        with args.out.open("x", encoding="utf-8") as file:
            json.dump(job, file, indent=2)
        return {"id": name, "revision": revision, "file": str(args.out)}
    return saved if args.json else json.dumps(job, indent=2)


def _save_config(api: RunnerApi, args: argparse.Namespace) -> dict:
    if args.file.stat().st_size > 1024 * 1024:
        raise ClientError("config_too_large", "Configuration exceeds 1 MiB.")
    body = {"sourceJobId": args.assets,
            "job": json.loads(args.file.read_text(encoding="utf-8-sig")), "description": args.description}
    if args.build_file:
        body["buildFiles"] = args.build_file
    return api.json("/api/v1/test-configs/" + urllib.parse.quote(args.name, safe=""), "POST", body)


def _prepare_requested_tests(api: RunnerApi, args: argparse.Namespace, capabilities=()) -> dict:
    if len(args.id) > 54 or len(args.tests) > 256:
        raise ClientError("selection_limit", "Use an ID up to 54 characters and at most 256 selected tests.")
    selected = selection(api, args.tests)
    if args.start and not selected:
        raise ClientError("tests_required", "--start requires at least one explicitly selected test.")
    executable_sha = None
    application_id = args.application if args.command == "select" else args.id
    if args.command == "upload":
        executable_sha = upload_application(api, args.directory.resolve(), args.exe, application_id)
    identity_fields = {}
    if "applicationIdentity" in capabilities:
        # One small server declaration read, before starts. Never download a
        # native binary to distinguish it from a launcher script on the client.
        identity = api.json("/api/v1/applications/" + urllib.parse.quote(application_id, safe=""))
        executable_sha = runner_test_results.sha(identity["sha256"])
        identity_fields = {"launchSha256": runner_test_results.sha(identity["launchSha256"]),
                           "executable": identity["executable"], "launchExecutable": identity["launchExecutable"]}
    requests = select_tests(api, application_id, args.id, selected, args.start)
    return {"application": application_id, "sha256": executable_sha, **identity_fields,
            "startRequested": args.start, "tests": requests}


def _execute_test_command(api: RunnerApi, args: argparse.Namespace) -> dict | str:
    info = api.json("/api/v1/help?topic=test-workflow")
    if not isinstance(info, dict) or "requestedTests" not in info.get("capabilities", []):
        raise ClientError("capability_missing", "The tester does not support explicit test requests.",
                          "Deploy the matching runner version; upload is not replaced with submit.")
    if args.command == "list":
        return {"tests": catalog(api)}
    if args.command == "show":
        return _show_config(api, args)
    if args.command == "config-upload":
        return _save_config(api, args)
    if args.command in ("upload", "select"):
        return _prepare_requested_tests(api, args, info.get("capabilities", []))
    if args.command == "start":
        replies = [api.json("/api/v1/test-runs/" + urllib.parse.quote(request_id, safe="") + "/start", "POST", {}) for request_id in args.ids]
        return {"tests": replies}
    return api.json("/api/v1/test-runs/" + urllib.parse.quote(args.id, safe=""))


# Disk catalog maintenance is separate from test selection and start permission.
def _import_disk(api: RunnerApi, args: argparse.Namespace) -> dict:
    source_job = api.json("/api/v1/jobs/" + urllib.parse.quote(args.from_job, safe=""))
    source_file = next((file for file in source_job.get("files", [])
                        if file.get("path", file.get("Path")) == args.path), None)
    if source_file is None:
        raise ClientError("disk_source_missing", "The source job does not declare that file.")
    length = source_file.get("length", source_file.get("Length"))
    sha256 = source_file.get("sha256", source_file.get("Sha256"))
    api.json("/api/v1/disk-assets", "POST", {
        "id": args.id, "kind": args.kind, "length": length, "sha256": sha256,
        **({"description": args.description} if args.description is not None else {}),
    })
    return api.json("/api/v1/disk-assets/" + urllib.parse.quote(args.id, safe="") + "/import", "POST",
                    {"sourceJobId": args.from_job, "path": args.path})


def _upload_disk(api: RunnerApi, args: argparse.Namespace) -> dict:
    source = args.file.resolve()
    if source.is_symlink() or not source.is_file():
        raise ClientError("source_invalid", "Disk asset source must be a regular file.")
    before = source.stat()
    digest = hashlib.sha256()
    with source.open("rb") as file:
        for block in iter(lambda: file.read(1024 * 1024), b""):
            digest.update(block)
    after = source.stat()
    if before.st_size <= 0 or (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise ClientError("source_changed", "Disk asset source changed while hashing.")
    sha256 = digest.hexdigest()
    api.json("/api/v1/disk-assets", "POST", {
        "id": args.id, "kind": args.kind, "length": after.st_size, "sha256": sha256,
        **({"description": args.description} if args.description is not None else {}),
    })
    asset_url = "/api/v1/disk-assets/" + urllib.parse.quote(args.id, safe="")
    api.upload_path(asset_url + "/content", source, after.st_size, sha256)
    return {**api.json(asset_url), "sha256": sha256}


def _execute_disk_command(api: RunnerApi, args: argparse.Namespace) -> dict:
    info = api.json("/api/v1/help?topic=disk-assets")
    if not isinstance(info, dict) or info.get("capability") != "diskAssets":
        raise ClientError("capability_missing", "The tester does not support disk assets.", "Deploy a compatible runner; do not copy HDDs through SSH.")
    if args.command == "disk-list":
        return api.json("/api/v1/disk-assets")
    if args.command == "disk-show":
        return api.json("/api/v1/disk-assets/" + urllib.parse.quote(args.id, safe=""))
    if args.command == "disk-delete":
        return api.json("/api/v1/disk-assets/" + urllib.parse.quote(args.id, safe=""), "DELETE")
    if args.command == "disk-import":
        return _import_disk(api, args)
    return _upload_disk(api, args)


def main() -> int:
    """Write one report or structured error. Only wait --updates streams progress."""
    try:
        value = execute(parser().parse_args())
        print(value if isinstance(value, str) else json.dumps(value, separators=(",", ":")))
        return 0
    except ClientError as error:
        print(json.dumps(error.document(), separators=(",", ":")))
        return 1
    except KeyboardInterrupt:
        print(json.dumps({"ok": False, "code": "client_interrupted", "hint": "Inspect the same request IDs; a requested start may already be queued."}))
        return 130
    except (OSError, ValueError, KeyError, TypeError, http.client.HTTPException) as error:
        print(json.dumps({"ok": False, "code": "test_client_error", "error": str(error)[:1024],
                          "hint": "Inspect the same IDs. Upload/select never imply start; --start may already have queued some requests."}, separators=(",", ":")))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
