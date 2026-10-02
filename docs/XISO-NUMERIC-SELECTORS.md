# Compact XISO test selectors

XISO campaigns can select many tests without copying their full stable names into every request. Numeric selectors are an operator convenience layered over the existing stable test IDs and categories; the runner resolves them to stable leaves before it freezes a campaign.

## Discover the mapping

Numeric IDs belong to one immutable registered suite and its pinned catalog. Read the mapping from that suite before building a request:

```bash
python scripts/runner_xiso.py --pretty categories SUITE
python scripts/runner_xiso.py --pretty tests SUITE --limit 100
python scripts/runner_xiso.py --pretty tests SUITE --offset 100 --limit 100
```

Both responses include `catalogId`, `catalogSha256`, `selectorVersion`, and `idBase`. Test results are always assigned their global catalog `test_id` before filtering or pagination, so `--category shaders` does not renumber the shader subset.

Do not reuse a numeric list with a different suite or catalog. Stable string IDs remain the portable selector when a request must survive catalog changes.

## Test-group IDs

`test_groups` indexes the runner's stable subsystem table:

| Group ID | Category | Purpose |
| ---: | --- | --- |
| 0 | `cpu` | CPU execution and translation |
| 1 | `commands` | Command processing and queries |
| 2 | `shaders` | Shaders and pipelines |
| 3 | `textures` | Textures and formats |
| 4 | `geometry` | Geometry and draw submission |
| 5 | `surfaces` | Surfaces and memory |
| 6 | `scenarios` | Composite scenarios |
| 7 | `other` | Newly added or unmapped suites |

The categories endpoint returns all eight entries with `count` and `available`. Selecting a group whose count is zero is rejected instead of silently producing an empty campaign.

## CLI examples

Run tests 0, 3, 29, and 41:

```bash
python scripts/runner_xiso.py select APPLICATION \
  --id focused-tests \
  --suite SUITE \
  --test-ids 0,3,29,41
```

Run the CPU and shader groups:

```bash
python scripts/runner_xiso.py select APPLICATION \
  --id cpu-and-shaders \
  --suite SUITE \
  --test-groups 0,2
```

The options may be repeated and are flattened into one array:

```bash
python scripts/runner_xiso.py select APPLICATION \
  --id mixed-selection \
  --suite SUITE \
  --test-ids 0,3,29 \
  --test-ids 41,57 \
  --test-groups 2 \
  --test shader_lifecycle.pipeline_train
```

Named categories, stable test IDs, numeric test IDs, and numeric groups are unioned. Duplicate selectors do not execute a leaf twice. Selection remains upload-only unless `--start` is explicitly supplied.

## HTTP request

The compact JSON fields are arrays of zero-based integers:

```json
{
  "id": "focused-tests",
  "application": "candidate-build",
  "suite": "shader-pilot",
  "test_ids": [0, 3, 29, 41],
  "test_groups": [2]
}
```

The runner resolves the arrays using the selected suite, stores the stable names in the frozen campaign plan, and then uses the normal XISO selection path. Existing dependency closure still applies. For example, choosing one inseparable Vulkan memory-pressure checkpoint also adds the other checkpoints and reports them in `addedDependencies`.

Bounds are deliberately strict: test IDs are `0..511`, group IDs are `0..7`, and the server verifies that each ID actually exists in the pinned catalog. A changed selection needs a new campaign ID; an identical normalized selection remains idempotent.
