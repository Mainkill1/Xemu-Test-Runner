# Xemu Test Runner

Run repeatable xemu tests on a Windows or Linux tester over HTTP. The tester owns the queue, xemu process, measurements and evidence. Your agent uploads a build, chooses saved tests and reads the results.

**Upload does not start a test. Start explicitly. Wait until it finishes. Download raw evidence only when needed.**

## Connect from the agent/build machine

The tester must already be running; [operator setup](#operator-setup-on-the-tester) is separate from running tests remotely.

Use Python 3.10 or later. No Python packages need installing. Keep these files from the **same checkout** together in `scripts/`:

```text
runner_tests.py          Commands for normal test operation
runner_transport.py      HTTP and resumable file transfers
runner_test_results.py   Result, comparison and diagnostic commands
runner_wait.py           Quiet completion waiting and read reconnects
```

Set the tester address once. In PowerShell:

```powershell
$env:XEMU_RUNNER_URL = "http://tester:9368"
```

Or in a POSIX shell:

```sh
export XEMU_RUNNER_URL="http://tester:9368"
```

Run the examples below from the repository root. Global options such as `--url` and `--json` go **before** the command. `python scripts/runner_tests.py --help` lists commands; append `--help` to any command for its arguments.

## Run a saved test

### 1. Inspect what will run

Open `/tests` on the tester for the browser catalog and configuration viewer, or use:

```sh
python scripts/runner_tests.py list
python scripts/runner_tests.py show smoke
```

`smoke` is an example: choose a name returned by your tester. When a name has multiple saved revisions, use `NAME@FULL_REVISION` to select one explicitly.

### 2. Upload and select, without starting

```sh
python scripts/runner_tests.py upload ./candidate --exe xemu.exe --id build-149 --tests smoke
```

The directory contains the application and required build dependencies, not another full test plan or workload tree. The receipt includes the executable SHA-256 and selected request IDs, such as `build-149-t001`. One uploaded application can serve several tests.

### 3. Start, then wait for completion

```sh
python scripts/runner_tests.py start build-149-t001
python scripts/runner_tests.py wait build-149-t001
```

Start queues the request behind existing work. Wait follows the **same request with no overall time limit**, quietly reconnecting after transient connection failures. It prints the final assessment or a problem requiring attention. No timer or duration estimate is needed.

For small progress replies instead of silence:

```sh
python scripts/runner_tests.py wait build-149-t001 --updates
```

`status` returns immediately. Waiting on an unstarted selection reports `start_required`; it never starts it. Interrupting the client does not cancel the tester. See [completion waiting](docs/COMPLETION-WAIT.md).

To upload and explicitly authorize execution in one command, use `--start`:

```sh
python scripts/runner_tests.py upload ./candidate --exe xemu.exe --id build-150 --tests smoke --start
```

Reuse identical IDs and inputs after a lost response. Use new IDs for intentional new attempts. Multi-test starts are separate durable requests, not an atomic batch. New bulk uploads can be refused during a benchmark; already-staged work can still be queued.

## Use the right identifier

| Identifier | Where it comes from | Used for |
| --- | --- | --- |
| Test name + revision | `list` | Choosing an immutable configuration. |
| Application ID | Your upload `--id`, e.g. `build-149` | Reusing the uploaded build with `select`. |
| Test request ID | Upload/select receipt, e.g. `build-149-t001` | `start`, `status`, `wait`. |
| Run ID | A started request's status/final reply | `diagnostics`, `state`, `csv`. |
| Executable SHA-256 | Upload receipt | `result`, `baseline`, `compare`; filenames do not identify builds. |

A **test revision hash** pins configuration. An **executable hash** identifies application bytes. They are not interchangeable.

## Read results and compare builds

Replace the uppercase placeholders with complete executable hashes:

```sh
python scripts/runner_tests.py result CANDIDATE_SHA256
python scripts/runner_tests.py baseline KNOWN_GOOD_SHA256
python scripts/runner_tests.py compare --b CANDIDATE_SHA256
python scripts/runner_tests.py compare --a REFERENCE_SHA256 --b CANDIDATE_SHA256
```

The tester calculates the readable reports. Setting a baseline is an explicit write that freezes the currently indexed reference results; later runs do not move the pin. Omitting `--a` uses that baseline. No baseline is selected automatically.

**Finished does not mean passed.** Check execution, correctness, evidence and comparison separately. Failed or incompatible attempts do not become speedup claims. Comparison tables end with direction-aware **Improvement %**; details are in [hash results](docs/HASH-RESULTS.md). Hash reports cover indexed archived attempts, so resolve indexing errors before claiming complete coverage.

Raw data stays optional:

```sh
python scripts/runner_tests.py diagnostics RUN_ID
python scripts/runner_tests.py state RUN_ID
python scripts/runner_tests.py diagnostics RUN_ID --out diagnostics.zip
python scripts/runner_tests.py csv RUN_ID ./metrics.csv
```

`diagnostics` reads a small crash/bundle report; only `--out` downloads its verified ZIP. A confirmed crash stays `crashed`, and other requested tests can proceed after ownership is released and the attempt is archived. Successful runs do not create failure-labeled screenshots. Plan screenshots are diagnostic by default: they do not require two hosts to reach an identical guest frame and their image-content checks do not gate correctness/evidence unless the screenshot step explicitly sets `Purpose: "correctness"`. Each diagnostic screenshot retains a `.context.json` sidecar with host timing, active segment, nearest available guest frame/time and last controller-input context. See [screenshot diagnostics](docs/SCREENSHOT-DIAGNOSTICS.md).

## Save configurations and reuse disks

```sh
python scripts/runner_tests.py show smoke --out smoke.json
python scripts/runner_tests.py config-upload smoke-custom ./edited.json --assets seed-smoke
```

Edit the downloaded configuration and save it under a new name. `seed-smoke` is an existing retained API package supplying the required assets. Saving never runs a test. [Requested tests](docs/REQUESTED-TESTS.md) explains configuration authoring and application reuse.

Large HDDs belong in the [disk catalog](docs/DISK-ASSETS.md), not every test package. Managed runtime HDDs default to deletion **after confirmed exit and evidence collection**. Snapshot carriers are currently copied intact; small snapshot overlays are not implemented. [Guest HDD extraction](docs/GUEST-HDD-RESULTS.md) collects configured FATX results without downloading or mounting the disk.

Cache history is also test input. Use an explicit [run-state policy](docs/RUN-STATE.md); cold application caches do not mean cold driver or OS caches. Retain source assets needed by saved tests and run evidence needed by raw-download links.

## Direct HTTP and deeper reference

Start with `GET /api/v1/health` for liveness and `GET /api/v1/agent?view=summary` for the deployed capabilities. Completion waits use `GET /api/v1/test-runs/{id}/wait` with no query parameters. Hash comparison uses `GET /api/v1/compare?A={hash}&B={hash}`.

| Need | Reference |
| --- | --- |
| Understand or change the implementation | [Code guide](docs/CODE-GUIDE.md) |
| Drafts, uploads, validation and submission | [Agent API](docs/AGENT-API.md) |
| Small status replies and selected evidence | [Observations](docs/AGENT-OBSERVATIONS.md), [evidence](docs/AGENT-EVIDENCE.md) |
| Crash collection and ZIP retention | [Crash reports](docs/CRASH-REPORTS.md) |
| Correctness contracts and Linux capture limits | [Experiment contracts](docs/TRUSTWORTHY-EXPERIMENTS.md), [Steam Deck](docs/STEAM_DECK.md) |
| Advanced client and initial package authoring | [Lower-level client](docs/AGENT-CLIENT.md), [test library](docs/AGENT-TEST-LIBRARY.md) |

The lower-level `runner_api.py run`, `submit`, `retry` and `submit-draft` commands **authorize execution**. They are not substitutes for upload-only `runner_tests.py upload`.

## Operator setup on the tester

From a source checkout with the .NET SDK selected by `global.json`:

```sh
dotnet build src/XemuTestRunner/XemuTestRunner.csproj -c Release
dotnet run --project src/XemuTestRunner -- init
dotnet run --project src/XemuTestRunner -- doctor
dotnet run --project src/XemuTestRunner -- run --non-interactive
```

Leave the runner running. Installation, upgrades and stopped-machine recovery are operator tasks, not per-test agent steps. Publish with `scripts/publish.ps1 -Rid win-x64` or `bash scripts/publish.sh linux-x64`. The listener has no built-in authentication/TLS; restrict it to the trusted test network. See the [operator guide](OPERATOR-GUIDE.md).

Development checks and where to add regression tests are in the [code guide](docs/CODE-GUIDE.md). CI fixtures verify software contracts; they are not real-game, GPU or large-transfer qualification.
