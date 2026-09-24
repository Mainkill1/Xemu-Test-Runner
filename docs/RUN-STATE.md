# Run-to-run storage and cache state

A result is not just an executable and a test plan. Shader binaries, pipeline caches, learned prewarm history, config saves, EEPROM, HDD/savegame data and host caches can change the next attempt. This feature records selected state and isolates supported xemu application paths; it does not certify that an entire computer is cold.

## Known xemu paths

For the managed portable profile, xemu.toml must exist beside the executable. This anchors xemu's settings base to the executable directory even when the runner passes a separate -config_path. A config override by itself does not change that base.

| Stored state | Location relative to executable base |
|---|---|
| OpenGL shader binaries | shaders/ |
| OpenGL reload/LRU list | shader_cache_list |
| Vulkan SPIR-V cache and learned fallback/prewarm history | cache/vulkan/ |
| Other current application cache files | cache/ |

The application inventory covers all three top-level entries: cache, shaders and shader_cache_list. It does not hash the executable, every game image or arbitrary user profile directories. The profile is based on the current xemu layout; a future executable that writes elsewhere is not automatically contained.

## Explicit policy in a saved test configuration

Add Isolation under the existing RuntimeState section. It is optional for compatibility; when omitted it serializes exactly as before, so old immutable test-definition identities do not change.

```json
{
  "RuntimeState": {
    "Enabled": true,
    "KeepOnFailure": true,
    "Files": [
      {"Source":"seeds/hdd.qcow2","Destination":"hdd.qcow2"},
      {"Source":"seeds/eeprom.bin","Destination":"eeprom.bin"}
    ],
    "Isolation": {
      "CacheMode":"cold",
      "CacheShaders":true,
      "DriverCache":"uncontrolled",
      "AllowUncontrolledDriverCache":false,
      "RequirePrivateGuestState":true
    }
  }
}
```

This is a fragment of an existing test definition. Pin the source file hashes through the existing manifest/runtime seed contract. In the package's xemu TOML, explicitly reference the copies:

```toml
[sys.files]
hdd_path = '{runtimeDir}/hdd.qcow2'
eeprom_path = '{runtimeDir}/eeprom.bin'
```

Read-only boot/flash/DVD inputs remain package-local. Managed private guest state refuses writable images outside the declared runtime materialization. Explicit QEMU drive/readconfig overrides are refused because they could bypass this mapping. A fixture with no guest can disable RequirePrivateGuestState, but the unresolved guest defaults then remain unqualified rather than being declared isolated.

## Cold, seeded and inherited mean different experiments

- **cold:** move any existing package-local cache entries into this run's state/prior-cache/ area, then create empty application cache directories. No host/global cache is deleted. CacheShaders=true permits normal cache creation inside the attempt; cold is not the same as disabling caching.
- **seeded:** require a package-relative SeedDirectory and complete SeedSha256 tree identity, verify the bounded tree, then copy it into separate writable application cache storage. Seed content may contain only cache/, shaders/ and shader_cache_list. The immutable seed is not shared writable storage. A seeded cache is not proof the executable/driver accepted every entry or that a workload is fully warm.
- **inherited:** preserve and inventory whatever the package contains. This is observational state and is not qualified as a controlled comparison baseline.

No extra warm-up process or test is automatically started. For steady-state measurement, declare a consistent warm-up and measurement segment in the already-requested plan. Do not seed B from A's cache unless that cross-build compatibility experiment is intentional and distinctly configured.

## Configuration snapshots

The runner parses TOML with Tomlyn, writes perf.cache_shaders explicitly from the policy, expands supported storage placeholders, and passes a separate writable effective config. The original config is not overwritten when xemu saves settings. Diagnostics retain original, effective-at-launch and final config files plus their SHA-256 identities.

The file is **not** a complete dump of every compiled-in default. Omitted settings still depend on the executable; author renderer, resolution, synchronization, tweaks and other performance-relevant values explicitly in the saved test. There is no xemu effective-settings introspection handshake in this patch.

The state contract hashes semantic authored configuration, the selected policy, actual initial application-cache contents and runtime seed identities. Run-specific absolute paths are not used as the state identity. Full path/seed evidence remains in the ledger.

## Driver and OS boundaries

