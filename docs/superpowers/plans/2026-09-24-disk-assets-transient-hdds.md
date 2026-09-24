# Runner-Managed Disk Assets and Transient HDDs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Store Xbox HDD seeds/save-state carriers once, materialize only one private runtime HDD for an explicitly requested run, preserve guest evidence, then delete transient disks safely.

**Architecture:** A runner-owned `Workspace/DiskAssets` catalog holds immutable content addressed by stable ID plus SHA-256. Saved tests reference catalog assets through `RuntimeState.DiskAssets`; materialization makes a byte-identical private runtime copy and existing xemu config/`SnapshotName` selects it. The first version deliberately does not convert/extract xemu internal snapshots or use QCOW2 backing chains because xemu save states include VM state stored in the HDD image and the current bounded guest reader rejects backing chains.

**Tech Stack:** .NET 10, existing embedded HTTP server/FileUploadStore, JSON manifests, existing RuntimeState/GuestHddResults pipeline, Python standard-library client.

**Spec:** `docs/DISK-ASSETS.md`

## Global Constraints

- Upload/select/config operations never start a test.
- No arbitrary host paths in saved tests; only catalog asset ID + complete SHA-256 + runtime destination.
- Catalog content is immutable after verified publication.
- `deleteAfterEvidence` is the default runner-managed disk retention policy.
- A transient disk is never deleted while process ownership is uncertain.
- Guest result extraction and crash/state evidence happen before transient deletion.
- Startup recovery may retry only a previously-authorized cleanup receipt; it never infers process death from missing metadata.
- Snapshot carriers are copied byte-for-byte in this version; no claim that `qemu-img convert -l` preserves xemu VM save-state data.
- Existing package RuntimeState files remain compatible.

## Review Focus

- Interrupted upload: resume the same asset without publishing corrupt content.
- Catalog tampering/hash mismatch: fail before target launch and keep source evidence intact.
- Crash/timeout: finalize bounded evidence and delete transient disk only after confirmed exit.
- Held/preserved target: keep runtime HDD because ownership/evidence finalization is incomplete.
- Runner crash during deletion: a durable cleanup receipt allows safe startup retry.

---

### Task 1: Disk asset catalog and HTTP lifecycle

**Files:** create `Runtime/DiskAssetCatalog.cs`, `Networking/EmbeddedHttpServer.DiskAssets.cs`; modify agent routing/discovery; test `AgentChecks/DiskAssetApiChecks.cs`.

**Interfaces:** `DiskAssetCatalog` resolves only catalog-owned paths and returns immutable manifests; HTTP create/list/detail/upload/delete uses existing FileUploadStore.

- [ ] Write failing API tests for create/list/detail, resumable-ready status, idempotent create, conflict, benchmark transfer policy and reference-protected delete.
- [ ] Verify those tests fail because the routes do not exist.
- [ ] Implement the bounded catalog and routes.
- [ ] Run AgentChecks on Windows/Linux CI.

### Task 2: Job schema and private materialization

**Files:** modify `JobContracts.cs`, `JobDefinition.cs`, `RuntimeStateManager.cs`, `InputManifestBuilder.cs`; test `DiskAssetChecks`.

**Interfaces:** `RuntimeState.DiskAssets[] = { AssetId, ExpectedSha256, Destination, Retention }`; materializations retain DiskAssetId/Retention provenance.

- [ ] Write execution fixtures using raw JSON so the red test compiles before new schema types exist.
- [ ] Verify xiso seed is currently ignored and cleanup evidence is absent.
- [ ] Implement ID/hash validation and verified byte-for-byte catalog copy.
- [ ] Verify a snapshot-carrier copy remains byte-identical and existing `SnapshotName` becomes `-loadvm`.
- [ ] Reject catalog hash mismatch before process start.

### Task 3: Evidence-before-delete and recovery

**Files:** modify `RuntimeStateManager.cs`, `RunnerEngine.cs`; tests in `DiskAssetChecks`.

**Interfaces:** `runtime-cleanup.json` records target-stopped/evidence-finalized authorization, files, state and errors.

- [ ] Write failing tests for default delete-after-evidence, keep retention, unsafe held state, and startup retry of an authorized pending cleanup.
- [ ] Implement selective deletion after workload/diagnostic finalization.
- [ ] Leave transient disks intact when target ownership/evidence is uncertain.
- [ ] Retry only `pending` authorized receipts at startup.

### Task 4: Agent-friendly client and docs

**Files:** modify `runner_transport.py`, `runner_tests.py`; create client fixture and `docs/DISK-ASSETS.md`.

**Interfaces:** `disk-list`, `disk-show`, `disk-upload`, `disk-delete`; upload remains resumable and never starts a test.

- [ ] Write client fixture proving disk upload makes no start/submit request.
- [ ] Implement generic resumable file upload and thin disk commands.
- [ ] Document migration away from duplicated package HDD seeds.
- [ ] Document snapshot limitation and one-transient-copy behavior.

### Task 5: Integrated verification

- [ ] Run full Windows/Linux build/regression suite.
- [ ] Run DiskAssetChecks and Python client fixtures.
- [ ] Confirm crash/state qualification still passes.
- [ ] Record exact CI runs and remaining snapshot-overlay qualification boundary in the PR.
