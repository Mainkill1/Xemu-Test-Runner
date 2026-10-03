# Conker menu checkpoint

This procedure skips the opening cutscene of **Conker: Live & Reloaded (USA)**
for a stationary bar/menu performance test. It was exercised on the Steam Deck
Vulkan tester. It requires an operator-provided game image, firmware, EEPROM,
and a compatible full VM snapshot; the repository does not contain those files.

## Pinned Deck inputs

| Input | SHA-256 | Tester identity |
|---|---|---|
| Conker ISO | `9f9f4e20224865436d96664198d56559ed1f5762cf29410343bdcec4e70692ba` | `conker-usa-readonly-v1` |
| Full VM snapshot carrier | `76b8befb7f2e3de7f75660e7223c1abcaa7c44198cd01f556d8668b52c48fce4` | `conker-bar-deck-v1` |

The 425 MiB QCOW2 carrier contains the internal snapshot `conker-bar`,
including about 58 MiB of VM state. `qemu-img check` completed without errors
before it was registered as a read-only source for private test HDD copies.
The snapshot is a test input, not a replacement for the complete game ISO.
Keep its hash and title-specific firmware/EEPROM hashes in the saved test
revision. Do not substitute a disk-only snapshot with the same name: it lacks
the running Xbox state needed to bypass the cutscene.

## Launch and measurement plan

Start xemu normally with an isolated configuration and copied snapshot-carrier
HDD. Do **not** pass `-loadvm` at process startup. That attempt failed before
the virtual USB hub was initialized. After the emulator starts, execute this
plan through the tester's existing pause/resume and diagnostic monitor API:

```json
[
  { "type": "wait", "delayMs": 5000 },
  { "type": "pause" },
  { "type": "diagnostic", "diagnosticId": "load-conker-bar" },
  { "type": "resume" },
  { "type": "screenshot", "name": "bar-entry", "delayMs": 10000 },
  { "type": "segment_start", "name": "conker-bar-menu" },
  { "type": "wait", "delayMs": 30000 },
  { "type": "segment_end", "name": "conker-bar-menu" },
  { "type": "screenshot", "name": "bar-end" },
  { "type": "quit" }
]
```

Define `load-conker-bar` as a diagnostic monitor action with command
`loadvm conker-bar`. Explicitly permit that diagnostic in the test revision;
ordinary A/B tests should not silently gain monitor access. There is no
scripted gameplay input during measurement, so the camera and menu stay at
the same saved position. Require both screenshots to exist and contain a
non-black image, guest-frame logging, and a completed measurement segment.
Verify that the screenshots show the bar/menu rather than relying only on
the non-black check.

On the Deck tester, the baseline run `20261002-002914736-a52d786fc1924149abc30f1b470bc893`
and candidate run `20261002-003014444-335a0b1eee5348a1a4cd520e4aa8ab73`
both reached this checkpoint. These runs used different xemu binaries but the
same pinned inputs and plan. The full balanced comparison and its limits are
recorded in [xemu PR #284](https://github.com/Mainkill1/xemu/pull/284).

## Qualification boundaries

- This checkpoint was validated on the Deck tester. A Windows VM-state load
  and menu image still need separate validation; do not assume a cross-host
  snapshot is portable.
- A cold xemu shader cache does not establish a controlled Mesa driver cache.
  Keep that state visible and do not mark a comparison eligible by accepting
  uncontrolled driver cache merely to turn its status green.
- Compare the same saved scene, segment duration, renderer, settings, and
  input hashes for both binaries. Report average, p95, p99, maximum frame
  time, and any correctness or evidence failure for every run.
- The snapshot and ISO are private tester inputs. Publish a redistributable
  snapshot only after its provenance and contained state have been reviewed.
