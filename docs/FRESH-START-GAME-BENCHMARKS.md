# Fresh-start game benchmark procedures

These procedures use the existing saved-test HTTP executor and performance
analysis. Preserve `procedures/pgr2-parked-v1.json`: the parked start-line route
has no acceleration. The two new contracts are Conker: Live & Reloaded and
Dead or Alive Xtreme Beach Volleyball (USA).

| Procedure | Input | Measurement |
|---|---|---|
| Conker (candidate, not qualified) | Full Xbox startup; nominal 15-second startup allowance, then 15 seconds and A for 100 ms | 30 seconds after the final input |
| PGR2 | Existing `pgr2-parked-v1` inputs and portable save | 30 seconds parked; no gameplay input |
| DOAXBV | Wait 90 seconds, A for 100 ms; wait 5 seconds, A for 100 ms | 30 seconds after the final input |

Qualification must confirm each intended scene on both devices and both builds.
A non-black screenshot alone does not prove that the menu input succeeded.
Conker's game-start boundary remains under qualification. The candidate's two
15-second waits are wall-clock waits; the first is not a detected Xbox boot
boundary. Native Windows and Steam Deck checks remained in the opening movie.
Do not present that candidate as a working menu benchmark or describe it as
15 seconds after a measured guest startup boundary.

Use one immutable saved-test revision per host/game, and the same procedure for
both application hashes. Run `A B B A`, then `B A A B`; retain failed attempts.
Do not change the pinned baseline, add warmups, or modify delays to make a slow
candidate reach a different scene. The full XISO campaign remains a separate
correctness workload with its own pinned catalog and oracle.

The normal measurement contract is the existing named CPU segment and bounded
25-second guest-frame tail (five five-second flip records), with 55 host samples
and at least 160 guest-frame intervals. Record both windows as distinct: authored
screenshots and finalization can move the frame tail relative to segment bounds.
Use the saved `performance.json` and tester comparison API; no separate sampler.
GPU readings are already in the original `metrics.csv`; unavailable process-GPU
readings must not be replaced by total-device usage without labeling that change.

Both builds need equivalent guest-flip logging for frame-time comparison. Label
an upstream build with this telemetry as **upstream + matched telemetry**, and
retain its exact source and executable hash. Guest flip intervals are not host
presentation counts or proof of identical simulation time.

Check the frozen procedure using the existing read-only helper:

```sh
python scripts/gameplay_procedure.py \
  --contract procedures/doaxbv-menu-fresh-v1.json \
  --url http://TESTER:9368 --test-id SAVED_ID --revision FULL_SHA256
```

## Native qualification, 2026-10-02

Both rigs use runner main `0186f52f18ab983954c473dd42f80c2ff660cbda`, which
includes the Mesa qualification, numeric XISO selectors, balanced XISO campaigns,
and pinned PGR2 procedure checks. The older published `6089e8b8` runner predates
those changes and must not be substituted for this campaign.

- PGR2's procedure hash remains
  `138d85703e09c6d45027f6784ba6df7fafe8410bd0c9b2ec636b7e1e78a01f0f`.
  Windows upstream reached the parked car at the measurement start. Deck
  upstream reached the same course but still displayed the introductory camera
  transition at measurement start; its end screenshot showed the parked car.
  This is a scene-alignment limitation even when the numeric assessment passes.
- DOAXBV reached the island menu on Windows. The Deck's matched-EEPROM retry
  reached the menu by the end screenshot but was still transitioning at the
  start screenshot. An earlier Deck attempt displayed a guest disc error.
  Retain that failure; this retry does not establish its root cause.
- Conker's candidate A press did not skip the movie on either rig. It remains
  unqualified. Do not silently replace A with another button or pool movie
  measurements with menu measurements.

These contracts preserve the requested inputs as reproducible test sources.
Their presence is not proof of consumed guest input, identical simulation time,
a benchmark pass, or a performance result. Numeric correctness checks and scene
qualification are separate. The runner has no qualified external frame provider
for these procedures; their waits remain time-based.
