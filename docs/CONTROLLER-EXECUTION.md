# Native controller execution (draft)

`ControllerInput` selects a distinct gameplay transport for a saved test. A job
without that section retains its existing keyboard behavior and serialized
identity. The native route uses the shared helper from controller foundation
PR #72. It does not start authoring capture, an encoder, or browser media.

The runner creates one neutral OS-visible controller before launching xemu,
refreshes its full state every 40 ms through holds and neutral waits, and
removes it during run cleanup. The native helper independently removes the
device after 250 ms without a valid report. The selected SDL environment is
passed through the actual target launcher. Failed input is never retried through
the keyboard provider.

Native Linux runs currently require a private xemu configuration with these
explicit values:

```toml
[input]
auto_bind = false

[input.virtual_ports]
port1_connected = 1

[input.bindings]
port1 = '0600cc4158656d752052756e6e657200'
port1_driver = 'usb-xbox-gamepad'
```

`RuntimeState.Isolation` must be enabled so the runner produces a run-owned
effective `-config_path`; the authored package configuration supplies these
values. The preflight rejects a
keyboard port, missing explicit binding, or this controller GUID on another
port. Windows native runs are rejected until the helper's device identity and
the target xemu binding are qualified together. A ready OS device or an applied
receipt alone does not prove guest consumption.

Example job fragment (this branch's proposed schema):

```json
{
  "RequireInput": true,
  "ControllerInput": {
    "Backend": "native-os-gamepad",
    "ControllerIndex": 0,
    "MappingProfile": "runner-xbox-port1-v1"
  },
  "Plan": [
    {"Type": "button", "Button": "Start", "DurationMs": 500},
    {"Type": "controller_state", "State": {
      "Buttons": [], "RightTrigger": 255, "LeftX": 16000
    }, "DurationMs": 2000},
    {"Type": "controller_state", "State": {"Buttons": []}, "DurationMs": 250}
  ]
}
```

Adjacent `controller_state` steps replace the whole state without inserting a
release pulse. The runner neutralizes before any other step or at the end.
Named `button` steps resolve to controller controls in native mode; triggers
and stick directions are analog values. The control page exposes a finite
full-state trigger/stick form while a native session is active. Its API requires
the current session ID and rejects stale requests. Manual calls use the same
session and are rejected during an active scripted plan. Host key chords remain
separate.

`controller-input.json` records semantic OS-submission receipts, the helper and
mapping hashes, effective SDL environment hash, refresh count, errors, and cleanup outcome. Refresh reports are
counted, not treated as repeated button presses. This is OS submission evidence;
visual checkpoints or other independent tests are still needed to show the
game acted on the input.

Focused checks:

```text
dotnet run --project tests/GamepadProviderChecks -c Release
dotnet run --project tests/ControllerIntegrationChecks -c Release
dotnet run --project tests/AgentChecks -c Release
```

This draft has no qualified Windows target binding, native PGR2 result, or
external frame pacing. Those gates remain open.
