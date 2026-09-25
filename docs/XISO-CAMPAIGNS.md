# XISO campaigns: choose work, not infrastructure

Use `scripts/runner_xiso.py` on the agent/build machine with `runner_transport.py` beside it. Set `XEMU_RUNNER_URL` to the tester's LAN HTTP origin. Application uploads remain the existing `runner_tests.py upload` workflow; upload once and refer to its application ID for every campaign.

```sh
python scripts/runner_xiso.py categories shader-pilot
python scripts/runner_xiso.py tests shader-pilot --category shaders
python scripts/runner_xiso.py select build-184 --id pr184-shaders --category shaders
python scripts/runner_xiso.py start pr184-shaders
python scripts/runner_xiso.py wait pr184-shaders
```

The suite may be omitted when exactly one is registered. With multiple suites, add `--suite NAME`; the runner never silently switches a pinned campaign to a newer ISO. Selection is upload-only. Add `--start` to select and explicitly authorize in one command.

## Seven subsystem sections, not seven leaves

| Category | Scope |
|---|---|
| `cpu` | Floating-point and translation-block workloads |
| `commands` | PFIFO, command boundaries and report queries |
| `shaders` | Shader lifecycle, pipeline switching and uniform updates |
| `textures` | Texture formats, cubemaps and S3TC control families |
| `geometry` | Primitive types, small draws, vertex submission/allocation |
| `surfaces` | Render surfaces, readback and memory-pressure checkpoints |
| `scenarios` | Composite title-shaped workloads |

Future unmapped suites remain visible under `other`. Category counts come from the actual registered XISO. The reviewed shader pilot has 160 leaves and five structural groups, including six new shader-lifecycle leaves. These are individual cases, not six total subsystem categories.

Categories and explicit `--test` IDs form a union. Repeat either option. Unknown IDs/categories fail before campaign publication. Selecting a memory-pressure checkpoint expands its inseparable five-checkpoint route; `plan` discloses each added dependency. Groups are outcomes, not independently selectable/timed tests.

```sh
python scripts/runner_xiso.py select build-184 --id one-shader --test shader_lifecycle.pipeline_train
python scripts/runner_xiso.py select build-184 --id cpu-surface --category cpu --test surface.cpu_read_clean_surface
python scripts/runner_xiso.py plan cpu-surface --pretty
```

Global options such as `--pretty` precede the command: `runner_xiso.py --pretty plan cpu-surface`.

## Small requests and resolved defaults

This is a complete routine API request:

```json
{"id":"pr184-shaders","application":"build-184","categories":["shaders"]}
```

Omitted settings inherit the suite's pinned defaults. Registration normally derives warmups, work multiplier and GPU completion mode from its pinned reference. Only explicit overrides are transmitted, for example `--multiplier 4` adds just `settings.measurement_iterations_multiplier`. Unknown settings are errors, not silently ignored text.

The runner freezes effective settings, ISO/catalog identities, selected IDs, chunk membership and ordered attempts into the campaign before start. `GET /api/v1/xiso-campaigns/ID?view=plan` exposes the full resolved plan. A different request cannot replace an existing ID.

Changing work settings without a matching oracle contract does not make old reference measurements comparable; such results remain ineligible rather than being relabeled. A successful create/start/read response is not a guest-correctness verdict.

## Chunking and A/B

With no selectors, `smoke` chooses representative tests. Explicit selectors default to `sections`. `full` selects all leaves in route-aware chunks. `monolithic` executes a compatible selection in one process, but refuses selections that require fresh launches.

The first planner keeps execution routes together, splits on subsystem boundaries, and allows at most twelve routes per ordinary chunk. This is a deterministic work partition, not a duration guarantee. The shader pilot's lifecycle cases get independent launches even though its older generated catalog labels them same-process. Memory-pressure checkpoints remain one indivisible lifetime experiment.

```sh
python scripts/runner_xiso.py select candidate --id compare-shaders --category shaders --reference parent
```

Supplying a reference freezes `A1, B1, B2, A2` for each chunk. Both sides use identical selection/settings. The scheduler executes one child at a time through the existing runner, not a second executor. A crash or preparation failure stays in the campaign and does not silently erase coverage; other authorized children continue after ownership is released. No automatic rerun is added.

`cancel` stops remaining work and cancels an unprepared child. It does not pretend an owned process has stopped or kill a running target. An already-claimed child finalizes through its existing ownership/termination policy.

## Latest reviewed shader target

`targets` returns the known shader-inclusive candidate pin:

- ID: `shader-pilot-51bc23d`
- source: `51bc23d3706dea771b76545594256376d8588364` (xemu-perf-tests PR 44)
- ISO SHA-256: `b944d317035779afb15b7f7a0d90b0e35dd93b2257addba78c073438979cbf14`
- catalog ID: `sha256:8307fde80201084ce39706e9490cbdf16cddfb1aa21fc19fecf34942fa2f3dd4`
- 160 leaves, five structural groups; qualification is **candidate**.

