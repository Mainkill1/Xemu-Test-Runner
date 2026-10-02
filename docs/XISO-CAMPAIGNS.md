# XISO campaigns: choose work, not infrastructure

Run `scripts/runner_xiso.py` on the agent/build machine with `runner_transport.py` beside it. Set `XEMU_RUNNER_URL` to the tester LAN HTTP origin. Applications use the existing `runner_tests.py upload` workflow; upload once and reference that application ID in campaigns.

```sh
python scripts/runner_xiso.py categories shader-pilot
python scripts/runner_xiso.py tests shader-pilot --category shaders
python scripts/runner_xiso.py select build-184 --id pr184-shaders --category shaders
python scripts/runner_xiso.py start pr184-shaders
python scripts/runner_xiso.py wait pr184-shaders
```

`shader-pilot` is an example registered name. Omit `--suite` only when exactly one suite is registered. Otherwise supply `--suite NAME`. A campaign never changes to a newer ISO implicitly. Selection does not start execution; add `--start` to explicitly authorize in the same command.

## Categories and individual tests

| Category | Scope |
|---|---|
| `cpu` | Floating-point and translation-block workloads |
| `commands` | PFIFO, command boundaries and report queries |
| `shaders` | Shader lifecycle, pipeline switching and uniform updates |
| `textures` | Texture formats, cubemaps and S3TC controls |
| `geometry` | Primitive types, small draws and vertex submission/allocation |
| `surfaces` | Render surfaces, readback and memory-pressure checkpoints |
| `scenarios` | Composite title-shaped workloads |

Future unmapped suites remain visible under `other`. Counts come from the registered XISO, not a documentation constant. The reviewed shader pilot has 160 leaves and five structural groups, including six shader-lifecycle leaves. Leaves are individual cases, not subsystem categories.

Repeat `--category` and `--test`; their selections form a union. Unknown selectors fail before publication. A memory-pressure checkpoint expands to its inseparable five-checkpoint route. `plan` discloses added dependencies. Structural groups are not independently selected or timed.

```sh
python scripts/runner_xiso.py select build-184 --id one-shader --test shader_lifecycle.pipeline_train
python scripts/runner_xiso.py select build-184 --id cpu-surface --category cpu --test surface.cpu_read_clean_surface
python scripts/runner_xiso.py --pretty plan cpu-surface
```

Global options such as `--url` and `--pretty` precede the command.

## Defaults and small requests

This is a complete routine request:

```json
{"id":"pr184-shaders","application":"build-184","categories":["shaders"]}
```

Settings inherit the registered suite defaults. Registration normally derives warmups, fixed-work multiplier and GPU completion mode from its pinned reference. Only explicit overrides are transmitted: `--multiplier 4` adds `settings.measurement_iterations_multiplier` and nothing else. Unknown settings are rejected instead of ignored.

The frozen plan includes effective settings, ISO/catalog identities, selected IDs, dependencies, chunk membership and ordered attempts. Read it through `plan` or `GET /api/v1/xiso-campaigns/ID?view=plan`. Different inputs cannot replace the same campaign ID.

Changing work settings without a compatible reference does not relabel old oracles. Missing oracle coverage or incompatible work remains visible and cannot produce qualified measurements. API success, guest correctness and performance-comparison eligibility are separate results.

## Full paired default and two-host preflight

A paired campaign with a reference application and no selectors defaults to `full`: every catalog leaf is selected in route-aware chunks. An unpaired diagnostic with no selectors retains the representative `smoke` default. Explicit selectors default to `sections`; `monolithic` uses one process for a compatible selection and refuses tests whose contract requires fresh launches.

The planner preserves execution routes, separates subsystems, and allows up to twelve ordinary routes per chunk. This is a deterministic partition, not an estimated completion time. The shader pilot's lifecycle cases run independently even where the generated catalog still says same-process; memory-pressure checkpoints stay together. A full shader-inclusive campaign therefore cannot be treated as one monolithic process.

```sh
python scripts/runner_xiso.py select candidate --id compare-shaders --category shaders --reference parent
```

A reference application freezes `A1, B1, B2, A2, B3, A3, A4, B4` for each chunk: an ABBA block followed by a BAAB block, four attempts per build. The same selected tests, work settings, ISO and catalog apply to each attempt. The existing runner remains the only executor. No agent script constructs attempt order or counts missing work. Crashed/failed children remain in the campaign while subsequent authorized children continue after ownership is released. No automatic rerun is added.

