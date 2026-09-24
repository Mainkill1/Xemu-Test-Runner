"""Task-level HTTP workflows. No remote shell or process execution lives here."""
from __future__ import annotations

from pathlib import Path
import sys
import time
import urllib.parse
from runner_transport import ClientError, RunnerApi, declaration, encode_path, inside, job_url, manifest, run_url


class Workflows:
    def __init__(self, api: RunnerApi, operation_seconds: float = 1800, progress: bool = False):
        self.api, self.operation_seconds, self.progress = api, operation_seconds, progress

    def status(self, job_id: str) -> dict:
        return self.api.json(job_url(job_id) + "?view=summary")

    def result(self, job_id: str, job: dict | None = None) -> dict:
        job = job if job is not None else self.status(job_id)
        if not job.get("runId"):
            return {**job, "available": False, "outcome": None, "code": "result_not_ready"}
        result = self.api.json(run_url(job["runId"]) + "?view=summary")
        return {"jobId": job_id, "state": job["state"], **result}

    def wait(self, job_id: str, seconds: float) -> dict:
        deadline = time.monotonic() + seconds
        job = self.status(job_id)
        while True:
            if job["state"] == "tested":
                return self.result(job_id, job)
            if job.get("blocker") or job["state"] in ("held", "blocked", "cancelled", "unavailable", "draft"):
                return job
            remaining = deadline - time.monotonic()
            if remaining < 1:
                return job
            query = urllib.parse.urlencode({"view": "summary", "since": job["cursor"], "wait": min(20, int(remaining))})
            job = self.api.json(job_url(job_id) + "?" + query)

    def operation(self, job_id: str) -> dict:
        deadline = time.monotonic() + self.operation_seconds
        while time.monotonic() < deadline:
            value = self.api.json(job_url(job_id) + "/operation")
            if not value:
                raise ClientError("operation_missing", "No operation receipt is available.", "Inspect the existing job before retrying.")
            if value["state"] == "completed":
                # Completion publication can briefly precede release of the
                # draft mutation reservation. Do not race the next upload/edit.
                if self.status(job_id)["state"] != "busy":
                    return value
            elif value["state"] not in ("queued", "running"):
                raise ClientError(value.get("errorCode") or "operation_failed", value.get("error") or "Runner operation failed.",
                                  value.get("hint") or "Inspect this draft's operation and validation. Reuse its ID.")
            time.sleep(0.5)
        raise ClientError("operation_pending", "The operation has not finished.", "Poll this job's operation; keep the same ID.")

    def submit_draft(self, job_id: str) -> dict:
        job = self.status(job_id)
        if job["state"] in ("queued", "testing", "tested", "held", "blocked"):
            return job
        if job["state"] == "busy":
            self.operation(job_id)
            job = self.status(job_id)
        if job["state"] != "draft":
            raise ClientError("job_not_submittable", "The job is not an editable draft.", "Inspect this job; create a fresh ID only for an intentional new attempt.")
        self.api.json(job_url(job_id) + "/submit", "POST", {})
        self.operation(job_id)
        return self.status(job_id)

    def upload_files(self, job_id: str, root: Path, files: list[dict], chunk_bytes: int) -> None:
        for item in files:
            if self.progress:
                print("Upload/check " + item["Path"], file=sys.stderr)
            self.api.upload(job_id, root, item, chunk_bytes)

    def submit(self, root: Path, job_id: str, reuse: str | None, chunk_bytes: int) -> dict:
        request = manifest(root, job_id)
        self.api.json("/api/v1/jobs", "POST", request)
        current = self.status(job_id)
        if current["state"] == "busy":
            self.operation(job_id)
            current = self.status(job_id)
        if current["state"] == "draft":
            if reuse:
                self.api.require("payloadReuse")
                self.api.json(job_url(job_id) + "/reuse", "POST", {"sourceJobId": reuse})
                self.operation(job_id)
            self.upload_files(job_id, root, request["Files"], chunk_bytes)
        return self.submit_draft(job_id)

    def run_test(self, test_id: str, revision: str, job_id: str, build: Path | None,
                 labels: dict, chunk_bytes: int) -> dict:
        request = {"id": job_id, "testId": test_id, "revision": revision, **labels}
        files = []
        if build is not None:
            detail = self.api.json("/api/v1/tests/" + urllib.parse.quote(test_id, safe="") + "/" + urllib.parse.quote(revision, safe=""))
            # A build directory must contain all declared build slots. Requiring
            # these avoids accidentally mixing a new executable with old DLLs.
            files = [declaration(build, name) for name in detail["buildFiles"]]
            request["files"] = files
        self.api.json("/api/v1/jobs/from-test", "POST", request)
        self.operation(job_id)
        current = self.status(job_id)
        if build is not None and current["state"] == "draft":
            self.upload_files(job_id, build, files, chunk_bytes)
        return self.submit_draft(job_id)

    def clone(self, source: str, destination: str, submit: bool) -> dict:
        self.api.json(job_url(source) + "/clone", "POST", {"newId": destination})
        self.operation(destination)
        return self.submit_draft(destination) if submit else self.status(destination)

    def collect(self, job_id: str, output: Path, selected: list[str], all_files: bool) -> dict:
        job = self.status(job_id)
        if job["state"] != "tested" or not job.get("runId"):
            raise ClientError("run_not_archived", "Collect requires an archived tested attempt.", "Use result/logs for focused inspection; do not copy a live run.")
        run_id = job["runId"]
        base = inside(output, run_id)
        prefix = run_url(run_id)
        items = []
        excluded = 0
        if all_files:
            self.api.require("artifactPages")
            cursor = None
            seen = set()
            while True:
                path = prefix + "/artifacts?limit=100"
                if cursor:
                    path += "&cursor=" + urllib.parse.quote(cursor, safe="")
                page = self.api.json(path)
                if not page.get("complete", False):
                    raise ClientError("evidence_incomplete", "Artifact inventory is incomplete: " + ", ".join(page.get("issues", [])),
                                      "Inspect the limits/errors; no complete-collection claim was made.")
                excluded = max(excluded, page.get("excluded", 0))
                items.extend(page["items"])
                cursor = page.get("nextCursor")
                if not cursor:
                    break
                if cursor in seen or len(items) > 10000:
                    raise ClientError("evidence_incomplete", "Artifact pagination repeated or exceeded its declared bound.")
                seen.add(cursor)
        else:
            for name in sorted(set(selected or ["assessment.json"])):
                inside(base, name)
                href = prefix + "/artifacts/" + encode_path(name)
                with self.api.open(href, "HEAD") as response:
                    length = response.headers.get("Content-Length")
                    if length is None:
                        raise ClientError("artifact_length_missing", "Artifact metadata has no Content-Length.")
                    items.append({"path": name, "bytes": int(length), "href": href})
        total = 0
        names = set()
        # Collect the entire metadata inventory before touching outputs. A failed
        # final page cannot be reported as a complete collection of earlier pages.
        for item in items:
            name = item["path"]
            if name in names:
                raise ClientError("evidence_incomplete", "Artifact inventory contains a duplicate path.")
            names.add(name)
            href = prefix + "/artifacts/" + encode_path(name)
            self.api.download(href, inside(base, name), item["bytes"])
            total += item["bytes"]
        return {"ok": True, "jobId": job_id, "runId": run_id, "fileCount": len(items), "bytes": total,
                "directory": str(base), "complete": True, "scope": "allEligibleArtifacts" if all_files else "selectedArtifacts", "excluded": excluded}


def gate(result: dict, requirement: str | None) -> int:
    if requirement is None:
        return 0
    outcome = result.get("outcome") or {}
    if not result.get("available") or not outcome:
        return 3
    if outcome.get("execution") not in ("completed", "notStarted"):
        return 2
    if requirement == "correctness":
        return 0 if outcome.get("correctness") == "passed" and outcome.get("execution") == "completed" else 2 if outcome.get("correctness") == "failed" else 3
    value = outcome.get("comparison")
    return 0 if value == "eligible" and outcome.get("execution") == "completed" else 2 if value == "ineligible" else 3
