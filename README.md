# Xemu Test Runner

Run repeatable xemu tests on a Windows or Linux tester over HTTP. The tester owns the queue, xemu process, measurements and evidence. Your agent uploads a build, chooses saved tests and reads the results.

**Upload does not start a test. Start explicitly. Wait until it finishes. Download raw evidence only when needed.**

## Upstream v0.8.136 baseline compatibility

> **TESTING REQUIRED.** No retained per-test results verified in this audit establish that upstream **v0.8.136** passes or fails the pinned XISO below on OpenGL or Vulkan. Every unverified case is explicitly **Needs testing**. The earlier **144 OpenGL PASS observations belong to a different fork executable and XISO**, not this release. Do not use them as this baseline's reference.

Baseline: [official xemu v0.8.136](https://github.com/xemu-project/xemu/releases/tag/v0.8.136), source `fc24584ce88f0915ad7f04775bb7712c2e3f49ee`. The supplied run requests target the official Windows x86-64 release package. Other platforms require their own executable and environment identities.

This inventory covers **all 160 individual tests in the runner-pinned `shader-pilot-51bc23d` XISO**, plus five separately listed structural groups. It does not claim to cover every later test-suite revision.

| Workload identity | Exact pin |
| --- | --- |
| XISO source | `51bc23d3706dea771b76545594256376d8588364` |
| XISO SHA-256 | `b944d317035779afb15b7f7a0d90b0e35dd93b2257addba78c073438979cbf14` |
| Catalog ID | `sha256:8307fde80201084ce39706e9490cbdf16cddfb1aa21fc19fecf34942fa2f3dd4` |
| Source inventory | [Pinned catalog with descriptions, revisions and measurement classes](https://github.com/Mainkill1/xemu-perf-tests/blob/51bc23d3706dea771b76545594256376d8588364/resources/catalog.json) |

| Verified evidence in this audit | OpenGL | Vulkan |
| --- | ---: | ---: |
| Confirmed working individual tests | 0 | 0 |
| Confirmed failing individual tests | 0 | 0 |
| Individual tests still needing execution/validation | 160 | 160 |
| Approved performance-reference tests | 0 | 0 |

Zero confirmed failures is **not** a claim that everything works. The tester HTTP health request was refused; no native test was started. See the [run instructions and setup prerequisites](baselines/v0.8.136/README.md), [OpenGL run JSON](baselines/v0.8.136/opengl.campaign.json), [Vulkan run JSON](baselines/v0.8.136/vulkan.campaign.json), and [machine-readable compatibility inventory](baselines/v0.8.136/compatibility.json).

**How to read the table:** **Works** requires completed selected work and passing required correctness evidence on this exact baseline/backend. **Fails** requires retained evidence of a failed test. **Blocked** identifies a setup/capability failure, not an emulator correctness verdict. **Needs testing** means no qualifying result has been established. A crash that prevents later tests from running does not make those later tests FAIL. Correctness compatibility and performance-reference approval are separate.

### Every individual test

The IDs are the exact selectors accepted by `runner_xiso.py --test`; the pinned catalog above supplies each test's detailed workload contract.

<!-- BEGIN V08136 TEST MATRIX -->
| Individual test ID | OpenGL | Vulkan |
| --- | --- | --- |
| `busy_pfifo.pfifo_saturation` | Needs testing | Needs testing |
| `busy_pfifo.pgraph_pattern_polling` | Needs testing | Needs testing |
| `cpu_floating_point.sse_scalar` | Needs testing | Needs testing |
| `cpu_floating_point.x87_scalar` | Needs testing | Needs testing |
| `cpu_translation_blocks.direct_loop` | Needs testing | Needs testing |
| `cpu_translation_blocks.indirect_dispatch` | Needs testing | Needs testing |
| `cpu_translation_blocks.indirect_dispatch_stress` | Needs testing | Needs testing |
| `fill_rate.solid` | Needs testing | Needs testing |
| `fill_rate.textured` | Needs testing | Needs testing |
| `game_load.cross_title_hotpath.blend_constant_reuse` | Needs testing | Needs testing |
| `game_load.cross_title_hotpath.gpu_wait_control` | Needs testing | Needs testing |
| `game_load.cross_title_hotpath.pgr2_lagspot_inline_elements` | Needs testing | Needs testing |
| `game_load.cross_title_hotpath.pgr2_small_draws` | Needs testing | Needs testing |
| `game_load.cross_title_hotpath.pipeline_state_churn` | Needs testing | Needs testing |
| `game_load.cross_title_hotpath.queued_vertex_cpu_writes` | Needs testing | Needs testing |
| `game_load.cross_title_hotpath.s3tc_streaming_fenced_draws` | Needs testing | Needs testing |
| `game_load.cross_title_hotpath.scaled_surface_pressure` | Needs testing | Needs testing |
| `game_load.cross_title_hotpath.surface_reuse` | Needs testing | Needs testing |
| `game_load.cross_title_hotpath.texture_binding_reuse` | Needs testing | Needs testing |
| `game_load.cross_title_hotpath.texture_update_reuse` | Needs testing | Needs testing |
| `game_load.doax_menu_representative.cpu_only` | Needs testing | Needs testing |
| `game_load.doax_menu_representative.cpu_pfifo_gpu` | Needs testing | Needs testing |
| `game_load.doax_menu_representative.cpu_pfifo_gpu_streaming` | Needs testing | Needs testing |
| `game_load.doax_menu_representative.full_system` | Needs testing | Needs testing |
| `game_load.doax_menu_representative.gpu_only` | Needs testing | Needs testing |
| `game_load.doax_menu_representative.pfifo_only` | Needs testing | Needs testing |
| `game_load.doax_menu_representative.streaming_only` | Needs testing | Needs testing |
| `game_load.doax_menu_stress.cpu_only` | Needs testing | Needs testing |
| `game_load.doax_menu_stress.cpu_pfifo_gpu` | Needs testing | Needs testing |
| `game_load.doax_menu_stress.cpu_pfifo_gpu_streaming` | Needs testing | Needs testing |
| `game_load.doax_menu_stress.full_system` | Needs testing | Needs testing |
| `game_load.doax_menu_stress.gpu_only` | Needs testing | Needs testing |
| `game_load.doax_menu_stress.pfifo_only` | Needs testing | Needs testing |
| `game_load.doax_menu_stress.streaming_only` | Needs testing | Needs testing |
| `game_load.long_unlocked_scene.alpha_overdraw` | Needs testing | Needs testing |
| `game_load.long_unlocked_scene.combined` | Needs testing | Needs testing |
| `game_load.long_unlocked_scene.cpu` | Needs testing | Needs testing |
| `game_load.long_unlocked_scene.full_system` | Needs testing | Needs testing |
| `game_load.long_unlocked_scene.pfifo` | Needs testing | Needs testing |
| `game_load.long_unlocked_scene.streaming_surface_reuse` | Needs testing | Needs testing |
| `game_load.pgr2_ai_backup.cpu_only` | Needs testing | Needs testing |
| `game_load.pgr2_ai_backup.cpu_pfifo_gpu` | Needs testing | Needs testing |
| `game_load.pgr2_ai_backup.cpu_pfifo_gpu_streaming` | Needs testing | Needs testing |
| `game_load.pgr2_ai_backup.full_system` | Needs testing | Needs testing |
| `game_load.pgr2_ai_backup.gpu_only` | Needs testing | Needs testing |
| `game_load.pgr2_ai_backup.pfifo_only` | Needs testing | Needs testing |
| `game_load.pgr2_ai_backup.streaming_only` | Needs testing | Needs testing |
| `game_load.repeated_display` | Needs testing | Needs testing |
| `game_load.repeated_display_boosted` | Needs testing | Needs testing |
| `game_load.s3tc_sync_factor.bc2_bordered_fallback` | Needs testing | Needs testing |
| `game_load.s3tc_sync_factor.bc2_native_eligible` | Needs testing | Needs testing |
| `game_load.s3tc_sync_factor.bc3_bordered_fallback` | Needs testing | Needs testing |
| `game_load.s3tc_sync_factor.bc3_native_eligible` | Needs testing | Needs testing |
| `game_load.s3tc_sync_factor.dxt1_dirty_once_redraw` | Needs testing | Needs testing |
| `game_load.s3tc_sync_factor.dxt1_ring_payload_generations` | Needs testing | Needs testing |
| `game_load.s3tc_sync_factor.dxt1_same_address_queued` | Needs testing | Needs testing |
| `game_load.s3tc_sync_factor.dxt1_same_address_wait` | Needs testing | Needs testing |
| `game_load.s3tc_sync_factor.rgba8_dirty_once_redraw` | Needs testing | Needs testing |
| `game_load.s3tc_sync_factor.rgba8_ring_payload_generations` | Needs testing | Needs testing |
| `game_load.s3tc_sync_factor.rgba8_same_address_queued` | Needs testing | Needs testing |
| `game_load.s3tc_sync_factor.rgba8_same_address_wait` | Needs testing | Needs testing |
| `high_vertex_count.arrays` | Needs testing | Needs testing |
| `high_vertex_count.inline_arrays` | Needs testing | Needs testing |
| `high_vertex_count.inline_buffers` | Needs testing | Needs testing |
| `high_vertex_count.inline_elements` | Needs testing | Needs testing |
| `pfifo_array_elements.array_element16` | Needs testing | Needs testing |
| `pfifo_array_elements.array_element32` | Needs testing | Needs testing |
| `pfifo_array_elements.array_element_pgr2` | Needs testing | Needs testing |
| `pfifo_packet_boundary.array_element16_overflow` | Needs testing | Needs testing |
| `pfifo_packet_boundary.array_element32_overflow` | Needs testing | Needs testing |
| `pfifo_packet_boundary.incrementing_inline_fallback` | Needs testing | Needs testing |
| `pfifo_packet_boundary.inline_array_overflow` | Needs testing | Needs testing |
| `pipeline_texture_switch.clear_texture_normal` | Needs testing | Needs testing |
| `pipeline_texture_switch.palette_dma_remap` | Needs testing | Needs testing |
| `pipeline_texture_switch.palette_only_update` | Needs testing | Needs testing |
| `pipeline_texture_switch.sampler_only_identity` | Needs testing | Needs testing |
| `pipeline_texture_switch.shader_negative_control` | Needs testing | Needs testing |
| `pipeline_texture_switch.shared_page_overlap` | Needs testing | Needs testing |
| `pipeline_texture_switch.texture_dma_remap` | Needs testing | Needs testing |
| `pipeline_texture_switch.texture_switch` | Needs testing | Needs testing |
| `primitive_type.line_loop.fixed_function` | Needs testing | Needs testing |
| `primitive_type.line_loop.vertex_shader` | Needs testing | Needs testing |
| `primitive_type.line_strip.fixed_function` | Needs testing | Needs testing |
| `primitive_type.line_strip.vertex_shader` | Needs testing | Needs testing |
| `primitive_type.lines.fixed_function` | Needs testing | Needs testing |
| `primitive_type.lines.vertex_shader` | Needs testing | Needs testing |
| `primitive_type.points.fixed_function` | Needs testing | Needs testing |
| `primitive_type.points.vertex_shader` | Needs testing | Needs testing |
| `primitive_type.polygon.fixed_function` | Needs testing | Needs testing |
| `primitive_type.polygon.vertex_shader` | Needs testing | Needs testing |
| `primitive_type.quad_strip.fixed_function` | Needs testing | Needs testing |
| `primitive_type.quad_strip.vertex_shader` | Needs testing | Needs testing |
| `primitive_type.quads.fixed_function` | Needs testing | Needs testing |
| `primitive_type.quads.vertex_shader` | Needs testing | Needs testing |
| `primitive_type.triangle_fan.fixed_function` | Needs testing | Needs testing |
| `primitive_type.triangle_fan.vertex_shader` | Needs testing | Needs testing |
| `primitive_type.triangle_strip.fixed_function` | Needs testing | Needs testing |
| `primitive_type.triangle_strip.vertex_shader` | Needs testing | Needs testing |
| `primitive_type.triangles.fixed_function` | Needs testing | Needs testing |
| `primitive_type.triangles.vertex_shader` | Needs testing | Needs testing |
| `report_query.clear_boundary` | Needs testing | Needs testing |
| `report_query.dma_descriptor_rewrite` | Needs testing | Needs testing |
| `report_query.dma_range_guard` | Needs testing | Needs testing |
| `report_query.dma_target_switch` | Needs testing | Needs testing |
| `report_query.fifo_producer_ordering` | Needs testing | Needs testing |
| `report_query.multiple_boundaries` | Needs testing | Needs testing |
| `report_query.single_boundary` | Needs testing | Needs testing |
| `report_query.zero_query` | Needs testing | Needs testing |
| `shader_lifecycle.pipeline_capacity_c` | Needs testing | Needs testing |
| `shader_lifecycle.pipeline_capacity_c_minus_one` | Needs testing | Needs testing |
| `shader_lifecycle.pipeline_capacity_c_plus_one` | Needs testing | Needs testing |
| `shader_lifecycle.pipeline_identical_replay` | Needs testing | Needs testing |
| `shader_lifecycle.pipeline_train` | Needs testing | Needs testing |
| `shader_lifecycle.pipeline_uniform_only` | Needs testing | Needs testing |
| `surface.basic` | Needs testing | Needs testing |
| `surface.cpu_read_after_gpu_write` | Needs testing | Needs testing |
| `surface.cpu_read_clean_surface` | Needs testing | Needs testing |
| `surface.framebuffer_working_set_002` | Needs testing | Needs testing |
| `surface.framebuffer_working_set_008` | Needs testing | Needs testing |
| `surface.framebuffer_working_set_032` | Needs testing | Needs testing |
| `surface.framebuffer_working_set_064` | Needs testing | Needs testing |
| `surface.full_clear_elision_guard` | Needs testing | Needs testing |
| `surface.overlapping_surface_churn_representative` | Needs testing | Needs testing |
| `surface.overlapping_surface_churn_stress` | Needs testing | Needs testing |
| `surface.partial_channel_clear_guard` | Needs testing | Needs testing |
| `surface.surface_download_path` | Needs testing | Needs testing |
| `surface.surface_list_lookup_002` | Needs testing | Needs testing |
| `surface.surface_list_lookup_008` | Needs testing | Needs testing |
| `surface.surface_list_lookup_032` | Needs testing | Needs testing |
| `surface.surface_list_lookup_128` | Needs testing | Needs testing |
| `surface.vulkan_memory_pressure.representative.alias_resize` | Needs testing | Needs testing |
| `surface.vulkan_memory_pressure.representative.growth` | Needs testing | Needs testing |
| `surface.vulkan_memory_pressure.representative.idle_retention` | Needs testing | Needs testing |
| `surface.vulkan_memory_pressure.representative.plateau` | Needs testing | Needs testing |
| `surface.vulkan_memory_pressure.representative.reuse` | Needs testing | Needs testing |
| `surface.vulkan_memory_pressure.stress.alias_resize` | Needs testing | Needs testing |
| `surface.vulkan_memory_pressure.stress.growth` | Needs testing | Needs testing |
| `surface.vulkan_memory_pressure.stress.idle_retention` | Needs testing | Needs testing |
| `surface.vulkan_memory_pressure.stress.plateau` | Needs testing | Needs testing |
| `surface.vulkan_memory_pressure.stress.reuse` | Needs testing | Needs testing |
| `texture_cubemap_fallback.unbordered_subblock_dxt1` | Needs testing | Needs testing |
| `tiny_draw.arrays.fixed_function` | Needs testing | Needs testing |
| `tiny_draw.arrays.vertex_shader` | Needs testing | Needs testing |
| `tiny_draw.inline_arrays.fixed_function` | Needs testing | Needs testing |
| `tiny_draw.inline_arrays.vertex_shader` | Needs testing | Needs testing |
| `tiny_draw.inline_buffers.fixed_function` | Needs testing | Needs testing |
| `tiny_draw.inline_buffers.vertex_shader` | Needs testing | Needs testing |
| `tiny_draw.inline_elements.fixed_function` | Needs testing | Needs testing |
| `tiny_draw.inline_elements.vertex_shader` | Needs testing | Needs testing |
| `uniform_thrash.uniform_thrash` | Needs testing | Needs testing |
| `vertex_buffer_allocation.disjoint_same_page` | Needs testing | Needs testing |
| `vertex_buffer_allocation.mixed.arrays` | Needs testing | Needs testing |
| `vertex_buffer_allocation.mixed.inline_arrays` | Needs testing | Needs testing |
| `vertex_buffer_allocation.mixed.inline_buffers` | Needs testing | Needs testing |
| `vertex_buffer_allocation.mixed.inline_elements` | Needs testing | Needs testing |
| `vertex_buffer_allocation.rising_transient_growth` | Needs testing | Needs testing |
| `vertex_buffer_allocation.tiny.arrays` | Needs testing | Needs testing |
| `vertex_buffer_allocation.tiny.inline_arrays` | Needs testing | Needs testing |
| `vertex_buffer_allocation.tiny.inline_buffers` | Needs testing | Needs testing |
| `vertex_buffer_allocation.tiny.inline_elements` | Needs testing | Needs testing |
<!-- END V08136 TEST MATRIX -->

### Structural groups and special handling

These five records describe child completion; they are **not five additional timing benchmarks**:

| Structural group | OpenGL | Vulkan |
| --- | --- | --- |
| `game_load.long_unlocked_scene` | Needs testing | Needs testing |
| `game_load.cross_title_hotpath` | Needs testing | Needs testing |
| `game_load.s3tc_sync_factor` | Needs testing | Needs testing |
| `surface.vulkan_memory_pressure.representative` | Needs testing | Needs testing |
| `surface.vulkan_memory_pressure.stress` | Needs testing | Needs testing |

The four `pfifo_packet_boundary.*` leaves require the xemu-only opt-in and are correctness-only. The two `report_query.dma_descriptor_rewrite` / `report_query.dma_range_guard` leaves and all six `shader_lifecycle.*` leaves are also correctness-only in this catalog; their elapsed times are not approved performance references. Shader-lifecycle cases need the runner's separate-process handling and additional host evidence for shader-mechanism claims. Memory-pressure checkpoints must retain their complete route. The word `vulkan` in a test ID does not establish that it passed on Vulkan or that its OpenGL control is invalid.

Use the **same pinned ISO, catalog/test revision, selection/route, fixed work, renderer, configuration, seed/cache policy, environment and metric contract** for baseline/candidate comparisons. Record each executable's own SHA-256 separately. A failing baseline followed by a passing candidate is a compatibility repair, not a percentage speedup. Never replace the fixed upstream baseline with a newer fork merely to make the suite pass.

The [earlier fork-cycle audit](docs/HISTORICAL-FORK-XISO-COMPATIBILITY.md) is retained as historical evidence only. Update this table and the JSON together when exact-baseline evidence is available; attach run IDs, hashes and per-test outcomes rather than deriving PASS from a successful API request.

## Connect from the agent/build machine

The tester must already be running; [operator setup](#operator-setup-on-the-tester) is separate from running tests remotely.

Use Python 3.10 or later. No Python packages need installing. Keep these files from the **same checkout** together in `scripts/`:

```text
runner_tests.py          Commands for normal test operation
runner_transport.py      HTTP and resumable file transfers
runner_test_results.py   Result, comparison and diagnostic commands
runner_wait.py           Quiet completion waiting and read reconnects
runner_xiso.py           Optional category/individual XISO campaigns
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

## Select XISO subsystems or individual tests

Once an operator registers a matched XISO suite and prepared clean seed, open `/xiso` for category checkboxes and individual-test selection. Categories are `cpu`, `commands`, `shaders`, `textures`, `geometry`, `surfaces`, and `scenarios`; future unmapped tests remain visible under `other`.

```sh
python scripts/runner_xiso.py suites
python scripts/runner_xiso.py categories shader-pilot
python scripts/runner_xiso.py tests shader-pilot --category shaders
python scripts/runner_xiso.py select build-149 --id pr149-shaders --category shaders
python scripts/runner_xiso.py start pr149-shaders
python scripts/runner_xiso.py wait pr149-shaders
```

`shader-pilot` is an example registered name. Add `--suite NAME` when more than one suite is installed. Repeat `--category` or `--test STABLE_ID` to select a union. Most settings stay on the tester: missing values inherit the pinned suite defaults and `plan` shows the full frozen configuration. Selection never starts tests unless `--start` is present.

The newest reviewed shader pilot has **160 leaves and five structural groups**, including six shader-lifecycle cases. `targets` reports its exact candidate ISO/catalog pins; it is not silently treated as a qualified baseline. The runner reads the catalog from the actual ISO, creates deterministic chunks, preserves required checkpoint groups, and launches shader-lifecycle cases independently. `--reference APPLICATION_ID` freezes an ABBA schedule per chunk.

The runner injects a small plan into the private HDD before boot, retains exact guest results and plan receipts after exit, then deletes that transient disk. No guest network or ISO rewrite is needed. [XISO campaigns](docs/XISO-CAMPAIGNS.md) covers one-time registration, the prepared FATX slot, available defaults, and qualification limits.

## Use the right identifier

| Identifier | Where it comes from | Used for |
| --- | --- | --- |
| Test name + revision | `list` | Choosing an immutable configuration. |
| Application ID | Your upload `--id`, e.g. `build-149` | Reusing the uploaded build with `select`. |
| Test request ID | Upload/select receipt, e.g. `build-149-t001` | `start`, `status`, `wait`. |
| Campaign ID | XISO `select --id` | Frozen multi-chunk selection, `start`, `status`, `wait`, `attempts`. |
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

`diagnostics` reads a small crash/bundle report; only `--out` downloads its verified ZIP. A confirmed crash stays `crashed`, and other requested tests can proceed after ownership is released and the attempt is archived. Successful runs do not create failure-labeled screenshots.

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
| Category/individual XISO selection and campaigns | [XISO campaigns](docs/XISO-CAMPAIGNS.md) |
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