Create the same paired request on Windows and Steam Deck with distinct campaign IDs, then compare their frozen plans **before starting either campaign**:

```sh
python scripts/runner_xiso.py --url http://WINDOWS:9368 select candidate --reference baseline --suite suite-id --id pr-example-windows
python scripts/runner_xiso.py --url http://DECK:9368 select candidate --reference baseline --suite suite-id --id pr-example-deck
python scripts/runner_xiso.py --url http://WINDOWS:9368 check-pair pr-example-windows --other-url http://DECK:9368 --other-id pr-example-deck
```

`check-pair` compares portable frozen media, selection, settings, dependencies, chunk contents and A/B attempt order. It does not equate Windows and Linux renderer/driver environments; compare results within each host before interpreting cross-host trends. Both hosts need the same hash-pinned ISO and compatible clean seed and reference. Existing tests must not change between PR campaigns without a new suite identity.

If any selected leaf lacks an oracle in the pinned reference, creation fails before child tests are scheduled and lists the missing coverage. First run an **unpaired full diagnostic** with the exact ISO to obtain raw output, qualify its reference, then register a new immutable suite. Do not fabricate the missing oracle or override driver-cache qualification to turn a failed campaign green.

`cancel` cancels remaining unclaimed work; an owned target is not killed or declared stopped by this command. A claimed child completes through existing process/timeout/finalization policy. Corrupt campaign metadata is reported by the campaign list without being rewritten or blocking unrelated valid campaigns. Shutdown/restart uses persisted start intent, and interrupted create/start handoffs reuse the exact child ID.

## Known shader-inclusive target

`targets` exposes the reviewed candidate pin:

| Field | Value |
|---|---|
| ID | `shader-pilot-51bc23d` |
| Source | `51bc23d3706dea771b76545594256376d8588364` (xemu-perf-tests PR 44) |
| ISO SHA-256 | `b944d317035779afb15b7f7a0d90b0e35dd93b2257addba78c073438979cbf14` |
| Catalog ID | `sha256:8307fde80201084ce39706e9490cbdf16cddfb1aa21fc19fecf34942fa2f3dd4` |
| Inventory | 160 leaves, five structural groups |
| Qualification | **candidate**, not an approved performance baseline |

The latest packaged target is `shader-runner-suite-6a87374`, built from xemu-perf-tests PR #45 plus PR #44 head `13e0cac` at source commit `6a87374fd807a9391e198928907b75462e1b246e`. The ISO SHA-256 is `e6da3efe6f1e507edd9af7e1e9d2b3498459568a06d7bdf21556d72bc761b667` and catalog ID is `sha256:2490192671df057163aa6c7bb2f144491dfa35f42ef2f9318a87f50ec92e7747`. This revised readiness catalog has 159 leaves. It remains a **candidate**. The bundled reference contains 136 leaf oracles, leaving 23 leaves unavailable for full paired comparison. A new reference must be qualified for the exact 159-leaf suite before a full paired campaign can run. The `suites` API shows `oracleLeafCount` and `missingOracleCount` for newly registered suites. Earlier 160-leaf target pins remain available for historical campaigns and must not be silently replaced.

The older ENG523 release is not substituted. Registration verifies actual ISO bytes and embedded catalog against a requested target. This is not automatic internet downloading or proof of ubershader promotion/capacity behavior; those claims require the workload's host trace and timing qualification.

## Register once

A saved base template provides the machine-specific xemu configuration, private HDD/EEPROM, firmware, partition geometry, execution limits and pinned reference. It must use managed configuration and normal disc boot, not `-snapshot`, saved-VM restore or an initially paused guest. Execution limits belong to that saved template; observers do not estimate them.

```sh
python scripts/runner_xiso.py register xiso-base --revision FULL_TEMPLATE_REVISION --id shader-pilot --target shader-pilot-51bc23d
```

The runner reads root `catalog.json` from the actual self-contained XDVDFS ISO and freezes its hash/ID with the ISO. It accepts one packaged ISO or a shared `dvd_path` asset. It never pairs an older image with a catalog from the latest checkout.

Packaged HDD/ISO inputs are imported once into the existing asset catalog and omitted from generated child payloads. Firmware/DVD remain shared read-only. Writable HDD/EEPROM remain private. The original base template and historical packages are not rewritten or automatically pruned.

### Prepared FATX seed

