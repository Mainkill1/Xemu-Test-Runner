"""Thin report commands. The tester owns analysis; the client only renders its response."""
from __future__ import annotations

import hashlib
import ipaddress
from pathlib import Path
import urllib.parse
from runner_transport import ClientError, RunnerApi, run_url

COMMANDS = {"result", "compare", "baseline", "index", "csv", "diagnostics", "state", "performance", "connect"}


def register(sub) -> None:
    sub.add_parser("connect", help="Check HTTP access from this agent machine; identify accidental loopback use.")
    performance = sub.add_parser("performance", help="Read the runner's saved CPU/cadence/frame-tail analysis; no raw download.")
    performance.add_argument("run_id")
    performance.add_argument("--format", choices=("json", "markdown"))
    performance.add_argument("--out", type=Path)
    result = sub.add_parser("result", help="Compact stored results with the default baseline comparison.")
    result.add_argument("sha256")
    result.add_argument("--format", choices=("json", "markdown"))
    result.add_argument("--out", type=Path)
    compare = sub.add_parser("compare", help="Ask the tester to compare executable hashes; A defaults to the pinned baseline.")
    compare.add_argument("--a")
    compare.add_argument("--b", required=True)
    compare.add_argument("--format", choices=("json", "markdown", "csv"))
    compare.add_argument("--out", type=Path)
    baseline = sub.add_parser("baseline", help="Show the current pin; supplying SHA256 explicitly replaces it.")
    baseline.add_argument("sha256", nargs="?")
    index = sub.add_parser("index", help="Index existing canonical archived evidence; never execute a test.")
    index.add_argument("run_id")
    state = sub.add_parser("state", help="Inspect cache policies, actual paths and before/after state summaries.")
    state.add_argument("run_id")
    diagnostics = sub.add_parser("diagnostics", help="Read a crash/bundle summary; --out explicitly downloads its verified ZIP.")
    diagnostics.add_argument("run_id")
    diagnostics.add_argument("--out", type=Path)
    csv = sub.add_parser("csv", help="Explicitly download a run's original metrics.csv.")
    csv.add_argument("run_id")
    csv.add_argument("output", type=Path)


def sha(value: str) -> str:
    if len(value) != 64 or any(c not in "0123456789abcdefABCDEF" for c in value):
        raise ClientError("hash_invalid", "Use the complete executable SHA-256, not its filename or a short prefix.")
    return value.lower()


def _connect(api: RunnerApi) -> dict:
    try:
        info = api.json("/api/v1/agent?view=summary")
    except (OSError, ValueError) as error:
        raise ClientError("runner_unreachable", str(error)[:512],
                          "Run connect on the agent/build machine against the tester's LAN URL. Check routing, listener bind and firewall; do not substitute SSH execution.") from error
    if not isinstance(info, dict) or info.get("api") != "xemu-test-runner":
        raise ClientError("runner_identity_invalid", "The HTTP endpoint is not the expected tester.")
    hostname = urllib.parse.urlsplit(api.url).hostname or ""
    try:
        loopback = ipaddress.ip_address(hostname).is_loopback
    except ValueError:
        loopback = hostname.lower() == "localhost" or hostname.lower().endswith(".localhost")
    return {"reachable": True, "runner": api.url, "version": info.get("version"),
            "instance": info.get("instance"), "capabilities": info.get("capabilities", []),
            "loopback": loopback,
            "hint": "This reaches the local machine. Remote agents must use the tester's LAN address, not run this helper through SSH." if loopback else
                    "HTTP works from this agent machine. Use this URL for upload/select/wait/report; no remote shell is needed."}


def _report(api: RunnerApi, args, path: str, format_name: str, limit: int):
    if format_name == "json" and args.out is None:
        return api.json(path)
    with api.open(path) as response:
        data = response.read(limit + 1)
    if len(data) > limit:
        raise ClientError("report_too_large", "Server report exceeds the client response limit.")
    text = data.decode("utf-8")
    if args.out:
        with args.out.open("x", encoding="utf-8", newline="") as output:
            output.write(text)
        return {"file": str(args.out), "bytes": len(data), "format": format_name}
    return text


def execute(api: RunnerApi, args):
    if args.command == "connect":
        return _connect(api)
    if args.command == "performance":
        format_name = args.format or ("json" if args.json else "markdown")
        return _report(api, args, run_url(args.run_id) + "/performance?format=" + format_name, format_name, 65536)
    if args.command == "state":
        return api.json(run_url(args.run_id) + "/state")
    if args.command == "diagnostics":
        value = api.json(run_url(args.run_id) + "/diagnostics")
        if args.out is None:
            return value
        bundle = value.get("bundle") or {}
        digest = bundle.get("sha256")
        if not bundle.get("href") or not digest or bundle.get("state") not in ("captured", "partial"):
            raise ClientError("diagnostic_bundle_unavailable", "The ZIP is not finalized or collection failed.", "Read the diagnostic summary; this does not change the test outcome or rerun it.")
        digest = sha(digest)
        api.download(run_url(args.run_id) + "/artifacts/diagnostics.zip", args.out, bundle["bytes"])
        actual = hashlib.sha256()
        with args.out.open("rb") as archive:
            for block in iter(lambda: archive.read(1024 * 1024), b""):
                actual.update(block)
        if actual.hexdigest() != digest:
            raise ClientError("diagnostic_hash_mismatch", "Downloaded ZIP differs from the runner's finalized receipt.", "Keep the file for inspection or move it aside before retrying; no verified-download claim was made.")
        return {"ok": True, "runId": args.run_id, "file": str(args.out), "bytes": bundle["bytes"], "sha256": digest, "bundleState": bundle["state"]}
    info = api.json("/api/v1/help?topic=build-results")
    if not isinstance(info, dict) or "executableHashResults" not in info.get("capabilities", []):
        raise ClientError("capability_missing", "The tester does not support executable-hash results.", "Deploy the matching runner version; no client-side comparison fallback is used.")
    if args.command == "baseline":
        return api.json("/api/v1/baseline", "PUT", {"sha256": sha(args.sha256)}) if args.sha256 else api.json("/api/v1/baseline")
    if args.command == "index":
        return api.json("/api/v1/build-results/index", "POST", {"runId": args.run_id})
    if args.command == "csv":
        path = run_url(args.run_id) + "/artifacts/metrics.csv"
        with api.open(path, "HEAD") as response:
            length = response.headers.get("Content-Length")
            if length is None:
                raise ClientError("artifact_length_missing", "The raw CSV has no declared byte count.")
        api.download(path, args.output, int(length))
        return {"runId": args.run_id, "rawCsv": str(args.output), "bytes": int(length)}
    format_name = args.format or ("json" if args.json else "markdown")
    query = {"format": format_name}
    if args.command == "compare":
        query["B"] = sha(args.b)
        if args.a:
            query["A"] = sha(args.a)
        path = "/api/v1/compare?" + urllib.parse.urlencode(query)
    else:
        path = "/api/v1/build-results/" + sha(args.sha256) + "?" + urllib.parse.urlencode(query)
    return _report(api, args, path, format_name, 8 * 1024 * 1024)
