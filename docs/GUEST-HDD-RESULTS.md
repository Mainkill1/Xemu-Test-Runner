# Automatic guest HDD results and A/B statistics

This adapter is part of normal finalization, not a scheduled external script or another executor. After an explicitly requested workload stops, the tester extracts its declared guest file, validates it and publishes measurements into the existing executable-hash result pipeline. Upload/select/read/compare still never authorize execution.

## What becomes automatic

```text
explicit test execution
    -> confirmed process exit
    -> targeted read of private HDD result file
    -> preserve original guest bytes + extraction receipt
    -> validate pinned record set, fixed work and functional hashes
    -> calculate per-leaf statistics
    -> canonical workload assessment/result
    -> ordinary archived result indexing
    -> server-side A/B table with Improvement % last
```

The original xemu-perf-tests file is JSON despite its `results.txt` extension, normally `E:\xemu_perf_tests\results.txt`. This implementation targets its schema-version-1 array with stable leaf/group IDs, work parameters, `raw_results` in microseconds and applicable correctness hashes. It does not parse text printed by an agent or derive guest timing from host CPU samples.

## Configure a saved test once

Add `Workload.GuestHddResults` to the existing JobDefinition. Its fields are:

| Field | Meaning |
|---|---|
| Image | Destination path of a declared private RuntimeState HDD, relative to that attempt's runtime directory. |
| PartitionOffsetBytes | Verified FATX partition offset in the virtual/raw disk, not a QCOW2 physical-file offset. |
| PartitionLengthBytes | Actual partition length used to derive FATX allocation-table geometry. |
| GuestPath | Path relative to that partition; default `xemu_perf_tests/results.txt`. Do not include `E:`. |
| ExpectedResults | Package-relative known-correct guest result file for exactly the intended workload/revision. Include it in the payload manifest/RequiredFiles. |
| ExpectedResultsSha256 | SHA-256 of that complete reference file. The test library then pins this contract automatically. |
| MaximumResultBytes | Optional, 1..16777216; default 16 MiB. |

Use the partition geometry from the retained test-disk profile. The reader intentionally does not guess an E-partition size from a filename or scan a whole disk for signatures. Offset zero is supported for partition-only raw test images, but it is not the ordinary whole-HDD E-partition location.

The seed must be immutable and must not already contain GuestPath. Every execution uses the runner's private runtime copy. A stale seed is rejected rather than deleting a user's files or formatting any image. The configured image must match a materialized runtime destination; arbitrary host images and live/held images are not accepted.

Existing saved tests omit this property and retain their original canonical serialization. Enabling it is a new test revision. Invalid field names within the new definition, unsafe paths, unpinned references and invalid geometry are rejected during JSON deserialization as well as extraction.

The test's existing procedure must reach workload completion and stop xemu normally. This is a post-run collector, not a new guest completion detector. Do not wait inside the running plan for `guest/results.txt` to appear on the host: extraction occurs after process exit. Partial output from a timeout/forced stop is preserved when readable but never upgraded to a successful complete test.

## Read-only and bounded

The native C# adapter supports original-Xbox FATX16/FATX32 on raw disks and self-contained QCOW2 version 2/3 normal clusters. It follows the configured directories and allocation chain, including fragmented files. There is no filesystem mount, administrator requirement, qemu-img conversion, whole-HDD network download, external process, image rewrite or additional dependency.

QCOW2 backing chains, encrypted/compressed clusters, dirty/corrupt images and incompatible external-data/extended-L2 features are rejected explicitly. Unsupported images do not silently yield zero-filled plausible results. Current FATX extraction is little-endian original-Xbox FATX, not Xbox 360 XTAF.

Bounds include a 16 MiB result/reference limit, 64 MiB physical-read budget per image, 1 MiB per parent-directory scan, 16 path components and 65,536 visited clusters. Allocation cycles, invalid addresses and truncated chains fail. The 30-second processing deadline is checked between reads; it is not an operating-system guarantee that a stalled physical I/O request can be forcibly aborted. Runtime process ownership must be released first. External modification of the private workspace is unsupported.

CI includes a 512 MiB FATX32 partition with a nonzero offset and verifies that extraction reads less than 128 KiB from the two image files combined. That is a fixture bound, not a claim that every real result file will fit in 128 KiB.

## Evidence and validity

The result directory receives:

- `guest/results.txt`: byte-exact file extracted from the current private image, including partial JSON when readable.
- `guest/extraction.json`: run identity, format/path, byte count, raw/reference SHA-256, reader byte count, freshness rule and parser version.
- `guest/normalized-results.json`: checked records, applicable assertions and per-leaf statistics, only after parsing completes.

