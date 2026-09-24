# Agent client

Run scripts/runner_api.py with its adjacent runner_transport.py and runner_workflows.py on the build/agent machine. Python 3.10+ standard library only. The client calls HTTP and never executes commands on the tester.

## Commands

| Command | Normal behavior |
| --- | --- |
| discover | Small deployed capability/version entry view; --detail opts into the route manual. |
| tests / jobs | Paged compact inventory; --offset and --limit select a page. |
| status ID | Small lifecycle observation; --detail opts into the full job. |
| run TEST --revision PIN --id NEW [--build DIR] | Materialize a pre-baked test, reuse unchanged payload, upload changed build slots and submit. |
| submit DIR --id NEW [--reuse SOURCE] | Initial/custom package with job.json; optional server-local reuse from another retained package. |
| submit-draft ID | Validate and submit an existing draft. |
| bake TEST --from-job SOURCE | Freeze a reusable definition; --build-file declares each replaceable build slot. |
| retry SOURCE --id NEW | Clone and submit an intentional new attempt without another payload upload. |
| clone SOURCE NEW | Create an editable clone without submitting it. |
| wait ID --max-wait 30 | Bounded lifecycle wait; returns a blocker, terminal assessment or current state. |
| result ID | Read the canonical compact assessment, not raw files. --section failures narrows displayed details. |
| logs ID | Bounded stderr by default; --stream, --max-bytes and --cursor support incremental inspection. |
| collect ID DIR | Collect assessment.json by default; --only selects artifacts, --all explicitly follows the complete eligible inventory. |
| edit ID | Replace a draft plan with --json, --json-file, --stdin or a positional plan file; uses If-Match. |
| withdraw / cancel ID | Existing unclaimed-job actions; never silently stop an active target. |
| request METHOD PATH | Explicit raw HTTP JSON operation; --json/--json-file/--stdin supply its body. |

## Output and gates

Set XEMU_RUNNER_URL or use global --url. Normal stdout is one compact JSON document, including structured errors. Progress is disabled; global --progress enables per-file messages on stderr. Global --pretty changes only formatting. Global options precede the command. Explicit detail/raw operations may return larger data.

No gate: exit 0 means the requested client operation succeeded, not guest correctness. With --require correctness, only completed execution plus correctness=passed succeeds. With --require eligible, completed execution plus comparison=eligible succeeds. Failed requirements exit 2; unavailable/not-evaluated outcomes exit 3. Client/API errors exit 1; interrupted client exits 130 without claiming server cancellation. Server error code/status/hint are preserved rather than embedded as escaped JSON text.

submit/run/retry/submit-draft accept --wait and --max-wait. The test wait window defaults to 30 seconds. A bounded expiry returns current state and the same ID. The separate global --wait-seconds bounds payload-preparation/validation operation polling (default 1800 seconds). --timeout is the per-request socket timeout, not a whole-file transfer deadline. Held/blocked ownership exits the wait loop immediately.

## Pre-baked builds

Without --build, run uses the retained definition's original payload. With --build, every declared buildFiles slot must exist under that local directory; the helper hashes only those files, not a job plan or workload tree. Matching contents are copied on the tester and require no upload. Different digests remain missing and are uploaded in bounded resumable chunks. Normal submit always verifies the materialized package.

Unknown or missing deployed capabilities produce capability_missing. Redirects are rejected rather than allowing a returned URL to change origin or silently change a POST into a GET. There is no SSH fallback.

## Recovery and collection

After a disconnected creation/submission, rerun the identical command with the same ID and unchanged local inputs. Resumable uploads query the committed server offset and never assume the last attempted chunk was accepted. Publication-unconfirmed state is reported explicitly. Failed operations retain their server hint; the client does not manufacture another attempt.

Full collection fetches the complete paged metadata inventory first. An incomplete/expired/repeating inventory fails before a full-collection success can be reported. Every file uses the existing range endpoint and a .part file; byte counts are checked before final publication. Existing same-length files are reused, not independently digest-verified: the result API does not yet provide a digest manifest. Hidden/link exclusions remain outside the eligible inventory and are reported in its receipt. Exact raw log bytes remain an explicit artifact request; decoded log slices are for inspection.

The Python helper reads ordinary JSON, not the comments/trailing commas permitted by the C# configuration parser. Hidden payload files/directories are omitted and symlinks are rejected. Packages must contain every required input. Draft editing replaces the complete JobDefinition; it is not JSON Patch and never edits a live attempt.

## Validation

python -m unittest discover -s tests -p test_runner_api.py -v runs subprocess client commands against a recorded HTTP fixture on both Windows and Linux CI. The C# AgentChecks suite separately tests the real embedded listener. These contract checks do not substitute for an API-only real-xemu run on each test rig.
