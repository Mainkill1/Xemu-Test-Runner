# Shared firmware/DVD inputs with managed state

Issue #40 exposed a real boundary: managed state requires package-local immutable inputs, but deployed tests use shared firmware/DVD paths. Removing Isolation avoids that path check only by making the benchmark ineligible. This change provides an explicit catalog reference instead.

Register firmware or a DVD image using the existing catalog API with `kind: "readonly-input"`. Upload once through `/api/v1/disk-assets/{id}/content`, or import a stable retained package through the existing local import operation. The catalog's internal `disk.qcow2` filename is opaque storage; bytes are not converted and may be ROM/ISO content.

In the saved configuration:

```json
{
  "RuntimeState": {
    "Enabled": true,
    "Isolation": {
      "CacheMode": "cold",
      "ReadOnlyAssets": [
        {"Field":"bootrom_path","AssetId":"boot-rom-v1","ExpectedSha256":"FULL_SHA256"},
        {"Field":"flashrom_path","AssetId":"flash-rom-v1","ExpectedSha256":"FULL_SHA256"},
        {"Field":"dvd_path","AssetId":"test-disc-v1","ExpectedSha256":"FULL_SHA256"}
      ]
    }
  }
}
```

Merge the section into the existing private HDD/EEPROM/cache policy; this is not a full runnable test. Only bootrom_path, flashrom_path and dvd_path may use shared references. Saved tests contain no host filesystem path. The runner resolves the catalog ID, verifies its declared length and full SHA-256, then writes the resolved path into its one effective TOML. The semantic configuration hashes shared content rather than installation-specific paths. The original authored TOML remains evidence.

Shared files are not copied into the runtime package. The state ledger records selected field/ID/path/length plus before/after hashes. Verification happens during preparation and after confirmed target exit, never during timed sampling. Changed, missing, inaccessible or incompletely checked content invalidates state qualification. This is not a filesystem sandbox: a rogue program with OS permissions may write a file, and post-exit verification detects changed bytes rather than preventing every possible mutation.

Existing state-evidence deadlines still bound hashing. Large DVD images may require the existing `DeadlineSeconds` policy to be sized appropriately (maximum 60 seconds). No successful verification is claimed when that bound is exceeded. No global image conversion, reflink, extra copy, cache purge or network service is introduced.

This does not choose a baseline, waive driver-cache control, weaken screenshot assertions, rewrite old definitions or mark a historical unmanaged run eligible. Create a new pinned test revision and explicitly request it. The default partial-control acceptance remains unchanged; tests must honestly record any uncontrolled driver/OS cache factors.
