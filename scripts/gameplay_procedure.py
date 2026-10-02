#!/usr/bin/env python3
"""Read-only check that a saved gameplay test uses a pinned input procedure."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import urllib.parse

from runner_transport import ClientError, RunnerApi


DEFAULT_CONTRACT = Path(__file__).resolve().parents[1] / "procedures" / "pgr2-parked-v1.json"
HEX_REVISION = re.compile(r"[0-9a-fA-F]{64}\Z")


def load_contract(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if value.get("schemaVersion") != 1 or not value.get("id") or not isinstance(value.get("planPrefix"), list):
        raise ValueError("Unsupported or incomplete gameplay procedure contract.")
    if value.get("measurementMs", 0) <= 0 or value.get("minimumFrameSamples", 0) <= 0:
        raise ValueError("Procedure measurement and frame evidence must be positive.")
    if not value["planPrefix"] or value["planPrefix"][-1].get("type") != "segment_start":
        raise ValueError("Procedure must end its input prefix at segment_start.")
    return value


def _step(value: dict) -> dict:
    kind = value.get("type")
    if kind == "wait":
        return {"type": kind, "delayMs": value.get("delayMs")}
    if kind == "button":
        return {"type": kind, "button": value.get("button"), "durationMs": value.get("durationMs")}
    if kind in ("segment_start", "segment_end"):
        return {"type": kind, "name": value.get("name")}
    if kind == "screenshot":
        return {"type": kind, "name": value.get("name")}
    return {"type": kind}


def _identity(contract: dict) -> str:
    parts = {key: contract[key] for key in ("schemaVersion", "id", "planPrefix", "measurementMs", "minimumFrameSamples")}
    return hashlib.sha256(json.dumps(parts, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def assess(job: dict, contract: dict) -> dict:
    """Compare semantic inputs and measurement boundaries, excluding host paths/builds."""
    errors = []
    plan = job.get("plan")
    if not isinstance(plan, list):
        plan = []
        errors.append("Saved definition has no plan.")
    if not job.get("requireInput"):
        errors.append("Gameplay input is not required by this definition.")
    if plan and plan[0].get("type") == "require_input":
        plan = plan[1:]
    starts = [i for i, step in enumerate(plan) if step.get("type") == "segment_start"]
    if len(starts) != 1:
        errors.append("Expected one stationary-start measurement boundary.")
        start = len(plan)
    else:
        start = starts[0]
    observed_prefix = [_step(step) for step in plan[:start + 1]]
    expected_prefix = [_step(step) for step in contract["planPrefix"]]
    if observed_prefix != expected_prefix:
        errors.append("Input sequence, holds, waits, or measurement start differs from the pinned parked course.")

    after_start = plan[start + 1:]
    ends = [i for i, step in enumerate(after_start) if step.get("type") == "segment_end"]
    if len(ends) != 1:
        errors.append("Expected one stationary-start measurement end.")
        measured = after_start
        after_end = []
    else:
        measured = after_start[:ends[0]]
        after_end = after_start[ends[0] + 1:]
        if after_start[ends[0]].get("name") != expected_prefix[-1].get("name"):
            errors.append("Measurement end names another segment.")
    waits = [step.get("delayMs") for step in measured if step.get("type") == "wait"]
    if waits != [contract["measurementMs"]]:
        errors.append("Stationary measurement must contain exactly the pinned 30-second wait.")
    expected_measurement = [
        {"type": "screenshot", "name": "recording-start"},
        {"type": "wait", "delayMs": contract["measurementMs"]},
        {"type": "screenshot", "name": "recording-end"},
    ]
    if [_step(step) for step in measured] != expected_measurement:
        errors.append("Stationary measurement must retain start/end scene screenshots around its wait.")
    if any(step.get("type") not in ("wait", "screenshot") for step in measured):
        errors.append("Gameplay input or another action occurred during stationary measurement.")
    if any(step.get("type") in ("button", "controller_state") for step in after_end):
        errors.append("Gameplay input occurred after the stationary measurement.")
    analysis = job.get("workload", {}).get("analysis") or {}
    if analysis.get("segment") != expected_prefix[-1].get("name"):
        errors.append("Performance analysis does not select the parked measurement segment.")
    if analysis.get("minimumFrameSamples", 0) < contract["minimumFrameSamples"]:
        errors.append("Guest-frame evidence minimum is below the pinned contract.")
    if not analysis.get("guestFramesPath"):
        errors.append("Performance analysis has no guest-frame log path.")
    if any(step.get("button") in ("RTrigger", "LTrigger") for step in plan):
        errors.append("Acceleration or braking input is forbidden in the parked course.")
    return {
        "ok": not errors,
        "procedureId": contract["id"],
        "procedureSha256": _identity(contract),
        "buttonCount": sum(step.get("type") == "button" for step in expected_prefix),
        "transport": "native-os-gamepad" if job.get("controllerInput") else "host-keyboard",
        "errors": errors,
    }


def _read_saved(api: RunnerApi, test_id: str, revision: str) -> dict:
    if not HEX_REVISION.fullmatch(revision):
        raise ClientError("revision_invalid", "Pin a full 64-character saved-test revision.")
    path = "/api/v1/tests/" + urllib.parse.quote(test_id, safe="") + "/" + revision.lower() + "?view=definition"
    value = api.json(path)
    return value["definition"]["job"]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--contract", type=Path, default=DEFAULT_CONTRACT)
    parser.add_argument("--url", default=os.environ.get("XEMU_RUNNER_URL"))
    parser.add_argument("--test-id", required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--other-url")
    parser.add_argument("--other-test-id")
    parser.add_argument("--other-revision")
    args = parser.parse_args()
    try:
        contract = load_contract(args.contract)
        checks = []
        for url, test_id, revision in [(args.url, args.test_id, args.revision),
                                       (args.other_url, args.other_test_id, args.other_revision)]:
            if url is None and test_id is None and revision is None:
                continue
            if not url or not test_id or not revision:
                raise ClientError("arguments_invalid", "Every host check requires URL, test ID, and full revision.")
            job = _read_saved(RunnerApi(url), test_id, revision)
            checks.append({"testId": test_id, "revision": revision.lower(), **assess(job, contract)})
        if not checks:
            raise ClientError("arguments_invalid", "Specify at least one saved test.")
        result = {"ok": all(check["ok"] for check in checks), "checks": checks,
                  "framePacing": "not established by this procedure check"}
        print(json.dumps(result, separators=(",", ":")))
        return 0 if result["ok"] else 1
    except (ClientError, ValueError, KeyError) as error:
        if isinstance(error, ClientError):
            print(json.dumps(error.document(), separators=(",", ":")))
        else:
            print(json.dumps({"ok": False, "error": str(error)}, separators=(",", ":")))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
