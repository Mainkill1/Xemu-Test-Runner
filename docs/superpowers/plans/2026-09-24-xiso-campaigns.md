# XISO category campaigns implementation plan

**Goal:** Select a subsystem or exact stable tests with a small HTTP request, run an immutable suite through disposable HDDs, and retain complete campaign evidence.

**Approved design:** Immutable XISO/catalog, no guest network, offline private-HDD plan injection, deterministic route-aware chunks, explicit start and indefinite observer wait. The latest reviewed shader pilot is xemu-perf-tests PR 44 at 51bc23d3706dea771b76545594256376d8588364 (160 leaves / five structural groups); its reported ISO is b944d317035779afb15b7f7a0d90b0e35dd93b2257addba78c073438979cbf14. It remains candidate/route evidence, not a qualified performance baseline.

**Architecture:** Extend the existing saved test and requested-test scheduler, not a second executor. Register a suite once from a pinned template, read catalog.json from its actual XISO, import shared media once, resolve categories/leaf IDs into immutable child definitions, and only persist execution authorization on explicit start. Use a narrowly scoped preallocated FATX configuration slot in a clean seed; no filesystem mounting or general disk editor.

**Implementation:**
- [ ] Tests first: real HTTP registration, catalog identity, selector validation, optional defaults, immutable plans, no auto-start, restart/cancellation, native materialization and cleanup contracts.
- [ ] Bounded XDVDFS catalog reader, seven named subsystem categories plus visible Other for future unmapped suites, atomic memory-pressure dependencies and independent shader pilot launches.
- [ ] Private FATX plan-slot writer for raw/self-contained QCOW2, with preallocated-only writes, private ownership, readback verification and source preservation.
- [ ] Suite registry, server-side campaign planning and serial dispatch into existing requested tests. Persist schedule before authorizing anything; retain every attempt, including crashes and preparation failures.
- [ ] Extract guest completion receipt and exact results before transient deletion. Validate actual leaf coverage and pinned oracle subsets; missing oracles never self-approve.
- [ ] Thin Python selection/start/wait/report commands and browser catalog/selection page. No agent timers, guest networking, ISO rewriting, or extra background sampling.
- [ ] Verify full Windows/Linux workflows and record exact head. Native Xbox/GPU qualification stays distinct from fixture tests.

**Defaults:** category/test selectors are unions. No selector resolves a small smoke selection; explicit full/monolithic modes are available. Omitted measurement settings inherit the saved suite defaults. Identity and effective values are frozen before start. A/B schedules share the same plans and settings.

**Review focus:** stale XISO/catalog pairs; unsplittable checkpoint groups; interrupted publication and duplicate starts; missing guest receipts; malformed or shared QCOW2 clusters; unconfigured seed slots; unknown selectors; partial oracle coverage; and startup recovery that must never replay an archived child.
