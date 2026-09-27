# XISO baseline compatibility — 2026-09-26

## Decision

The fixed cycle baseline has **144 OpenGL leaf tests with retained guest PASS observations, one failing leaf, and four unexecuted leaves**. Those observations are useful for planning and diagnosing candidate comparisons, but **zero performance-reference approvals** can be recovered from this failed full-suite campaign. Vulkan was not run in that campaign.

The exact IDs, baseline executable, XISO/catalog pins, settings, evidence links, and candidate-pilot additions are recorded in [the machine-readable audit](evidence/xiso-baseline-compatibility-20260926.json). This is an evidence inventory, not a new correctness oracle, deployed matcher, or baseline change. The runner does not automatically consume this file.

## Which baseline?

The [fixed selection record](https://github.com/Mainkill1/xemu/blob/9f618d6d8c4c446ef023955f3d4de22f661f61a4/docs/performance/baseline-selection.json) distinguishes the documentation-aligned cycle commit from the retained executable's product source:

| Identity | Pin |
| --- | --- |
| Cycle | `baseline-bd1fecb9-cycle-01` |
| Cycle commit | `9f618d6d8c4c446ef023955f3d4de22f661f61a4` |
| Compiled source | `c17591d59c270b352b72e648f5ed65e4b2a3e77e` |
| Equivalent product commit | `bd1fecb93353272dda2a810991e28945de35b665` |
| Retained EXE SHA-256 | `3489fdcc593e942b92a612bf35a98f509ff0907e3370e1e5f45f2972d83fb16b` |
| Tested XISO source | `442ec11b52ce1c9d7359825944bcbc2b5f524d07` |
| Tested XISO SHA-256 | `3896df77a75fc35a6212f30b46cfd836fe65f86dbfc2004bd756c5fd7f1aacbe` |

These are repository-selected identities, not a claim that the live tester currently has the same baseline pin. Read `runner_tests.py baseline` to check the deployed pin; do not change it implicitly.

## Retained coverage

The [campaign report](https://github.com/Mainkill1/xemu-perf-tests/blob/1f5cc9d8629df9a9157d676c39545595e799a37a/docs/evidence/baseline-campaign-20260909/REPORT.md) specifies 149 leaves plus five structural groups: **154 required records**, not 149 total. Each of its two OpenGL attempts returned 150 records: 149 reported PASS, including five groups, and one reported FAIL.

| Classification | Leaves | Meaning for a candidate |
| --- | ---: | --- |
| Observed guest PASS | 144 | Historical execution observations on the exact old ISO; not proof of repeatable hash consensus or qualified timing. |
| Observed guest FAIL | 1 | `report_query.dma_range_guard`; preserve a known baseline failure. Candidate PASS may show a repair, not a percentage speedup. |
| Not executed | 4 | Missing opt-in PFIFO cases; unknown execution compatibility, not PASS and not confirmed incompatibility. |
| Approved performance reference | 0 | Both full-suite attempts remain explicitly invalid for performance. |

The four missing IDs are:

```text
pfifo_packet_boundary.array_element16_overflow
pfifo_packet_boundary.array_element32_overflow
pfifo_packet_boundary.inline_array_overflow
pfifo_packet_boundary.incrementing_inline_fallback
```

Their `enable_xemu_only_tests` opt-in was omitted. They are correctness-only tests and must not become timing benchmarks. See [perf-tests #10](https://github.com/Mainkill1/xemu-perf-tests/issues/10). The DMA-range result is retained in [xemu #60](https://github.com/Mainkill1/xemu/issues/60), regardless of later repairs to other builds.

The old runner required live markers absent from the baseline. An existing strict output-only path was identified in [perf-tests #11](https://github.com/Mainkill1/xemu-perf-tests/issues/11); this does not retrospectively approve those attempts. Do not add a marker waiver, patch the fixed baseline, or convert diagnostic timings into performance evidence.

## Do not mix XISO generations

The runner at `07258b38690e79a4de0d62f5f7afd5d58f4ff4dc` advertises `shader-pilot-51bc23d`: **160 leaves plus five groups**, ISO SHA-256 `b944d317035779afb15b7f7a0d90b0e35dd93b2257addba78c073438979cbf14`. See [XISO-CAMPAIGNS.md](XISO-CAMPAIGNS.md).

Comparing the two pinned generated catalogs finds all 149 old stable IDs plus **11 added leaves**: four pipeline/texture cases, one cubemap fallback case, and six shader-lifecycle cases. Their exact IDs are in the audit JSON. This establishes inventory overlap only. The 144 old PASS observations cannot be copied onto the new ISO; the complete pilot still requires validation against the fixed baseline and its own matching reference.

[Perf-tests PR #44](https://github.com/Mainkill1/xemu-perf-tests/pull/44) was subsequently reduced to a readiness suite, removing the capacity experiment. Its later image is another artifact, not an alias for the runner's pinned pilot.

The separately retained ENG523 image (`10ca07f2…`) and its [offline OpenGL/Vulkan revalidation](https://github.com/Mainkill1/xemu-perf-tests/blob/bf8dbe70f12f4d97f59f3f8e14b04fe9a04bf0f9/docs/oracle-revalidation.md) concern a different guest image and xemu build. Their passing results cannot fill missing coverage for this cycle baseline.

## Matching contract to preserve

A reference/candidate pair must share the actual XISO hash, catalog, stable test ID and revision, resolved route/dependencies, fixed work and completion settings, guest seed/state, cache contract, renderer/scale, environment, and measurement procedure. Pin each executable and its available source/build provenance separately: reference and candidate executable hashes intentionally differ.

Keep execution, correctness, evidence validity, and per-metric comparison eligibility separate. Missing IDs remain visible. Structural groups are not independent timing samples. An explicit subset of the 144 observed-pass leaves needs its own resolved selection and validated reference; never present it as a full-suite pass. Memory-pressure checkpoints retain their inseparable route and shader-lifecycle cases retain their required fresh-process behavior.

Use the existing runner-owned suite registration, result collection and ABBA campaign machinery rather than another scheduler. First read the live baseline, registered suite and effective plan. Then qualify the exact selected workload on the unchanged baseline with supported output evidence, retaining negative results, before approving candidate metrics. Candidate-only diagnostics are supplemental and must not become requirements the old baseline cannot satisfy. Follow-up implementation belongs with [#62](https://github.com/Mainkill1/Xemu-Test-Runner/issues/62).

## Validation boundary

This audit checked the repository baseline selection, retained campaign report, a raw normalized-output excerpt, and both pinned generated catalogs. The JSON inventory was checked for valid syntax, unique/disjoint status sets, 149 old leaves, five groups, the 144/1/4 split, and 11 new IDs giving 160 pilot leaves. Full raw reports were not re-parsed, re-sealed, or promoted to oracles.

A direct read of the documented tester HTTP origin was refused in this environment. No live baseline/suite inventory was retrieved, no native xemu test was started, and no runtime setting, test selection, baseline pin, or historical evidence was changed.
