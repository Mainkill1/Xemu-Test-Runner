# Runner-managed disk assets and transient Xbox HDDs

Large Xbox HDD images are content assets, not per-test package payloads. The runner stores each immutable disk once under `Workspace/DiskAssets`; saved tests pin an asset ID and its full SHA-256, then receive a private runtime copy only after an explicitly requested test is claimed.

## Why snapshot carriers are copied, not extracted

xemu save states are whole-machine snapshots stored in the HDD image. They include machine state in addition to the guest-visible disk state. QCOW2 internal snapshots change the disk mapping and may contain VM-state metadata. A disk conversion of one internal snapshot therefore is not treated as a preserved xemu save state.

This version accepts `snapshot-carrier` assets, copies the carrier byte-for-byte into the private runtime directory, and lets the existing `SnapshotName` path add `-loadvm NAME`. Only one test executes at a time, so persistent test definitions share one carrier and only the active run consumes a temporary copy. No backing-chain, reflink or extracted-snapshot optimization is claimed until native xemu qualification proves it.

For XISO tests, use `xiso-seed` with a small self-contained blank FATX/QCOW2 image. QCOW2 virtual size may be much larger than the physical catalog file. Do not use xemu `-snapshot` for guest-result tests: the runner needs the stopped run's modified private disk long enough to extract the guest result before deletion.

## Catalog API

```http
POST /api/v1/disk-assets
{"id":"xiso-empty-v1","kind":"xiso-seed","length":123456,"sha256":"<64 hex>"}

PUT /api/v1/disk-assets/xiso-empty-v1/content
GET /api/v1/disk-assets
GET /api/v1/disk-assets/xiso-empty-v1
GET /api/v1/disk-assets/xiso-empty-v1/content?upload-status=1
DELETE /api/v1/disk-assets/xiso-empty-v1
```

Large uploads use the existing Content-Range/X-Upload-Id resumable protocol. Published content is immutable. Deletion scans retained Pending/Testing/Tested API/test-library JSON and refuses an asset that is still referenced.

To deduplicate an existing retained API package without moving the HDD over the network:

```http
POST /api/v1/disk-assets/imported-v1/import
{"sourceJobId":"old-seed-package","path":"seeds/xbox_hdd.qcow2"}
```

Create the immutable catalog manifest first using the file declaration's length/SHA-256. The import is local, verified through the same publication path and blocked by active benchmark bulk-transfer policy.

## Saved test reference

```json
{
  "RuntimeState": {
    "Enabled": true,
    "DiskAssets": [
      {
        "AssetId": "xiso-empty-v1",
        "ExpectedSha256": "<full catalog SHA-256>",
        "Destination": "xbox_hdd.qcow2",
        "Retention": "deleteAfterEvidence"
      }
    ]
  }
}
```

Point the managed xemu TOML at `{runtimeDir}/xbox_hdd.qcow2`. Asset content is copied and SHA-256 verified before target launch. Runtime/input evidence records the asset ID, kind, byte count, digest and retention policy. Test configs never contain host catalog paths.

Retention values:

- `deleteAfterEvidence` (default): delete after confirmed process exit and finalized workload/diagnostic evidence, including crashes and failed tests.
- `keepOnFailure`: delete on successful evidence; keep the runtime disk for a failed attempt.
- `keep`: retain the runtime disk explicitly.

The package's older `KeepOnSuccess/KeepOnFailure` still controls ordinary RuntimeState files. Disk retention is separate so a failed test can retain a small EEPROM/save seed while deleting a huge boot-only HDD.

## Evidence-before-delete and recovery

Before deleting a transient asset the runner writes `Results/RUN_ID/runtime-cleanup.json` with `targetStopped=true`, `evidenceFinalized=true`, exact runtime directory and the authorized asset-relative paths. Deletion updates that receipt to `complete`.

If the runner stops after writing a `pending` receipt, startup may retry exactly those authorized paths. It does not infer permission to delete from a missing process, a stale PID or a partial result. A preserved/held target keeps its disk because evidence finalization is incomplete.

Guest HDD extraction still happens before cleanup. The existing extractor reads the current private disk and the immutable catalog seed; no agent downloads or mounts the HDD.

## Thin client

```sh
python scripts/runner_tests.py disk-list
python scripts/runner_tests.py disk-upload xiso-empty-v1 blank.qcow2 --kind xiso-seed
python scripts/runner_tests.py disk-import xiso-empty-v1 --from-job old-seed --path seeds/xbox_hdd.qcow2 --kind xiso-seed
python scripts/runner_tests.py disk-show xiso-empty-v1
python scripts/runner_tests.py disk-delete xiso-empty-v1
```

These commands never select/start/submit a test. Upload remains resumable. Import copies locally on the tester.

## Migration boundary

Create catalog assets first, create new immutable test-config revisions referencing them, verify the new runs, then remove duplicated HDD payloads only when no retained definitions need them. Historical test definitions/results are not rewritten. An automated multi-package migration/prune tool is intentionally separate from this storage primitive.


Catalog content download is intentionally not a normal API operation in this first version. The catalog exists to avoid moving HDD images repeatedly; raw disk export can be added later with its own retention/authorization contract rather than making agents fetch multi-gigabyte assets by default.