All are available through the existing run artifact API. No agent needs to connect to the host or retrieve the HDD. Existing raw evidence is never overwritten with different bytes for the same attempt.

Record IDs must match the reference set exactly. The parser rejects duplicate IDs/properties, unsupported schema and malformed sample counts; fixed work parameters must match the reference. Explicit failed outcomes/oracle status cannot pass. Applicable `source_kat`, `work_checksum`, `result_checksum` and `framebuffer_fnv1a64` values are compared with the pinned reference; leaves without applicable reference hashes remain unverified. Framebuffer comparison disabled by the reference is not treated as a correctness check. This adapter does not replace every workload-specific oracle semantic with a generic inference.

Groups establish structure but contribute no timing measurements. Per-leaf mean, median, min, max and nearest-rank p95 are computed from guest `raw_results`, which are already iteration-normalized microseconds. They are not divided by the multiplier a second time. Their names are `xiso/<stable-id-with-slashes>/<stat>_us`; host/workload metrics retain their existing identity. A 144-leaf/5-group fixture produces 720 per-leaf measurements without exceeding the bounded 4,096-metric index limit.

Extraction, record or oracle failure makes the canonical evidence/correctness outcome fail or incomplete. Existing comparison eligibility then excludes the affected attempt/cohort from improvement claims. The default KeepOnFailure behavior retains failed runtime state under the existing cleanup policy. No successful state is manufactured from process exit alone.

## A/B report contract

The existing API and thin client remain the entry points:

```http
GET /api/v1/compare?A=REFERENCE_SHA&B=CANDIDATE_SHA
GET /api/v1/compare?B=CANDIDATE_SHA&format=markdown
GET /api/v1/compare?A=REFERENCE_SHA&B=CANDIDATE_SHA&format=csv
```

```sh
python scripts/runner_tests.py compare --a REFERENCE_SHA --b CANDIDATE_SHA
python scripts/runner_tests.py compare --b CANDIDATE_SHA --format csv --out comparison.csv
```

JSON rows retain the previous `changePercent = (B / A - 1) * 100`. New `statsA`/`statsB` provide count, mean, median, min, max and sample standard deviation across attempts. A single attempt has no sample standard deviation. `improvementPercent` is separate:

- Lower is better: `(A - B) / A * 100`.
- Higher is better: `(B - A) / A * 100`.
- Neutral, zero/nonpositive reference or incomparable results: null / N/A.

Positive means improvement, negative means regression. A runtime dropping from 100 to 80 is a 20% time reduction; this is not relabeled as the different 25% reciprocal speedup. The end user does not calculate it.

Markdown groups by test/context and metric section, including XISO families. Every table ends in **Improvement %**. Full comparison CSV also ends with `improvement_percent` and includes A/B distributions. Full metric IDs remain intact in machine-readable output. Small JSON/Markdown views retain omission counts; full comparison CSV and per-run raw/normalized guest artifacts are explicit secondary requests.

The A/B unit of repetition is an execution attempt, not an individual guest timing sample. We compute statistics inside each run first, then compare per-attempt values. No unrelated subtest percentages are averaged into a fictitious whole-emulator speedup. Existing compatibility gates remain and this is a descriptive comparison, not statistical significance.

## Qualification and source formats

Tests exercise synthetic raw/QCOW2 images, fragmented/cyclic FAT chains, nonzero-offset FATX32, missing/stale/partial results, wrong hashes, an entire synthetic 149-record suite through canonical indexing and baseline comparison, original-byte artifact downloads and unsupported-feature refusals. They do not launch a real Xbox workload or qualify a particular user's HDD/GPU.

Format references used for the implementation:

- QEMU QCOW2 specification: https://www.qemu.org/docs/master/interop/qcow2.html
- FATX layout/reading notes by the FATX-tools author: https://aerosoul94.github.io/blog/2020/02/25/fatx-reading-and-recovery.html
- Guest writer at reviewed source `bf8dbe70f12f4d97f59f3f8e14b04fe9a04bf0f9`: https://github.com/Mainkill1/xemu-perf-tests/blob/bf8dbe70f12f4d97f59f3f8e14b04fe9a04bf0f9/src/test_host.cpp
- Pinned guest record/reference format: https://github.com/Mainkill1/xemu-perf-tests/blob/bf8dbe70f12f4d97f59f3f8e14b04fe9a04bf0f9/resources/reference-results.txt
