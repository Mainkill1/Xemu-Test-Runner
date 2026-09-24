# Xemu Test Runner

A foreground Windows/Linux .NET test appliance. One runner owns process execution, the queue, controls, telemetry and evidence. Agents operate the already-running tester over HTTP.

## Inspect the tests first

Open `/tests` on the tester for the configuration catalog, full JSON viewer/download, named config upload and explicit Start buttons. The test-focused Python client provides the same workflow:

```sh
python scripts/runner_tests.py list
python scripts/runner_tests.py show smoke --out smoke.json
python scripts/runner_tests.py config-upload smoke-custom ./edited.json --assets seed-smoke
```

Keep `runner_tests.py`, `runner_transport.py` and `runner_test_results.py` together on the agent/build machine. Python 3.10+ standard library only. Set `XEMU_RUNNER_URL` once, or pass global `--url http://tester:9368`. A bare test name must resolve to one revision; otherwise specify `NAME@FULL_REVISION`.

Saving a configuration does not modify its source package and **never starts a test**. Asset source jobs supply the retained workloads/config/firmware/seeds. Each saved definition has an immutable content revision.

## Upload once; start only when requested

```sh
python scripts/runner_tests.py upload ./candidate --exe xemu.exe --id build-149 --tests smoke smoke-custom
python scripts/runner_tests.py start build-149-t001 build-149-t002
```

The upload command stores the application and selected configurations without execution. To explicitly authorize execution in that same command, add `--start`:

```sh
python scripts/runner_tests.py upload ./candidate --exe xemu.exe --id build-150 --tests smoke --start
python scripts/runner_tests.py status build-150-t001
```

An active test is not interrupted. Start requests are persisted and queued; preparation waits behind existing work before publishing to the established execution queue. Applications upload once and are reused across selected tests. Executable filenames can differ from the config's expected path; content hashes identify builds. Dependencies must still provide the declared build-slot paths.

To select more tests against an already uploaded application, use `select APPLICATION_ID --id NEW_PREFIX --tests ...`; add `--start` only when execution is intended. A lost response is handled by inspecting/reusing the same IDs, not creating duplicate attempts. The multi-test helper stages every selection before starting them, but individual start requests are not an all-or-nothing transaction.

Actual uploads still obey the active benchmark's transfer policy. Previously staged work can be queued while a benchmark runs; new bulk uploads may need to wait for the policy to permit them. No SSH fallback is used.

## Results by executable hash

The upload receipt includes its executable SHA-256. Finished API-owned attempts are indexed locally from canonical evidence at idle boundaries. Select a known measured reference explicitly:

```sh
python scripts/runner_tests.py baseline KNOWN_EXECUTABLE_SHA256
python scripts/runner_tests.py result CANDIDATE_EXECUTABLE_SHA256
python scripts/runner_tests.py compare --a REFERENCE_SHA256 --b CANDIDATE_SHA256
python scripts/runner_tests.py csv RUN_ID ./raw-metrics.csv
```

Normal result output is a small readable report calculated by the tester, with the pinned baseline applied by default. Global `--json` returns structured output. Raw sampler CSV is a separate download; derived comparison CSV is available with `compare ... --format csv --out comparison.csv`.

`GET /api/v1/compare?A=SHA&B=SHA` performs the comparison server-side. Omitting A uses the explicit baseline snapshot. Baselines do not drift when later runs of the same executable arrive. Different procedures/environments, failed repetitions, missing metrics and zero reference values do not become invented speedup claims. The displayed change is descriptive, not a statistical-significance claim.

Reports include A/B medians, means, ranges and attempt counts, with **Improvement % as the final column** in each section and in comparison CSV. Positive means better for the declared metric direction; a runtime dropping from 100 to 80 is reported as +20% time reduction. JSON additionally provides sample standard deviations. Incomparable/neutral results have no invented improvement percentage.

## Guest results inside the HDD

A saved test can enable `Workload.GuestHddResults` to collect `xemu_perf_tests/results.txt` automatically from its private FATX HDD after confirmed process exit, before runtime cleanup. The runner reads only the configured path and its allocation chain; no mount, whole-disk conversion, SSH or agent-side HDD download is needed.

Raw and self-contained QCOW2 v2/v3 images are supported. The test definition pins the partition geometry, clean runtime seed and expected guest result file. The adapter preserves original guest bytes, validates record IDs/work/checksums, and calculates per-leaf mean/median/min/max/p95. These metrics enter the same baseline/A/B API in separate XISO sections. Unsupported image features, stale seeds and partial/wrong guest output fail explicitly.

Configure this once in the test library using [Guest HDD results](docs/GUEST-HDD-RESULTS.md). Uploading a candidate still does not start anything; the extraction is post-processing of an already-authorized execution. It is not a new guest completion detector.

A completed or archived process is not necessarily correct. Execution, correctness, evidence and comparison remain separate. An unconfigured correctness contract is not a pass. The lower-level client retains `result JOB_ID --require correctness` and `--require eligible` for explicit exit-code gates.

## Detailed protocols and operations

| Topic | Reference |
| --- | --- |
| Named configs, application uploads and explicit queue requests | [Requested tests](docs/REQUESTED-TESTS.md) |
| Saved hash results, comparison keys and baseline lifetime | [Hash results](docs/HASH-RESULTS.md) |
| Automatic guest HDD extraction and A/B statistics | [Guest results](docs/GUEST-HDD-RESULTS.md) |
| Gameplay image regions, thresholds, and Deck capture limits | [Trustworthy experiments](docs/TRUSTWORTHY-EXPERIMENTS.md) and [Steam Deck](docs/STEAM_DECK.md) |
| Draft/upload/validate/submit API | [Agent API](docs/AGENT-API.md) |
| Lightweight job/assessment observations | [Observations](docs/AGENT-OBSERVATIONS.md) |
| Original pinned package definitions/reuse | [Test library](docs/AGENT-TEST-LIBRARY.md) |
| Paged evidence and incremental logs | [Evidence](docs/AGENT-EVIDENCE.md) |
| Lower-level HTTP client | [Agent client](docs/AGENT-CLIENT.md) |

The old `runner_api.py run`, `submit`, `retry` and `submit-draft` commands remain **explicit execution commands**. Use `runner_tests.py upload` for upload-only operation. Keep retained definition source packages for reuse; the library is not an independent binary-retention service. Keep run evidence for raw CSV links even when normalized baseline records are retained separately.

## Operator bootstrap on the tester

```sh
dotnet build src/XemuTestRunner/XemuTestRunner.csproj -c Release
dotnet run --project src/XemuTestRunner -- init
dotnet run --project src/XemuTestRunner -- doctor
dotnet run --project src/XemuTestRunner -- run --non-interactive
```

Leave the foreground runner available. Installation/startup, stopped-machine recovery and upgrades remain operator tasks. Publish with `scripts/publish.ps1 -Rid win-x64` or `bash scripts/publish.sh linux-x64`. The listener defaults to port 9368, with no built-in authentication/TLS; use the trusted test network. The [operator guide](OPERATOR-GUIDE.md) retains local configuration, control and diagnostic reference.

## Verification

```sh
dotnet run --project tests/RunnerChecks -c Release
dotnet run --project tests/AgentChecks -c Release
python -m unittest discover -s tests -p 'test_runner_api*.py' -v
dotnet run --project tests/AgentChecks -c Release -- --client
```

CI fixtures cover protocol and calculation behavior on Windows/Linux. They do not prove native xemu/game/GPU correctness, large-LAN throughput or absence of intermittent filesystem failures. Review exact PR verification and unresolved findings before deployment.