The old ENG523 release is not substituted for this pilot. Registering a known target verifies its actual ISO hash and embedded catalog. This is not an automatic internet download, a claim that the candidate is a qualified baseline, or proof of observed ubershader promotion. Shader trace/capacity/promotion qualification remains part of the workload's own evidence requirements.

## Register once

A saved base test supplies the machine-specific xemu configuration, private HDD/EEPROM, firmware assets, result partition geometry and pinned oracle. It must boot normally, use managed configuration, and must not use `-snapshot`, saved-VM restore or an initially paused guest.

```sh
python scripts/runner_xiso.py register xiso-base --revision FULL_TEMPLATE_REVISION --id shader-pilot --target shader-pilot-51bc23d
```

The runner reads `catalog.json` from the **actual XISO**, hashes it, verifies its catalog records, and freezes the pair. It does not trust a catalog from the latest source checkout. A single packaged ISO or an existing shared `dvd_path` asset is accepted. The original saved template remains unchanged.

Package HDD and ISO inputs are imported once into the existing disk-asset store and removed from generated child packages. Firmware/DVD remain shared read-only; writable HDD/EEPROM remain per-attempt state. Source packages retained by older definitions are not automatically pruned.

### Prepared seed requirement

Use a clean FATX seed containing `E:\xemu_perf_tests\xemu_perf_tests_config.json` with a whitespace-padded, preallocated logical file size of 1 KiB to 1 MiB. A 64 KiB slot is sufficient for the fixture/current modest selections; registration/execution checks actual capacity instead of assuming it. The seed must not contain previous `results.txt` or `resolved-plan-result.json`.

The injector edits only that allocated file, never the ISO and never the shared seed. It does not allocate FAT clusters, change directories, format a user HDD or mount a host filesystem. Raw images and restricted plain self-contained QCOW2 are supported. QCOW2 backing chains, internal snapshots, shared/compressed/unallocated slot clusters and unsupported features are rejected. Pre- and post-injection hashes are distinct in runtime evidence; readback verifies the exact padded config bytes.

Preparing this seed is a one-time operator/build-fixture operation. This PR does not implement a general FATX editor or automatically fabricate Xbox partition layouts.

## Results, wait and cleanup

The guest reads the injected plan at boot, executes the resolved selection, closes its result/receipt and requests shutdown. The runner then confirms process exit, preserves exact `results.txt`, `resolved-plan-result.json`, guest config and injection receipt, verifies exact IDs/counts/plan identity, and compares only the selected leaves against the pinned reference. Parent groups are validated as structure rather than copied timing values.

Schema-1 guest receipts remain supported; schema-2 receipts additionally verify catalog identity. The XISO is not rebuilt to add a new receipt schema. Missing receipts, unknown/extra/duplicate IDs, missing oracle coverage or failed checks cannot create qualified measurements. All raw evidence is retained before the disposable HDD is deleted.

```sh
python scripts/runner_xiso.py status pr184-shaders
python scripts/runner_xiso.py attempts pr184-shaders
python scripts/runner_xiso.py wait pr184-shaders --updates
```

Wait has no public duration, interval or follow option. The server emits bounded transport heartbeats; the client follows indefinitely until terminal state or attention. Disconnects retry only the same read, never creation/start. Ordinary wait is silent until its final response.

Campaign creation and suite registration are preparation operations and obey benchmark transfer policy. Starting an already-created campaign and reading compact progress do not upload media. There is no guest TCP/UDP/FTP service and no extra live measurement sampler.

## Browser and API map

Open `/xiso` to select category checkboxes, search/pick individual tests, inspect optional overrides and create a campaign. Start is a separate button. The page shows the registered ISO/catalog identity and candidate status.

| API | Purpose |
|---|---|
| `GET /api/v1/xiso-targets` | Reviewed candidate artifact pins |
| `POST /api/v1/xiso-suites` | Register `{id,testId,revision}` plus optional target/settings |
| `GET /api/v1/xiso-suites/{id}/categories` | Category counts |
| `GET /api/v1/xiso-suites/{id}/tests` | Paged/filterable stable test IDs |
| `POST /api/v1/xiso-campaigns` | Resolve an unstarted selection |
| `POST /api/v1/xiso-campaigns/{id}/start` | Explicit, repeat-safe authorization |
| `GET /api/v1/xiso-campaigns/{id}/wait` | Completion observation without a timer argument |
| `GET /api/v1/xiso-campaigns/{id}/attempts` | Paged child outcomes and run IDs |
| `DELETE /api/v1/xiso-campaigns/{id}` | Cancel remaining unclaimed work |

Native xemu/GPU/shader-promotion qualification and the real prepared-seed/XISO pairing remain deployment checks. Synthetic FATX/QCOW2 and HTTP tests verify runner contracts, not game performance.
