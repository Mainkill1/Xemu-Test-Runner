# Error handling and recovery

Xemu Test Runner errors are intended to be actionable for both humans and automation.

## API error shape

All converted API error paths use:

```json
{
  "error": "Human-readable explanation.",
  "code": "stable_machine_code",
  "hint": "Concrete corrective action.",
  "status": 400,
  "help": "/api/v1/help",
  "details": {}
}
```

`error` remains a string for compatibility with older clients. New automation should branch on `code`, not parse prose.

## HTTP categories

| Code | Typical status | Meaning / action |
| --- | ---: | --- |
| `http_request_invalid` | 400 | Request line/header syntax is invalid. Correct HTTP syntax. |
| `request_body_invalid` | 400 | JSON/body fields are malformed or outside accepted ranges. Use the example in the hint/help route. |
| `body_not_allowed` | 400 | GET/HEAD contained a body. Remove it. |
| `route_not_found` | 404 | Path/method combination is unsupported. Check `/api/v1/help`. |
| `method_not_allowed` | 405 | Known resource, wrong HTTP method. |
| `target_not_active` | 409 | Operation requires an active xemu target. Start/attach one first. |
| `target_not_ready` | 409 | Target exists but is paused/not ready. Correct the state first. |
| `operation_blocked` | 409 | Active operation policy blocks an intrusive action. Do not disturb the benchmark, or explicitly change policy. |
| `preview_disabled` | 404 | Preview is disabled by configuration. |
| `preview_unavailable` / `screenshot_unavailable` | 503 | Capture provider/control path failed. Inspect control/provider state. |
| `diagnostic_not_found` | 404 | Unknown configured diagnostic recipe ID. List recipes first. |
| `diagnostic_request_invalid` | 400 | Diagnostic recipe/body is invalid. Correct fields. |
| `diagnostic_state_conflict` | 409 | Another diagnostic/target state conflicts. Wait/stop it. |
| `diagnostic_tool_failed` | 503 | External diagnostic provider/tool failed. Check `tools` and run evidence. |
| `file_path_invalid` | 400 | File path escapes/violates configured file root. Use a relative path. |
| `file_not_found` | 404 | Requested file does not exist. |
| `content_length_required` | 411 | Upload must provide Content-Length. |
| `content_range_invalid` | 400 | Resume range syntax/length is invalid. Query upload status and retry sequentially. |
| `upload_offset_mismatch` | 409 | Resume started at the wrong offset. Use returned expectedOffset. |
| `upload_in_progress` | 409 | Another upload owns the destination. Wait or use another path. |
| `upload_hash_mismatch` | 422 | Final SHA-256 differs from X-Content-SHA256. Re-upload correct bytes. |
| `evidence_not_found` | 404 | Run/artifact does not exist. List runs/artifacts first. |
| `evidence_request_invalid` | 400 | Run/log/artifact path is invalid or unsupported. |

## Queue/package errors

`QueueIssue` includes `Code`, `Message`, and computed `Hint`.

Important codes:

- `package_stabilizing`: wait for file activity to stop.
- `package_incomplete`: complete the package; prefer `.incoming-*` staging.
- `package_busy`: another process is copying/scanning/holding files.
- `package_access_denied`: fix permissions.
- `package_invalid`: run `xemu-test-runner validate <package>`.
- `package_changed_during_claim` / `package_changed_during_preflight`: package changed after visibility/ownership; leave the held Testing package intact and stage a fresh package.
- `testing_occupied`: another attempt owns Testing.
- `preserved_target`: inspect the target and use `POST /api/v1/xemu/quit` when finished.
- `package_wait_timeout`: finite automation stopped waiting for a retryable incomplete package.

Do not move/delete a Testing package merely to clear an error until the associated process/attempt state has been inspected.

## CLI behavior

Known user/config/package errors should be concise:

```text
request_invalid: Package validation failed: ...
Hint: Correct the reported field/path/value, then retry.
```

Machine-oriented commands use JSON error fields where supported.

Unexpected `internal_error` cases retain exception detail because they represent a runner defect rather than ordinary invalid input.

## Design rule for new endpoints

When adding an endpoint:

1. Choose a stable lower-snake-case error code.
2. Explain exactly what failed in `error`.
3. Put the recovery instruction in `hint`.
4. Use an appropriate HTTP status.
5. Put machine context in `details`, not inside prose when possible.
6. Never require clients to parse exception text to decide what to do.
7. Add the route/error to `/api/v1/help` when it is externally callable.
