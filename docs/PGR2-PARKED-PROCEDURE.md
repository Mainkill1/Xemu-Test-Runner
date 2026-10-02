# PGR2 parked benchmark procedure

The authoritative menu/input course is [`procedures/pgr2-parked-v1.json`](../procedures/pgr2-parked-v1.json). It was extracted from the saved Deck test `pr267-deck-pgr2-stationary-base-v6` at revision `bbdb66578f2f7200d14164d98c56c63e57300d1af782f1771872c5627c24deee`. The contract captures only the portable input sequence and measurement boundary; it does not publish game media, firmware, a save, or machine-specific launch paths.

The course waits 30 seconds before the first A press, then 12 seconds, then uses 4-second menu gaps. Ten A presses last 100 ms each; the single `LStickRight` press lasts 80 ms. The last A press is followed immediately by `segment_start` for `stationary-start`. The measurement waits 30 seconds while parked. There is no right-trigger acceleration or other gameplay input during that interval.

Use the read-only checker with each box's immutable test ID and full revision:

```bash
python scripts/gameplay_procedure.py \
  --url http://WINDOWS_TESTER:9368 \
  --test-id WINDOWS_PARKED_TEST \
  --revision FULL_64_HEX_REVISION \
  --other-url http://DECK_TESTER:9368 \
  --other-test-id DECK_PARKED_TEST \
  --other-revision FULL_64_HEX_REVISION
```

Both checks must return `ok: true` with procedure SHA-256 `138d85703e09c6d45027f6784ba6df7fafe8410bd0c9b2ec636b7e1e78a01f0f`. The checker accepts native controller or keyboard transport as separate recorded facts; it does not qualify their physical behavior as equivalent. It refuses a moving test, altered hold/wait, a delayed measurement start, missing frame-evidence minimum, or input during the measurement. A 400 definition-integrity response is a failed preflight, not a reason to use the catalog summary as though the test were valid.

The procedure checker does not recognize the start-line image or prove guest input consumption. Before publishing a result, inspect the start/end screenshots and confirm both hosts show the same parked scene. Report frame-time average, p95, p99, maximum, sample count, and guest cadence for the same 30-second interval, with all failed runs retained. A moving route changes the spatial region sampled when hosts run at different FPS and must not be compared with this parked course.

Input delays are still wall-clock based. `minimumFrameSamples` validates post-run evidence; it does not pace the menu. The merged runner currently reports `framePacingAvailable: false`. Until a qualified external guest-frame observer and executor gate are implemented, do not describe the input course as frame synchronized or convert seconds to nominal FPS. Generous loading waits reduce the risk of early input but cannot replace scene confirmation.
