# XISO campaigns implementation plan

Goal: an agent chooses a system category or stable test IDs, not a large JobDefinition. The runner owns expansion, private-HDD preparation, explicit start, queueing, coverage and cleanup.

Approved architecture: immutable ISO/catalog bundle; no guest networking; inject only the resolved guest configuration into a private disk before boot; preserve exact result/plan receipts after exit. Existing queue and process ownership remain authoritative.

Implementation sequence:
- [ ] Add tests for registration identity, category/leaf discovery, compact defaults, dependency closure, immutable campaign requests and explicit start.
- [ ] Add a suite-bundle producer on the shader-pilot guest branch so ISO SHA, exact catalog bytes, category membership and source commit travel together. A candidate build is not silently labeled qualified.
- [ ] Add bounded standalone-QCOW2 preparation and fixed-path FATX config injection; no mutation of shared seeds or mounted/live disks.
- [ ] Add runner suite registry, deterministic execution-unit chunks and durable parent campaigns backed by the existing requested-test dispatcher.
- [ ] Retain resolved settings and exact plan coverage, with guest receipt validation before transient disk deletion.
- [ ] Add thin client commands and a browser category/individual selector. Default to a small stored smoke selection when no selector is supplied; full-suite work requires an explicit mode/selection.
- [ ] Verify Windows/Linux HTTP/client, storage and existing regression workflows. Validate generated QCOW2 with qemu-img in Linux CI, not just the writer's own parser.

Identity and comparison constraints: never pair a new catalog with an older ISO. An existing suite ID and campaign ID are immutable. Defaults are frozen at campaign creation; nulls/unknown settings are rejected, not interpreted as permission to guess. A/B uses the same resolved work and declared order. Incomplete, crashed or cancelled children remain in coverage. Different shard membership/order/cache context is not interchangeable benchmark evidence.

No live deployment, automatic baseline promotion, test starts or raw HDD retention is authorized merely by creating a suite/campaign. Full VM snapshot extraction is not part of this XISO-only writer.
