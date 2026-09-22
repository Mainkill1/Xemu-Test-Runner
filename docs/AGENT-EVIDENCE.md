# Focused evidence inspection

Start with `GET /api/v1/runs/{runId}?view=summary`. Only request logs or artifacts when the assessment requires inspection. Focused help is at `/api/v1/help?topic=evidence`.

## Paged artifacts

`GET /api/v1/runs/{runId}/artifacts?limit=25` creates a bounded metadata snapshot. Follow `nextCursor` on the same route until it is null. Maximum page size is 100. Returned items contain path, byte count and the existing ranged-download URL, not file content.

Unlike the legacy first-1,000 listing, this route can enumerate up to 10,000 eligible files. It retains at most four snapshots, each for ten minutes, with a 50,000-entry traversal bound, depth 16 and a bounded filename-character budget. Cursor expiry, server restart or eviction returns `artifact_cursor_invalid`; restart the listing and never interpret the interrupted result as complete.

`complete` describes whether the metadata inventory covered its advertised scope. `nextCursor` separately indicates unreturned pages. Depth/size/entry limits, access errors and disappearing entries return `complete:false` with issue codes. Hidden entries and symlinks/junctions are intentionally outside the scope and counted in `excluded`. Linked paths are never followed. No filesystem snapshot or concurrent-writer freeze is claimed: collect archived attempts, and still validate every actual download length.

Collection must check every page, its complete flag, and every download. Downloading everything listed on one page is not an all-evidence success. The raw streaming/range endpoints are unchanged; there is still no independent content digest manifest for result artifacts.

## Incremental bounded logs

`GET /api/v1/runs/{runId}/log?file=stderr.log&bytes=4096` returns a bounded tail plus `nextCursor`. Feed that cursor into the next call to read subsequent bytes without repeating the previous tail. Default is 4 KiB; maximum is 16 KiB per response. `more` indicates additional bytes at the sampled length.

Supported logs are stdout.log, stderr.log, operator-events.jsonl and segments.jsonl. Cursors are bound to run/file and carry a 64-bit offset plus a SHA-256 anchor of up to 64 bytes immediately before that offset. Detected truncation or changed anchor returns `reset:true` and starts from offset zero. This detects ordinary truncate/replace cases; it is not a native filesystem-identity guarantee against arbitrary rewrites preserving that anchor. Concurrent truncation during an anchor read fails explicitly.

UTF-8 replacement decoding at byte boundaries is intentional. Offsets/lengths are byte counts; returned text is for inspection, not a byte-exact archival copy. Fetch the raw log artifact for exact evidence when needed.

Both routes honor the current bulk-transfer policy and record activity. They are not the fast benchmark status path. Small cached assessment/job summaries remain the routine polling interface.

## Verification

The HTTP fixture suite creates 1,007 files, follows all pages without duplicates, rejects foreign/expired cursors, verifies explicit depth exclusions, follows appended log bytes, detects truncation and enforces path/policy constraints. Run `dotnet run --project tests/AgentChecks -c Release`. This is not large-network throughput, native xemu or live-rig qualification.
