# Operating remote test machines

These instructions govern remote tester operation, not ordinary repository development.

Use the running tester's HTTP API. Do not log into the tester by SSH/RDP to copy builds, edit queued job.json files, start test commands, or retrieve result files. Do not bypass an API benchmark-policy refusal with shell access.

Start with `GET /api/v1/agent`. It returns the workflow, limits, OpenAPI URL, controller/result links, and recovery guidance. `docs/AGENT-API.md` explains the full contract. `scripts/runner_api.py` is an optional standard-library HTTP client that runs on the agent/build machine.

Normal workflow: create a client-ID draft, upload declared files, edit the draft with If-Match, submit, poll the operation, follow the job's run link, then download its artifacts. Reuse the SAME ID after a lost response; use a NEW ID for an intentional new attempt. A 202 response is acceptance, never a correctness verdict.

Never edit a live attempt. Withdraw an unclaimed queued job to draft, or clone a completed package into a new draft. Completed evidence is not editable. Check assessment and comparison eligibility before interpreting performance numbers.

The runner must already be running persistently on the tester. Installation, initial launch, machine power recovery and remote application upgrades are separate operator/bootstrap tasks; this API cannot start a stopped process. Keep the foreground runner alive rather than using --once/--one-shot for a remote queue.
