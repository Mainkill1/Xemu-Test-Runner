# Input-pacing settings

Open `/settings/input-pacing` on the runner to read and edit the runner-owned default input-pacing profile. The page uses the same versioned HTTP API available to automation; it does not maintain a separate browser-only copy.

The settings surface is deliberately application-independent. It does not inspect or modify xemu, inject hooks, or claim that externally submitted controller input was consumed by a guest. Frame pacing becomes effective only when the runner reports a qualified external frame provider. The current implementation reports that provider as unavailable and resolves an enabled profile to the explicitly configured time-only fallback when fallback is allowed.

## Read settings

```http
GET /api/v1/settings
GET /api/v1/settings/input-pacing
```

The focused response contains the persisted revision, update time, settings, host capabilities, and resolved mode:

```json
{
  "revision": 1,
  "updatedUtc": null,
  "settings": {
    "enabled": false,
    "allowTimeOnlyFallback": true,
    "preparation": {
      "minimumMs": 0,
      "minimumFrames": 0,
      "fallbackMs": 0,
      "maximumMs": 15000
    },
    "hold": {
      "minimumMs": 250,
      "minimumFrames": 15,
      "fallbackMs": 1000,
      "maximumMs": 5000
    },
    "neutral": {
      "minimumMs": 250,
      "minimumFrames": 1,
      "fallbackMs": 250,
      "maximumMs": 5000
    }
  },
  "capabilities": {
    "framePacingAvailable": false,
    "frameProvider": "unavailable"
  },
  "effectiveMode": "off"
}
```

`effectiveMode` is one of:

- `off`: pacing is disabled;
- `hybrid`: the selected host can enforce both elapsed-time and external-frame minimums;
- `timeOnlyFallback`: pacing is enabled, no qualified frame provider is available, and fallback is explicitly allowed;
- `unavailable`: pacing is enabled but the requested mode cannot run on this host.

No response invents frames from elapsed time, nominal FPS, monitor refresh, or screenshot requests.

## Update settings

Replace the complete profile using the revision returned by the preceding GET:

```http
PUT /api/v1/settings/input-pacing
Content-Type: application/json
```

```json
{
  "expectedRevision": 1,
  "settings": {
    "enabled": true,
    "allowTimeOnlyFallback": true,
    "preparation": {
      "minimumMs": 0,
      "minimumFrames": 0,
      "fallbackMs": 0,
      "maximumMs": 15000
    },
    "hold": {
      "minimumMs": 250,
      "minimumFrames": 15,
      "fallbackMs": 1250,
      "maximumMs": 5000
    },
    "neutral": {
      "minimumMs": 250,
      "minimumFrames": 1,
      "fallbackMs": 250,
      "maximumMs": 5000
    }
  }
}
```

A successful write atomically persists the document under the runner workspace and increments the revision. A stale writer receives `409 settings_revision_conflict`; read the current document and deliberately reapply the change rather than overwriting another operator's update. Invalid ranges receive `400 input_pacing_settings_invalid` and do not advance the revision.

Each interval requires nonnegative millisecond and frame minimums and a positive maximum. The maximum must cover the elapsed-time minimum and, when fallback is enabled, its fallback duration. Maximum values are ceilings rather than compulsory waits.

## Hybrid rule

When a future qualified external frame provider is available, each configured interval completes only after both minimums measured from the same fresh boundary are satisfied:

```text
elapsed_ms >= minimum_ms
AND
subsequent_qualifying_frames >= minimum_frames
```

For the default held-button example, DOWN would remain active for at least 250 milliseconds and at least 15 subsequent qualifying target-frame events, whichever takes longer. A known-unavailable provider may use the separately configured fallback. A source that is available but temporarily not advancing is a stall, not permission to switch silently to time-only behavior.

## Scope of this change

This settings/API/UI change establishes the durable control plane and truthful capability reporting required by issue #63. It does not add an external frame provider or claim guest input acknowledgement. Executor integration must freeze the effective profile into each requested procedure and comparison identity before hybrid pacing can be used for qualification.