DriverCache supports uncontrolled, mesa and nvidia-gl. The latter two currently configure Linux-only, fresh per-run disk-cache namespaces. NVIDIA's adapter is specifically for OpenGL; it is not a Windows or Vulkan driver-control guarantee. Inherited cache override variables are recorded and replaced for the selected adapter.

The report deliberately retains driver-cache as uncontrolled and DriverNamespaceVerified=false: an environment setting alone does not prove which driver was loaded or that it honored the setting. With the default AllowUncontrolledDriverCache=false, that uncertainty blocks managed comparison qualification. Setting it true is an explicit acceptance of a **partially controlled** experiment, recorded in the comparison key; it never changes the reported uncertainty into verified isolation.

OS page cache remains explicitly uncontrolled on all platforms. No privileged memory-cache flush, driver reset, registry edit or global cache deletion occurs. Windows vendor caches, driver-internal RAM caches, arbitrary application writes and inherited defaults not covered by this profile need separate native validation. A/B ordering and identical warm-up procedures still matter.

## Where evidence is retained

`Results/RUN_ID/diagnostics/run-state/` contains report.json, config-before.toml, config-effective.toml, effective-live.toml and config-after.toml. The existing diagnostic ZIP collects these bounded metadata/config artifacts and is mirrored beside the finished executable by the crash-report feature.

The ledger gives before/after file counts, byte counts, relative filenames, file SHA-256 values and combined tree identities. It includes observed config changes, actual launch-time paths, runtime seed identities and unresolved controls. Empty directories have no content contribution to the tree digest. Limits/errors produce incomplete state, not a false empty cache.

Raw cache bytes are not automatically packed into the diagnostic ZIP. The prior cache is quarantined under Results/RUN_ID/state/prior-cache; final application cache files remain with the package as it moves from Testing to Tested. Captured absolute paths describe the execution location; package-relative entries identify the same files in the archived package. Large private HDD/EEPROM images remain under the existing RuntimeState retention policy rather than being duplicated in every diagnostic archive.

Directory quarantine uses a same-filesystem rename. Separate-volume package/results layouts must be qualified; a failed rename produces preparation failure before launch instead of deleting the old cache. A failed final inventory is retained as incomplete state and does not rewrite a native crash as success.

## API-first inspection

```sh
python scripts/runner_tests.py state RUN_ID
python scripts/runner_tests.py diagnostics RUN_ID
python scripts/runner_tests.py diagnostics RUN_ID --out run-diagnostics.zip
```

The state command only calls `GET /api/v1/runs/RUN_ID/state`. It returns policies, paths, counts, hashes, qualification and detail links, not a full cache-file listing. The larger manifest is an explicit artifact request and is also included in the diagnostic ZIP. Focused help: `/api/v1/help?topic=run-state`.

State summaries are cached by report file version. The first read projects bounded local metadata; it never enumerates or hashes the live cache. This is not a new polling/telemetry loop. Ordinary status remains the usual progress endpoint.

## Comparison and compatibility

Managed runs require an actual valid final report, not just a requested isolation setting. Inherited, missing or incomplete proof is ineligible. New benchmark runs without a state policy receive a comparison blocker without changing guest correctness or blocking execution. App-cache/seed policy and actual initial contents are part of the new comparison key, so incompatible states do not silently share a median.

Historical stored results remain historical: they are not rewritten, rehashed or certified cold. A missing report is shown as legacy/state_not_recorded. Old nonmanaged comparisons retain their existing identity, while new managed profiles form separate cohorts. Rerun both reference and candidate under the same explicit state profile before creating a new qualified baseline.

## Checks and remaining gaps

StateChecks exercises actual target launch/config delivery and post-exit recording in addition to cold/seeded/inherited policies, OpenGL/Vulkan paths, source immutability, private guest checks, tree bounds and symlinks. AgentChecks covers compact HTTP inspection, missing/malformed proof, benchmark gating and explicit partial-control acceptance. Crash qualification is rerun to ensure state recording does not stop continuation after a native crash.

Native xemu/driver acceptance, effective compiled-default reporting, a cache-snapshot promotion/export API and complete driver-cache qualification remain outside this patch. Agents should not be told these capabilities exist merely because the state ledger records the corresponding limitation.
