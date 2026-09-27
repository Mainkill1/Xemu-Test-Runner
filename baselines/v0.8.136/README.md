# Run the exact upstream v0.8.136 baseline

**Testing is still required.** The [main README matrix](../../README.md#upstream-v08136-baseline-compatibility) records every leaf as unverified until evidence from this release exists. These JSON files are real `POST /api/v1/xiso-campaigns` request bodies, not completed results or standalone machine configurations. They become executable after the one-time tester prerequisites below are satisfied. No native run or deployed-template readiness was verified in this audit.

| File | Purpose |
| --- | --- |
| `opengl.campaign.json` | Full discovery selection using the prepared `v08136-gl` suite. |
| `vulkan.campaign.json` | Full discovery selection using the prepared `v08136-vk` suite. |
| `compatibility.json` | Exact baseline/media identities and 160 explicit per-backend status rows; documentation data, not an API request or imported oracle. |

## 1. Prepare the tester once

Use the existing [XISO campaign workflow](../../docs/XISO-CAMPAIGNS.md). Required inputs are the pinned `shader-pilot-51bc23d` ISO, private writable EEPROM/HDD state, legally obtained firmware, a prepared clean FATX configuration slot, partition geometry, and a managed normal-disc-boot base template for each renderer. Both templates must use the same scale, guest RAM, video mode, fixed work, seed and cache policy. Run limits belong to the templates, not these request bodies.

**Renderer is set in the base template's effective xemu configuration.** Calling a suite `v08136-gl` does not make it OpenGL. Inspect the template and configure OpenGL for that suite and Vulkan for `v08136-vk`; do not add an unsupported `renderer` field to a campaign request. Record scale, VSync, RAM, GPU, driver, host and cache state with the results. Do not change either renderer implicitly between reference and candidate.

The current registration implementation also requires a hash-pinned expected-results file. Use a reviewed expected-output contract for this exact XISO/workload; it is not evidence that upstream has already passed. If that contract, prepared seed or matching template is missing, report **setup blocked**. Do not fabricate a passing golden file, use the historical 144-pass list, or weaken comparison checks to bootstrap a green result. This documentation does not implement a missing reference-capture mode.

Inspect saved tests with `runner_tests.py list` and `show`. Replace the uppercase placeholders below with their actual saved-test names and full revisions. Registration verifies the requested target against the real ISO and its embedded catalog:

```sh
python scripts/runner_xiso.py register OPENGL_BASE_TEST --revision FULL_OPENGL_REVISION --id v08136-gl --target shader-pilot-51bc23d --warmups 0 --multiplier 1 --completion per_iteration
python scripts/runner_xiso.py register VULKAN_BASE_TEST --revision FULL_VULKAN_REVISION --id v08136-vk --target shader-pilot-51bc23d --warmups 0 --multiplier 1 --completion per_iteration
```

Existing suite names are immutable. Use new names and update the request copies when a template changes. Never substitute an older ENG523 ISO or the later reduced readiness image for the pinned 160-leaf pilot.

## 2. Obtain and upload the official release

Use the Windows x86-64 release asset from [upstream v0.8.136](https://github.com/xemu-project/xemu/releases/tag/v0.8.136), not a fork build or a debug/PDB archive. The tag resolves to `fc24584ce88f0915ad7f04775bb7712c2e3f49ee`.

PowerShell, in a working directory where these output paths do not already exist:

```powershell
$archive = 'xemu-v0.8.136-win-x64.zip'
$directory = 'baseline-v0.8.136'
if ((Test-Path $archive) -or (Test-Path $directory)) { throw 'Choose fresh output paths; do not overwrite an existing baseline.' }
Invoke-WebRequest 'https://github.com/xemu-project/xemu/releases/download/v0.8.136/xemu-win-x86_64-release.zip' -OutFile $archive
$expected = 'b25a6c24a2c2c36a0843a153cd9ee59ca6833ef87bdcd855ba0824930e4ddd1d'
if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Upstream release archive hash mismatch.' }
Expand-Archive $archive -DestinationPath $directory
Get-FileHash "$directory/xemu.exe" -Algorithm SHA256
python scripts/runner_tests.py upload ./baseline-v0.8.136 --exe xemu.exe --id upstream-v08136-win-x64
```

The archive digest is the [publisher's release-asset digest](https://api.github.com/repos/xemu-project/xemu/releases/assets/441463419). It is **not the extracted executable digest**. Preserve the upload receipt's native executable SHA-256, verify it matches the extracted file, and record it with each result. `compatibility.json` deliberately leaves that value null until actual executable bytes are verified. This upload does not select or start tests, and does not alter the runner's globally selected baseline.

These requests assume a Windows x86-64 application slot named `xemu.exe`. Linux/macOS or differently shaped application templates need their own verified release artifact, matching application slot and separately identified run requests; do not relabel Windows evidence as another platform.

## 3. Submit the checked-in JSON and inspect the plan

Set `XEMU_RUNNER_URL` to the existing tester's HTTP origin. From the repository root in PowerShell:

```powershell
$endpoint = $env:XEMU_RUNNER_URL.TrimEnd('/') + '/api/v1/xiso-campaigns'
Invoke-RestMethod -Method Post -Uri $endpoint -ContentType 'application/json' -InFile 'baselines/v0.8.136/opengl.campaign.json'
Invoke-RestMethod -Method Post -Uri $endpoint -ContentType 'application/json' -InFile 'baselines/v0.8.136/vulkan.campaign.json'
python scripts/runner_xiso.py --pretty plan v08136-gl-r1
python scripts/runner_xiso.py --pretty plan v08136-vk-r1
```

Creation does **not** start execution. Check the plans' ISO/catalog pins, 160 selected leaves, dependencies, effective settings and exact application identity. Confirm each saved template's renderer separately. The initial settings are zero warmups, multiplier one and per-iteration completion: this is compatibility discovery, not a claim that every timing sample is long enough for a performance decision.

Explicitly start and observe through the existing runner:

```sh
python scripts/runner_xiso.py start v08136-gl-r1
python scripts/runner_xiso.py wait v08136-gl-r1
python scripts/runner_xiso.py attempts v08136-gl-r1 --limit 100
python scripts/runner_xiso.py start v08136-vk-r1
python scripts/runner_xiso.py wait v08136-vk-r1
python scripts/runner_xiso.py attempts v08136-vk-r1 --limit 100
```

Follow any pagination returned by the attempts API. A standalone campaign labels its sole build variant `candidate`/`B1`; here its application is the **upstream baseline**. That internal label does not change executable identity or promote an oracle.

## 4. Complete missing coverage, then approve references

`mode: full` preserves the complete selection and runner-owned chunking; it does not promise every leaf survives an earlier crash in the same chunk. Retain the first failed attempt. For tests not reached, explicitly create a new individual selection with `--test EXACT_ID` and a new campaign ID. Memory-pressure routes expand to all five checkpoints; shader-lifecycle cases already require separate processes. Inspect that the prepared guest plan enables required xemu-only tests. An omitted gated test is missing coverage, not a pass.

Keep guest PASS/FAIL, emulator crash/timeout, setup failure, missing output and timing eligibility separate. A normal exit, screenshot, successful HTTP request or matching record count alone is not correctness proof. Missing fork-only instrumentation must not be interpreted as an upstream guest failure; use the declared upstream-compatible evidence contract without manufacturing unavailable timing attribution.

Repeat completed baseline selections under new IDs, for example `v08136-gl-r2` and `v08136-vk-r2`, before approving repeatable references. Keep the same ISO, work and settings; preserve failures rather than replacing them with successful retries. Tiny performance samples require a separately declared, baseline-calibrated work setting shared with the candidate. Correctness-only leaves and structural groups never become timing benchmarks.

Update the main table and `compatibility.json` together. Each changed status needs retained run IDs, executable and ISO hashes, catalog/test revision, effective template/settings, backend/environment, completed-work checks, and byte-exact result/receipt hashes. Keep the approved performance-ID lists empty until their separate measurement gates pass. This JSON is not yet automatically ingested by the runner; changing it alone cannot approve a comparison.

For a subsequently qualified candidate pair, use the same registered suite and explicitly name the unchanged baseline application:

```sh
python scripts/runner_xiso.py select CANDIDATE_APPLICATION --id compare-v08136-gl --suite v08136-gl --mode full --reference upstream-v08136-win-x64 --warmups 0 --multiplier 1 --completion per_iteration
```

This creates the existing per-chunk ABBA schedule without starting it. Use the Vulkan suite and a different campaign ID for Vulkan. Do not change the global baseline pin merely to collect these observations.

## Verification scope

Audited runner source: `07258b38690e79a4de0d62f5f7afd5d58f4ff4dc`. Request fields were checked against `XisoCampaignRequest` and `runner_xiso.py`; registration prerequisites against `AgentJobStore.XisoSuites.cs`. Local checks validate JSON, 160 unique leaf IDs, five non-timing groups, matching README/status rows, and the request shapes. The tester health request returned connection refused. No native execution, deployed registration, release-binary hash calculation, or performance qualification is claimed.
