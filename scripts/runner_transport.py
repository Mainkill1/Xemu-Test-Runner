"""HTTP transport and bounded file transfers for the agent-side client."""
from __future__ import annotations

import hashlib
import json
import math
import os
from pathlib import Path, PurePosixPath
import re
import time
from typing import Any
import urllib.error
import urllib.parse
import urllib.request


class ClientError(RuntimeError):
    def __init__(self, code: str, message: str, hint: str = "", status: int | None = None):
        super().__init__(message)
        self.code, self.hint, self.status = code, hint, status

    def document(self) -> dict:
        result = {"ok": False, "code": self.code, "error": str(self)[:1024], "hint": self.hint[:512]}
        if self.status is not None:
            result["status"] = self.status
        return result


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class RunnerApi:
    def __init__(self, url: str, timeout: float = 60):
        parsed = urllib.parse.urlsplit(url or "")
        if parsed.scheme not in ("http", "https") or not parsed.netloc or parsed.username or parsed.password:
            raise ClientError("runner_url_invalid", "Set --url or XEMU_RUNNER_URL to the runner HTTP(S) origin.")
        if parsed.path not in ("", "/") or parsed.query or parsed.fragment:
            raise ClientError("runner_url_invalid", "The runner URL must not contain a path, query or fragment.")
        if not math.isfinite(timeout) or timeout <= 0:
            raise ClientError("timeout_invalid", "--timeout must be a positive finite number.")
        self.url, self.timeout = url.rstrip("/"), timeout
        self.opener = urllib.request.build_opener(NoRedirect())
        self.discovery: dict | None = None

    def open(self, path: str, method: str = "GET", data: bytes | None = None,
             headers: dict[str, str] | None = None):
        if not path.startswith("/") or path.startswith("//") or "\\" in path:
            raise ClientError("api_path_invalid", "API URLs must stay relative to the configured origin.")
        request = urllib.request.Request(self.url + path, data=data, method=method, headers=headers or {})
        try:
            return self.opener.open(request, timeout=self.timeout)
        except urllib.error.HTTPError as error:
            with error:
                raw = error.read(16384)
            if 300 <= error.code < 400:
                raise ClientError("redirect_rejected", "The runner returned a redirect.",
                                  "Use the actual runner origin; redirects are not followed.", error.code) from error
            try:
                value = json.loads(raw)
                if not isinstance(value, dict):
                    raise ValueError("Error response is not an object")
            except (ValueError, UnicodeDecodeError):
                value = {"error": raw.decode("utf-8", "replace")[:1024]}
            raise ClientError(str(value.get("code", "http_error")), str(value.get("error", "HTTP request failed.")),
                              str(value.get("hint", "Inspect this operation before retrying with the same ID.")), error.code) from error

    def json(self, path: str, method: str = "GET", body: Any = None,
             headers: dict[str, str] | None = None) -> Any:
        payload = None if body is None else json.dumps(body, separators=(",", ":"), allow_nan=False).encode()
        request_headers = {"Accept": "application/json", **(headers or {})}
        if payload is not None:
            request_headers["Content-Type"] = "application/json"
        with self.open(path, method, payload, request_headers) as response:
            raw = response.read(16 * 1024 * 1024 + 1)
        if len(raw) > 16 * 1024 * 1024:
            raise ClientError("response_too_large", "API JSON exceeds the 16 MiB transport limit.")
        return json.loads(raw) if raw else None

    def require(self, *capabilities: str) -> dict:
        if self.discovery is None:
            self.discovery = self.json("/api/v1/agent?view=summary")
        available = self.discovery.get("capabilities", []) if isinstance(self.discovery, dict) else []
        missing = [name for name in capabilities if name not in available]
        if missing:
            raise ClientError("capability_missing", "Runner lacks: " + ", ".join(missing),
                              "Deploy a compatible runner. Do not replace missing API operations with SSH.")
        return self.discovery

    def upload(self, job_id: str, root: Path, item: dict, chunk_bytes: int = 8 * 1024 * 1024) -> None:
        path = job_url(job_id) + "/files/" + encode_path(item["Path"])
        self.upload_path(path, inside(root, item["Path"]), item["Length"], item["Sha256"], chunk_bytes)

    def upload_path(self, path: str, source_path: Path, total: int, sha256: str,
                    chunk_bytes: int = 8 * 1024 * 1024) -> None:
        if total < 0 or source_path.is_symlink() or not source_path.is_file():
            raise ClientError("source_invalid", "Upload source must be a regular file with a non-negative declared length.")
        if source_path.stat().st_size != total:
            raise ClientError("source_changed", "Local payload length differs from its declaration.")
        failures = 0
        with source_path.open("rb") as source:
            while True:
                status = self.json(path + "?upload-status=1")
                if status["complete"] and not status["partial"] and status["length"] == total:
                    return
                if total == 0:
                    headers = {"Content-Type": "application/octet-stream", "X-Content-SHA256": sha256}
                    with self.open(path, "PUT", b"", headers) as response:
                        receipt = json.loads(response.read(65536))
                    if not receipt.get("complete"):
                        raise ClientError("publication_unconfirmed", "Zero-byte upload was not published.",
                                          "Inspect upload status and retry the same identity.")
                    return
                offset = int(status["length"]) if status["partial"] else 0
                if offset < 0 or offset > total:
                    raise ClientError("upload_offset_invalid", "Server returned an invalid committed upload offset.")
                if offset == total:
                    raise ClientError("publication_unconfirmed", "All bytes arrived but publication is unconfirmed.",
                                      "Inspect upload status and retry the same asset/job identity.")
                source.seek(offset)
                data = source.read(min(chunk_bytes, total - offset))
                if not data:
                    raise ClientError("source_changed", "Local payload became shorter after hashing.")
                headers = {"Content-Type": "application/octet-stream", "X-Content-SHA256": sha256,
                           "Content-Range": f"bytes {offset}-{offset + len(data) - 1}/{total}"}
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
                        raise ClientError("upload_interrupted", "Upload did not finish.",
                                          "Rerun the identical command with the SAME ID and unchanged local file.") from error
                    time.sleep(failures)

    def download(self, path: str, target: Path, expected: int) -> None:
        if expected < 0:
            raise ClientError("artifact_length_invalid", "Artifact has a negative byte count.")
        target.parent.mkdir(parents=True, exist_ok=True)
        partial = target.with_name(target.name + ".part")
        if target.is_symlink() or partial.is_symlink():
            raise ClientError("artifact_path_invalid", "Artifact output must not be a symlink.")
        if target.exists():
            if target.stat().st_size == expected:
                return
            raise ClientError("artifact_length_mismatch", "Existing artifact has a different length.", "Move the old local file aside before collecting again.")
        offset = partial.stat().st_size if partial.exists() else 0
        if offset > expected:
            raise ClientError("artifact_length_mismatch", "Partial artifact is larger than the server file.")
        if offset == expected and partial.exists():
            partial.replace(target)
            return
        headers = {"Range": f"bytes={offset}-"} if offset else {}
        with self.open(path, headers=headers) as response:
            append = response.status == 206 and offset > 0
            if append:
                match = re.fullmatch(r"bytes (\d+)-(\d+)/(\d+)", response.headers.get("Content-Range", ""))
                if not match or tuple(map(int, match.groups())) != (offset, expected - 1, expected):
                    raise ClientError("artifact_range_invalid", "Resumed range does not match the expected artifact.")
            elif response.status != 200:
                raise ClientError("artifact_range_invalid", "Unexpected artifact HTTP status.")
            written = offset if append else 0
            with partial.open("ab" if append else "wb") as output:
                while True:
                    block = response.read(min(1024 * 1024, expected - written + 1))
                    if not block:
                        break
                    if written + len(block) > expected:
                        raise ClientError("artifact_length_mismatch", "Server sent more artifact bytes than declared.")
                    output.write(block)
                    written += len(block)
        if partial.stat().st_size != expected:
            raise ClientError("artifact_incomplete", "Artifact download ended early.", "Rerun collect to resume its .part file.")
        partial.replace(target)


