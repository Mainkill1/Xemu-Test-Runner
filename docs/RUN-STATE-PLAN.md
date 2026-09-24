# Run-state isolation and provenance

## User requirement

Default shader caching and other retained state can favor a later attempt. Record what is stored, where it lives, which starting state was selected, and whether A/B comparison actually controls it. Preserve diagnostics with finished executables, never auto-start another test, and do not make a missing diagnostic provider hold a terminated attempt.

## Scope

Extend the existing RuntimeState contract with optional Isolation. Managed xemu runs use a package-local portable xemu.toml marker, an independently copied effective config, a cold/seeded/inherited application cache policy, and explicit driver-cache policy. CacheShaders is written explicitly into the effective TOML, so an omitted executable default cannot choose this factor silently. Use Tomlyn rather than regex to parse TOML. Existing optional-null serialization must leave saved definitions unchanged.

Before launch, save input/effective config hashes and paths, runtime seed identities, selected cache policies and actual initial cache tree identity. On termination, save final config and cache inventories. Keep state evidence under diagnostics/run-state so the existing diagnostic ZIP contains it without sweeping arbitrary host directories or multi-gigabyte runtime images. All heavy scans are bounded and occur outside measured execution. Raw OS file/page cache and unsupported driver state remain explicitly uncontrolled; do not call an empty application cache a cold machine.

Managed cold runs preserve any previous package-local cache in a run-specific quarantine instead of deleting a user's global cache. Seeded runs copy a bounded hash-pinned tree into a separate mutable cache; they never reuse another attempt's writable directory. Inherited runs are observational, not qualified cold/warm baselines. A warm-up is part of the already-requested plan, not an automatically launched extra test.

## Implementation order

1. Add tests for cold reset with retained prior data, config-copy immutability, explicit cache setting, seeded-copy verification, mode identity, inherited/uncontrolled qualification, path/link refusal, bounded inventories, optional-schema compatibility, and final state capture. Run tests-first CI.
2. Add RuntimeState.Isolation models plus focused inventory, TOML and session units. Attach the session to TargetLaunch and finalize only after the target is confirmed exited. Preparation never starts a process. Missing/failed inventory records remain visible.
3. Add state checks to workload/assessment qualification and build indexing without changing existing record schemas. Missing managed evidence and inherited/uncontrolled states do not produce qualified A/B claims. Existing unmanaged runs remain identified as legacy; no retroactive cold-state certification.
4. Expose a compact state API and a thin Python inspection command; include metadata in the existing diagnostic ZIP. Document cache/guest/OS boundaries and re-run Windows/Linux core, crash-continuation and state checks.

## Review focus

A fresh config path is not necessarily the xemu cache base; portable mode is determined by xemu.toml next to the executable. Driver cache controls are provider/platform specific. Mutable HDD/EEPROM paths must point to the actual private runtime materialization rather than inherited external paths. Config path expansion must preserve original input meaning. A failed post-run inventory cannot change crashed into passed or leave the next requested test waiting indefinitely. No global cache deletion, kernel cache flushing, or privileges are implied.
