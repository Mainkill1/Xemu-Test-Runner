# Remote tester operation

Use the already-running tester's HTTP API. Normal testing does not require SSH/RDP, remote command execution, queue-directory edits or raw result downloads. Do not bypass benchmark-policy refusals with shell access. Local repository development/build commands are separate from operating the tester.

Preferred entry point: run `scripts/runner_api.py` on the agent/build machine. Set XEMU_RUNNER_URL once. Start with `discover`, then `tests`. Use `run TEST --revision PIN --id NEW --build DIR` for a pre-baked test; the client expands the plan on the server and uploads changed build files only. Use `submit PACKAGE --id NEW` only when authoring an initial/custom package. `retry OLD --id NEW` intentionally repeats a package without another upload.

Use `wait ID --max-wait 30` and `result ID` for normal observation. A wait expiry retains the same ID and does not cancel or repeat work. Held/blocked states require inspection, not blind polling or deleting locks. Check the four assessment outcomes; use --require correctness or --require eligible for an explicit gate. Never equate tested/exit zero/API ok with guest correctness.

Responses are compact by default. Read detail only when needed. `logs ID` is bounded; its returned cursor reads subsequent bytes. `collect` defaults to assessment.json; --only selects files and --all explicitly requests every eligible artifact page. Do not download full evidence just to determine the outcome.

For direct HTTP, begin with GET /api/v1/agent?view=summary and follow focused help/actions. If a required capability is absent, report the deployed-version/configuration issue; do not invent a shell fallback. Reuse an identical request and ID after a lost response. A changed test definition/build request or intentional new attempt needs its own identity.

Plans and results are immutable while running/completed. Edit a draft with revision control, withdraw before queue claim, or clone to a fresh draft. Pin pre-baked test revisions; do not silently choose latest. Keep the baked definition's source package for payload reuse.

The runner must already be running persistently. Bootstrap/initial launch, upgrades, stopped-machine recovery and operating-system administration remain separate operator tasks. The OPERATOR-GUIDE.md local staging/one-shot examples are not the remote agent workflow. Place a short reference to these instructions in the consuming xemu workspace so its agents see the same entry point.
