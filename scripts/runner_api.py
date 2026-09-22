#!/usr/bin/env python3
"""Operate a running Xemu Test Runner through HTTP only (Python standard library).

Run this on the agent/build machine, not on the tester. All stdout is JSON;
progress/errors go to stderr. There is deliberately no SSH fallback.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from typing import Any


class ApiError(RuntimeError):
    def __init__(self, status: int, body: str):
        self.status = status
        self.body = body
        super().__init__(f"HTTP {status}: {body}")


class RunnerApi:
    def __init__(self, url: str, timeout: float = 60):
        parsed = urllib.parse.urlsplit(url)
        if parsed.scheme not in ("http", "https") or not parsed.netloc:
            raise ValueError("--url must be an HTTP(S) runner origin")
        if parsed.path not in ("", "/") or parsed.query or parsed.fragment:
            raise ValueError("--url must be an origin without a path, query or fragment")
        self.url = url.rstrip("/")
        self.timeout = timeout

    def open(self, path: str, method: str = "GET", data: bytes | None = None,
             headers: dict[str, str] | None = None):
        # Server links must not redirect the client into a different filesystem
        # or turn a relative API operation into a request to a different origin.
        if not path.startswith("/") or path.startswith("//"):
            raise ValueError("API paths must be origin-relative")
        request = urllib.request.Request(self.url + path, data=data, method=method,
                                         headers=headers or {})
        try:
            return urllib.request.urlopen(request, timeout=self.timeout)
        except urllib.error.HTTPError as error:
            raise ApiError(error.code, error.read(65536).decode("utf-8", "replace")) from error

    def json(self, path: str, method: str = "GET", body: Any = None,
             headers: dict[str, str] | None = None) -> Any:
        payload = None if body is None else json.dumps(body, separators=(",", ":")).encode()
        request_headers = {"Accept": "application/json", **(headers or {})}
        if payload is not None:
            request_headers["Content-Type"] = "application/json"
        with self.open(path, method, payload, request_headers) as response:
            raw = response.read(16 * 1024 * 1024 + 1)
            if len(raw) > 16 * 1024 * 1024:
                raise RuntimeError("API JSON exceeds the client's 16 MiB limit")
            return json.loads(raw) if raw else None

    def wait_operation(self, job_id: str, seconds: float) -> dict:
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            operation = self.json(job_url(job_id) + "/operation")
            if not operation:
                raise RuntimeError("No operation receipt exists for this job")
            if operation["state"] == "completed":
                return operation
            if operation["state"] not in ("queued", "running"):
                raise RuntimeError(json.dumps(operation))
            time.sleep(1)
        raise TimeoutError("Operation still running; poll the same job ID rather than create a duplicate")

    def wait_job(self, job_id: str, seconds: float) -> dict:
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            job = self.json(job_url(job_id))
            if job["state"] in ("tested", "cancelled", "unavailable"):
                return job
            if job["state"] == "draft":
                raise RuntimeError("This job is still a draft; submit it before waiting for a result")
            time.sleep(1)
        raise TimeoutError("Run is not terminal; inspect the same job and /api/v1/status")

    def upload(self, job_id: str, root: Path, declaration: dict, chunk_bytes: int) -> None:
        path = job_url(job_id) + "/files/" + encode_path(declaration["Path"])
        file_path = root / declaration["Path"]
        failures = 0
        with file_path.open("rb") as source:
            while True:
                status = self.json(path + "?upload-status=1")
                total = declaration["Length"]
                if status["complete"] and not status["partial"] and status["length"] == total:
                    return  # Submission revalidates every declared digest.
                offset = int(status["length"]) if status["partial"] else 0
                if offset < 0 or offset > total:
                    raise RuntimeError("Server returned an invalid committed upload offset")
                source.seek(offset)
                # An acknowledged final chunk can precede publication. A fresh
                # whole-file upload or operator inspection is preferable to a
                # bogus zero-length range pretending to finish that session.
                if total > 0 and offset == total:
                    raise RuntimeError("Upload publication is unconfirmed; inspect upload status before retrying")
                data = source.read(min(chunk_bytes, total - offset))
                if not data and total != 0:
                    raise RuntimeError("Source file changed or became shorter after manifest creation")
                headers = {"Content-Type": "application/octet-stream",
                           "X-Content-SHA256": declaration["Sha256"]}
                if total > 0:
                    headers["Content-Range"] = f"bytes {offset}-{offset + len(data) - 1}/{total}"
                    if status.get("uploadId"):
                        headers["X-Upload-Id"] = status["uploadId"]
                try:
                    with self.open(path, "PUT", data, headers) as response:
                        receipt = json.loads(response.read(65536))
                    failures = 0
                    if receipt["complete"]:
                        return
                except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
                    failures += 1
                    if failures >= 4:
                        raise RuntimeError("Upload interrupted; rerun submit with the SAME ID to resume") from error
                    time.sleep(failures)
                    # The next iteration reads the server's committed offset.

    def download(self, api_path: str, output: Path, expected_bytes: int) -> None:
        output.parent.mkdir(parents=True, exist_ok=True)
        partial = output.with_name(output.name + ".part")
        if output.exists():
            if output.stat().st_size == expected_bytes:
                return
            raise RuntimeError(f"Existing local artifact has the wrong length: {output}")
        offset = partial.stat().st_size if partial.exists() else 0
        if offset > expected_bytes:
            raise RuntimeError(f"Partial local artifact is larger than the server artifact: {partial}")
        headers = {"Range": f"bytes={offset}-"} if offset and offset < expected_bytes else {}
        if offset == expected_bytes and partial.exists():
            partial.replace(output)
            return
        with self.open(api_path, headers=headers) as response:
            append = response.status == 206 and offset > 0
            if append and not response.headers.get("Content-Range", "").startswith(f"bytes {offset}-"):
                raise RuntimeError("Download range does not match the local partial offset")
            with partial.open("ab" if append else "wb") as destination:
                while True:
                    block = response.read(1024 * 1024)
                    if not block:
                        break
                    destination.write(block)
        if partial.stat().st_size != expected_bytes:
            raise RuntimeError(f"Incomplete artifact: {partial}; rerun collect to resume")
        partial.replace(output)


def job_url(job_id: str) -> str:
    return "/api/v1/jobs/" + urllib.parse.quote(job_id, safe="")


def encode_path(path: str) -> str:
    return "/".join(urllib.parse.quote(part, safe="") for part in path.split("/"))


def manifest(root: Path, job_id: str) -> dict:
    job = json.loads((root / "job.json").read_text(encoding="utf-8-sig"))
    job.pop("id", None)
    job["Id"] = job_id
    executable = job.get("Executable", job.get("executable", "")).replace("\\", "/")
    files = []
    for directory, children, names in os.walk(root, followlinks=False):
        children[:] = sorted(name for name in children if not name.startswith("."))
        for child in children:
            if (Path(directory) / child).is_symlink():
                raise ValueError("Package symlinks are not supported")
        for name in sorted(names):
            path = Path(directory) / name
            relative = path.relative_to(root).as_posix()
            if name.startswith(".") or relative == "job.json":
                continue
            if path.is_symlink():
                raise ValueError(f"Package symlinks are not supported: {relative}")
            digest = hashlib.sha256()
            before = path.stat()
            with path.open("rb") as source:
                for block in iter(lambda: source.read(1024 * 1024), b""):
                    digest.update(block)
            after = path.stat()
            if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
                raise RuntimeError(f"Package changed while hashing: {relative}")
            files.append({"Path": relative, "Length": after.st_size, "Sha256": digest.hexdigest(),
                          "Executable": relative == executable or bool(after.st_mode & 0o111)})
    return {"Id": job_id, "Job": job, "Files": files}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True)
    parser.add_argument("--timeout", type=float, default=60)
    parser.add_argument("--wait-seconds", type=float, default=1800)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("discover")
    sub.add_parser("jobs")
    for name in ("status", "wait", "withdraw", "cancel"):
        sub.add_parser(name).add_argument("id")
    submit = sub.add_parser("submit")
    submit.add_argument("package", type=Path)
    submit.add_argument("--id", required=True)
    submit.add_argument("--wait", action="store_true")
    submit.add_argument("--chunk-mib", type=int, default=8, choices=range(1, 65))
    edit = sub.add_parser("edit")
    edit.add_argument("id")
    edit.add_argument("plan", type=Path)
    clone = sub.add_parser("clone")
    clone.add_argument("id")
    clone.add_argument("new_id")
    collect = sub.add_parser("collect")
    collect.add_argument("id")
    collect.add_argument("output", type=Path)
    raw = sub.add_parser("request")
    raw.add_argument("method")
    raw.add_argument("path")
    raw.add_argument("--json-file", type=Path)
    args = parser.parse_args()
    api = RunnerApi(args.url, args.timeout)

    if args.command == "discover":
        result = api.json("/api/v1/agent")
    elif args.command == "jobs":
        result = api.json("/api/v1/jobs")
    elif args.command == "status":
        result = api.json(job_url(args.id))
    elif args.command == "wait":
        result = api.wait_job(args.id, args.wait_seconds)
    elif args.command == "withdraw":
        result = api.json(job_url(args.id) + "/withdraw", "POST", {})
    elif args.command == "cancel":
        result = api.json(job_url(args.id), "DELETE")
    elif args.command == "edit":
        current = api.json(job_url(args.id))
        plan = json.loads(args.plan.read_text(encoding="utf-8-sig"))
        plan.pop("id", None)
        plan["Id"] = args.id
        result = api.json(job_url(args.id) + "/plan", "PUT", plan, {"If-Match": current["revision"]})
    elif args.command == "clone":
        api.json(job_url(args.id) + "/clone", "POST", {"newId": args.new_id})
        result = api.wait_operation(args.new_id, args.wait_seconds)
    elif args.command == "submit":
        root = args.package.resolve()
        request = manifest(root, args.id)
        api.json("/api/v1/jobs", "POST", request)
        current = api.json(job_url(args.id))
        if current["state"] == "draft":
            for file in request["Files"]:
                print(f"Uploading/resuming {file['Path']}", file=sys.stderr)
                api.upload(args.id, root, file, args.chunk_mib * 1024 * 1024)
            api.json(job_url(args.id) + "/submit", "POST", {})
            api.wait_operation(args.id, args.wait_seconds)
        elif current["state"] == "busy" and (current.get("operation") or {}).get("action") == "submit":
            api.wait_operation(args.id, args.wait_seconds)
        elif current["state"] not in ("queued", "testing", "tested"):
            raise RuntimeError("Job is not submittable: " + json.dumps(current))
        result = api.wait_job(args.id, args.wait_seconds) if args.wait else api.json(job_url(args.id))
    elif args.command == "collect":
        job = api.json(job_url(args.id))
        if job["state"] != "tested" or not job.get("runId"):
            raise RuntimeError("Collect requires a completed archived attempt; inspect live runs through log-tail/control APIs")
        run_id = job["runId"]
        run_path = "/api/v1/runs/" + urllib.parse.quote(run_id, safe="")
        run = api.json(run_path)
        saved = []
        base = args.output.resolve() / run_id
        for item in run["Artifacts"]:
            relative = PurePosixPath(item["Path"])
            if relative.is_absolute() or ".." in relative.parts or "\\" in item["Path"]:
                raise ValueError("Server returned an unsafe artifact name")
            target = base.joinpath(*relative.parts)
            if not target.resolve().is_relative_to(base):
                raise ValueError("Artifact destination escapes the selected output directory")
            api.download(run_path + "/artifacts/" + encode_path(item["Path"]), target, item["Bytes"])
            saved.append(str(target))
        result = {"jobId": args.id, "runId": run_id, "files": saved}
    else:
        body = json.loads(args.json_file.read_text(encoding="utf-8-sig")) if args.json_file else None
        result = api.json(args.path, args.method.upper(), body)
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, urllib.error.URLError) as error:
        print(json.dumps({"error": str(error)}), file=sys.stderr)
        raise SystemExit(1)
