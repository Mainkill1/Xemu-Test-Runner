#!/usr/bin/env python3
"""Fail closed on common xemu game-test provenance and scene-gate mistakes."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
from typing import Any


SHA256 = re.compile(r"^[0-9a-f]{64}$")
DHASH = re.compile(r"^[0-9a-fA-F]{16}$")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def load(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text())
    require(isinstance(value, dict), f"{path}: root must be an object")
    return value


def sha(value: Any, name: str) -> str:
    require(isinstance(value, str) and SHA256.fullmatch(value) is not None,
            f"{name} must be a lowercase full SHA-256")
    return value


def validate_record(record: dict[str, Any]) -> None:
    require(record.get("schema") == 1, "unsupported record schema")
    require(record.get("purpose") in {
        "state-authoring", "release-acceptance", "diagnostic-profile",
        "crash-investigation",
    }, "unsupported purpose")
    identity = record.get("identity")
    require(isinstance(identity, dict), "identity must be an object")
    for name in ("bios", "xemu", "bootrom", "eeprom", "dvd", "hdd"):
        item = identity.get(name)
        require(isinstance(item, dict), f"identity.{name} must be an object")
        sha(item.get("sha256"), f"identity.{name}.sha256")

    hdd = identity["hdd"]
    if hdd.get("requiresCandidateKeys"):
        require(hdd.get("origin") == "authored-on-candidate",
                "key-bound HDD must be authored on the candidate")
        require(bool(hdd.get("originJobId")),
                "key-bound HDD requires originJobId")
        require(sha(hdd.get("biosSha256"), "identity.hdd.biosSha256") ==
                identity["bios"]["sha256"],
                "HDD BIOS key domain differs from candidate BIOS")
        require(sha(hdd.get("eepromSha256"), "identity.hdd.eepromSha256") ==
                identity["eeprom"]["sha256"],
                "HDD EEPROM key domain differs from candidate EEPROM")

    measurement = record.get("measurement", {})
    require(isinstance(measurement, dict), "measurement must be an object")
    if record["purpose"] == "release-acceptance":
        require(measurement.get("build") == "release",
                "release acceptance requires a release build")
        require(measurement.get("instrumented") is False,
                "release acceptance cannot be instrumented")
        require(type(measurement.get("seconds")) is int and
                measurement["seconds"] > 0,
                "release acceptance requires a positive measurement duration")
        require(type(measurement.get("repeats")) is int and
                measurement["repeats"] >= 2,
                "release acceptance requires at least two repeats")
    if record["purpose"] == "diagnostic-profile":
        require(measurement.get("instrumented") is True,
                "diagnostic profile must disclose instrumentation")

    if record["purpose"] == "release-acceptance":
        scene = record.get("route", {}).get("scene", {})
        require(DHASH.fullmatch(str(scene.get("expectedImageDHash", ""))) is not None,
                "route scene needs a 16-digit image dHash")
        distance = scene.get("maximumImageHammingDistance")
        require(type(distance) is int and 0 <= distance <= 16,
                "scene maximum Hamming distance must be 0..16")


def find_input_hash(job: dict[str, Any], path: str) -> str | None:
    for item in job.get("inputs", []):
        if item.get("path") == path:
            return item.get("expectedSha256")
    return None


def validate_request(record: dict[str, Any], request: dict[str, Any]) -> None:
    job = request.get("job")
    require(isinstance(job, dict), "request.job must be an object")
    require(request.get("id") == job.get("id"),
            "request ID and job ID must match")
    require(find_input_hash(job, "flash.bin") ==
            record["identity"]["bios"]["sha256"],
            "request BIOS hash differs from record")
    require(find_input_hash(job, "state/eeprom.bin") ==
            record["identity"]["eeprom"]["sha256"],
            "request EEPROM hash differs from record")
    require(job.get("expectedExecutableSha256") ==
            record["identity"]["xemu"]["sha256"],
            "request xemu hash differs from record")
    require(find_input_hash(job, job.get("executable", "")) ==
            record["identity"]["xemu"]["sha256"],
            "request executable input hash differs from record")

    read_only = {
        item.get("field"): item
        for item in job.get("runtimeState", {}).get("isolation", {}).get(
            "readOnlyAssets", [])
    }
    for field, identity_name in (("bootrom_path", "bootrom"),
                                 ("dvd_path", "dvd")):
        item = read_only.get(field)
        expected = record["identity"][identity_name]
        require(isinstance(item, dict),
                f"request is missing the {field} catalog pin")
        require(item.get("assetId") == expected.get("assetId"),
                f"request {field} asset differs from record")
        require(item.get("expectedSha256") == expected["sha256"],
                f"request {field} hash differs from record")

    disks = job.get("runtimeState", {}).get("diskAssets", [])
    require(len(disks) == 1, "request must declare exactly one writable HDD asset")
    require(disks[0].get("assetId") == record["identity"]["hdd"].get("assetId"),
            "request HDD asset differs from record")
    require(disks[0].get("expectedSha256") ==
            record["identity"]["hdd"]["sha256"],
            "request HDD hash differs from record")

    if record["purpose"] == "release-acceptance":
        args = job.get("arguments", [])
        require("-plugin" not in args, "release acceptance request loads a plugin")
        operations = job.get("operations", {})
        require(operations.get("mode") == "benchmark",
                "release acceptance requires benchmark policy")
        for field in ("allowDiagnostics", "allowManualInput", "allowPauseResume",
                      "allowPreview", "allowBulkTransfers"):
            require(operations.get(field) is False,
                    f"release acceptance must set {field}=false")

        plan = job.get("plan", [])
        starts = [index for index, step in enumerate(plan)
                  if step.get("type") == "segment_start"]
        require(starts, "release acceptance has no segment_start")
        for index in starts:
            require(index > 0 and plan[index - 1].get("type") == "wait_for_scene",
                    "every segment_start must immediately follow wait_for_scene")
            condition = plan[index - 1].get("condition", {})
            require(condition.get("path") == record["route"]["scene"]["path"],
                    "wait_for_scene path differs from record")
            require(condition.get("expectedImageDHash") ==
                    record["route"]["scene"]["expectedImageDHash"],
                    "wait_for_scene dHash differs from record")
            require(condition.get("maximumImageHammingDistance") ==
                    record["route"]["scene"]["maximumImageHammingDistance"],
                    "wait_for_scene threshold differs from record")
        require(plan[-1].get("type") == "quit", "quit must be the final plan step")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("record", type=Path)
    parser.add_argument("--request", type=Path)
    args = parser.parse_args()
    record = load(args.record)
    validate_record(record)
    if args.request:
        validate_request(record, load(args.request))
    print(json.dumps({"ok": True, "record": str(args.record),
                      "request": str(args.request) if args.request else None}))


if __name__ == "__main__":
    main()