def job_url(value: str) -> str:
    return "/api/v1/jobs/" + urllib.parse.quote(value, safe="")


def run_url(value: str) -> str:
    return "/api/v1/runs/" + urllib.parse.quote(value, safe="")


def encode_path(value: str) -> str:
    return "/".join(urllib.parse.quote(part, safe="") for part in value.split("/"))


def inside(root: Path, name: str) -> Path:
    parts = PurePosixPath(name)
    if not name or parts.is_absolute() or any(part in (".", "..") for part in name.split("/")) or "\\" in name or ":" in name:
        raise ClientError("file_path_invalid", "Use a package-relative forward-slash path.")
    base = root.resolve()
    path = base.joinpath(*parts.parts)
    current = base
    for part in parts.parts:
        current /= part
        if current.is_symlink():
            raise ClientError("file_path_invalid", "Linked payload/output paths are not supported.")
    if not path.resolve().is_relative_to(base):
        raise ClientError("file_path_invalid", "Path escapes its selected directory.")
    return path


def declaration(root: Path, name: str, executable: bool = False) -> dict:
    path = inside(root, name)
    before = path.stat()
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    after = path.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise ClientError("source_changed", "Payload changed while hashing: " + name)
    return {"Path": name, "Length": after.st_size, "Sha256": digest.hexdigest(),
            "Executable": executable or bool(after.st_mode & 0o111)}


def manifest(root: Path, job_id: str) -> dict:
    job = json.loads(inside(root, "job.json").read_text(encoding="utf-8-sig"))
    if not isinstance(job, dict):
        raise ClientError("plan_invalid", "job.json must be a JSON object.")
    for key in list(job):
        if key.lower() == "id":
            del job[key]
    job["Id"] = job_id
    executable = job.get("Executable", job.get("executable", "")).replace("\\", "/").removeprefix("./")
    files = []
    for directory, children, names in os.walk(root, followlinks=False):
        children[:] = sorted(name for name in children if not name.startswith("."))
        for child in children:
            if (Path(directory) / child).is_symlink():
                raise ClientError("file_path_invalid", "Package symlinks are not supported.")
        for name in sorted(names):
            relative = (Path(directory) / name).relative_to(root).as_posix()
            if name.startswith(".") or relative == "job.json":
                continue
            files.append(declaration(root, relative, relative == executable))
    return {"Id": job_id, "Job": job, "Files": files}