The clean seed must contain `E:\xemu_perf_tests\xemu_perf_tests_config.json` with a whitespace-padded, preallocated logical length of 1 KiB to 1 MiB. Capacity is checked against the actual generated JSON. It must not contain previous `results.txt` or `resolved-plan-result.json`.

The injector edits only this existing allocated file in a private clone. It never mounts disks, allocates FAT clusters, rewrites directories, changes the ISO or modifies the shared seed. Raw images and restricted plain self-contained QCOW2 are supported. Backing chains, internal snapshots, shared/compressed/unallocated configuration clusters and unsupported QCOW2 features are rejected. Readback verifies the padded JSON; evidence distinguishes original-seed and prepared-image hashes.

Seed preparation is a one-time operator/build-fixture operation. This change does not implement a general FATX editor or automatically fabricate Xbox partition layouts. Normal blank disks without this prepared file are rejected with an actionable error.

## Evidence, reports and waiting

After confirmed process exit, the runner preserves byte-exact `results.txt`, `resolved-plan-result.json`, injected config and preparation receipt. It checks exact IDs, counts and plan identity, then compares selected leaves against the pinned reference. Parent groups are checked as structure, never used as independent timing samples. Original guest bytes are retained before transient HDD deletion.

Existing schema-1 guest receipts work; schema-2 receipts additionally check the returned catalog ID. No guest/XISO rebuild is required for this compatibility. Missing receipts, extra/duplicate IDs, absent oracles or failed checks suppress qualified measurements.

```sh
python scripts/runner_xiso.py status pr184-shaders
python scripts/runner_xiso.py attempts pr184-shaders
python scripts/runner_xiso.py wait pr184-shaders --updates
```

The normal report is a compact campaign summary with total/finished/passed/failed/incomplete/remaining counts, active child, and comparison-eligible count. `attempts` pages detailed per-child canonical outcomes and run IDs; existing result/comparison APIs supply metrics. A campaign marked passed still exposes comparison eligibility separately. Raw artifacts remain secondary.

`report CAMPAIGN` emits a PR-ready Markdown category summary and a table for every selected leaf, including A/B medians, pooled improvement, separate ABBA and BAAB changes, and every guest metric whose absolute change exceeds 1%. The category summary counts leaves faster/slower by more than 1% and shows the median leaf percentage change; it is not an additive runtime. `report CAMPAIGN --format csv --out FILE` retains all per-leaf metrics and four-attempt A/B distributions. The report also states the suite's candidate/approved qualification. Missing, failed, unindexed or comparison-ineligible attempts suppress numeric claims. Opposite changes above 1% between ABBA and BAAB are labeled inconclusive.

Wait has no timer, interval or follow option. The client follows server-controlled transport heartbeats indefinitely until terminal state or attention. Quiet waiting prints only the final response. `--updates` explicitly prints short heartbeat lines. Reconnection repeats only the same read, never selection or start.

Registration and campaign creation obey benchmark preparation/transfer policy. Starting an already-created campaign and compact observations do not upload media. The dispatcher reuses the existing idle scheduler, scans history once per server lifetime, and bounds caches for plans and finalized observations. There is no new live sampler or guest network service.

## Browser/API

Open `/xiso` for category checkboxes, individual search/selection, collapsed optional overrides and visible ISO/catalog/candidate identity. Create and Start are separate buttons.

| API | Purpose |
|---|---|
| `GET /api/v1/xiso-targets` | Known candidate artifact pins |
| `POST /api/v1/xiso-suites` | Register `{id,testId,revision}` with optional target/settings |
| `GET /api/v1/xiso-suites/{id}/categories` | Subsystem counts |
| `GET /api/v1/xiso-suites/{id}/tests` | Paged/filterable exact test IDs |
| `POST /api/v1/xiso-campaigns` | Resolve an unstarted selection |
| `POST /api/v1/xiso-campaigns/{id}/start` | Explicit repeat-safe authorization |
| `GET /api/v1/xiso-campaigns/{id}/wait` | Completion observation, no timer arguments |
| `GET /api/v1/xiso-campaigns/{id}/attempts` | Paged child outcomes/run IDs |
| `GET /api/v1/xiso-campaigns/{id}/report?format=json\|markdown\|csv` | Exact-attempt per-leaf report for PR evidence |
| `DELETE /api/v1/xiso-campaigns/{id}` | Cancel remaining unclaimed work |

Native xemu/GPU/shader-promotion qualification and the real prepared-seed/XISO pairing remain deployment checks. FATX/QCOW2, browser and HTTP fixtures establish runner contracts, not real-game performance.
