# Xemu Test Runner

A foreground Windows/Linux C#/.NET 10 test appliance with an embedded HTTP API. The runner owns the queue, launched xemu process, telemetry, controls and evidence. Agents operate it remotely through HTTP; no second service, database or remote shell is needed for normal testing.

## Agents: start here

Run `scripts/runner_api.py` on the **agent/build machine**, with Python 3.10 or later. Keep the adjacent `runner_transport.py` and `runner_workflows.py` files with it. There are no Python package dependencies.

Set `XEMU_RUNNER_URL` once, or pass `--url http://tester:9368` before the command:

```sh
python scripts/runner_api.py discover
python scripts/runner_api.py tests
python scripts/runner_api.py run smoke --revision <revision-from-tests> --id pr149-a --build ./candidate
python scripts/runner_api.py wait pr149-a --max-wait 30
python scripts/runner_api.py result pr149-a --require correctness
```

`run` above is a **local HTTP client command**, not a command executed on the tester. It expands the pinned test on the server, reuses unchanged files, uploads changed build slots, validates and submits the new attempt. No job.json or full test plan is required in the candidate directory. Each declared build slot must be present there; fixed workloads/config/seeds remain on the tester.

Normal stdout is one compact JSON document. Progress is off by default. `--progress` opts into stderr messages; `--pretty` only changes formatting. `status --detail` and `discover --detail` explicitly request larger reference documents.

A finished attempt is not necessarily correct. Read execution, correctness, evidence and comparison separately. `--require correctness` accepts only a completed execution with passed correctness. `--require eligible` requires comparison eligibility. Exit 0 without a gate means the requested client operation succeeded, not that the guest passed; gate failure is 2 and unavailable/not-evaluated evidence is 3.

## Bake a test once

Initially upload a complete package using the API client, then register it as a reusable test:

```sh
python scripts/runner_api.py submit ./complete-test-package --id seed-smoke --wait
python scripts/runner_api.py bake smoke --from-job seed-smoke --description "Smoke workload"
python scripts/runner_api.py tests
```

Baking also accepts a fully uploaded draft or cancelled package. The returned SHA-256 revision pins the full plan, contracts and declared assets. A new revision never rewrites an older one. The default replaceable build slot is the executable; pass repeated `--build-file` arguments while baking to include required build DLLs/assets.

After that, each attempt references only test ID/revision plus changed-build declarations. A 1,000-step plan remains on the tester. Payload reuse copies and verifies files locally; it does not share writable runtime disks between attempts. Keep the retained source package: this initial library uses it as the payload source, not an independent retention cache.

For a fresh rerun of an existing package:

```sh
python scripts/runner_api.py retry pr149-a --id pr149-b --wait
```

Use a new ID for an intentional new attempt. After a lost response, rerun the identical request with the **same ID**; never manufacture a duplicate because a wait expired.

## Inspect only what is needed

```sh
python scripts/runner_api.py result pr149-a --section failures
python scripts/runner_api.py logs pr149-a --stream stderr --max-bytes 4096
python scripts/runner_api.py collect pr149-a ./evidence --only assessment.json
```

Pass the returned log cursor to the next `logs --cursor` call for new bytes only. Collection defaults to assessment.json; `collect ... --all` is an explicit bulk operation. It follows every artifact page and refuses incomplete inventories. Receipts report counts/bytes/output directory, not hundreds of filenames. Artifact byte counts are checked; a result-content digest manifest is not currently provided.

Draft plans can be edited with `edit ID --json-file plan.json`, `--json '<object>'` or `--stdin`. Submit an existing draft with `submit-draft ID`. Withdraw only unclaimed queued work; clone/retry instead of editing live attempts or completed evidence.

## Direct API reference

Start with `GET /api/v1/agent?view=summary` for capability/version checks and focused help links. Missing deployed capabilities are an upgrade/configuration issue, not permission to bypass the runner through SSH.

| Task | Contract |
| --- | --- |
| Draft/upload/validate/submit lifecycle | [Agent API](docs/AGENT-API.md) |
| Small status, bounded waits and assessments | [Observations](docs/AGENT-OBSERVATIONS.md) |
| Immutable pre-baked tests and payload reuse | [Test library](docs/AGENT-TEST-LIBRARY.md) |
| Paged artifacts and incremental logs | [Evidence](docs/AGENT-EVIDENCE.md) |
| Client commands and recovery | [Client](docs/AGENT-CLIENT.md) |

The existing package-workflow OpenAPI is at `/api/v1/openapi.json`; the additive summary/library/evidence routes have focused help and the documents above. Full-schema coverage is not implied.

## Operator bootstrap — on the tester, once

```sh
dotnet build src/XemuTestRunner/XemuTestRunner.csproj -c Release
dotnet run --project src/XemuTestRunner -- init
dotnet run --project src/XemuTestRunner -- doctor
dotnet run --project src/XemuTestRunner -- run --non-interactive
```

Publish with `scripts/publish.ps1 -Rid win-x64` or `bash scripts/publish.sh linux-x64`. Leave the foreground runner running persistently. `--once` and `--one-shot` are finite local/operator modes, not the remote-agent workflow. A stopped process cannot restart itself through its own API; installation, startup, machine recovery and upgrades are separate operator tasks.

The embedded listener defaults to port 9368 on the LAN. It has no built-in authentication or TLS: restrict it to the trusted test network. Benchmark policy may reject transfers, input, captures and diagnostics. Respect that response; do not retry the operation through a shell. Existing activity accounting detects overlap but does not promise scheduler-level zero interference.

The pre-agent-refresh [operator/reference guide](OPERATOR-GUIDE.md) retains detailed configuration, job examples, controls, diagnostics and local CLI reference. Its local CLI/package-staging examples are **operator/development-only**, not instructions for agents to copy files or launch commands on remote testers. [Architecture](docs/ARCHITECTURE.md), [diagnostics](docs/DIAGNOSTICS.md), [Steam Deck](docs/STEAM_DECK.md), [validity contracts](docs/TRUSTWORTHY-EXPERIMENTS.md) and [validation coverage](docs/VALIDATION.md) remain available.

## Verification

```sh
dotnet run --project tests/RunnerChecks -c Release
dotnet run --project tests/AgentChecks -c Release
python -m unittest discover -s tests -p test_runner_api.py -v
```

CI exercises Windows/Linux builds, regression/process fixtures, HTTP agent contracts, Python client contracts, publishing and existing browser fixtures. These checks are not a claim of real-game correctness, native GPU qualification, 10 GB+ network transfer validation or live-rig deployment.
