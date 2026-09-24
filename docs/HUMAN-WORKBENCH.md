# Human workbench and short references

Open `/tests` on the runner. Upload application packages through the existing HTTP/Python upload workflow, then select a stored application and immutable test revision. **Save selection only** does not request execution. **Start** confirms execution intent and queues behind existing work. After a disconnected response, keep the original request ID and use **Inspect this request ID**; do not submit a replacement attempt to discover whether the first started.

The configuration catalog is paged. Search filters the loaded pages only; Load more expands the search. Inspect reads and exports the complete saved definition. Copy JobDefinition to editor copies only its editable job section. Save a new name to keep prior configurations and revisions available. Failed saves retain the name, source and editor text. The server, not the browser, validates nested fields and source readiness before saving. Uploaded files and source packages remain subject to the existing retention contract.

Build A/B selectors contain indexed executable identities. An empty A uses the explicitly saved baseline; it never means first or latest. Pin B requires confirmation and a qualified build. Reports and calculations come from the server. Full comparison CSV and original per-run CSV links are explicit secondary requests, not automatic raw-data downloads.

## API references

Full SHA-256 remains the durable identity. The build result, comparison and baseline endpoints, and the named-config inspection and requested-test endpoints, accept 12..64 hexadecimal prefixes when exactly one item exists in the relevant catalog. A resolved test request and baseline always store the complete digest. Invalid syntax is 400, unknown is 404, and ambiguous is 409. No first/latest match is selected. `/api/v1/builds?offset=0&limit=25` supplies canonical SHA-256 and a unique display reference, extended beyond 12 characters when required.

Legacy lower-level manifests still declare complete file hashes. Short references are selection handles, not weakened integrity hashes. The catalogs have explicit enumeration limits; an incomplete catalog is never treated as proof of uniqueness.

## Validation and recovery

JSON action requests require UTF-8 `application/json`, a bounded Content-Length, and a complete body within 15 seconds. New input parsing rejects unknown nested members, duplicate model keys, invalid required/null values and incompatible types before the mutation. Existing disk serialization and immutable-definition hashes are unchanged. Empty-body actions retain their established contract.

Errors preserve `error`, `code`, `hint`, `status`, `help`, and optional `details`, adding `ok:false`, a response-correlated `requestId`, and `recovery` (`correct`, `inspect`, or `wait`). Detailed filesystem failures are logged on the server rather than exposed as path dumps. JSON error bodies are bounded to 4 KiB. A streaming failure closes the response instead of appending JSON to artifact bytes.

Browser mutations must originate from the directly served runner origin. Script clients may omit Origin. This is not authentication or TLS; do not expose the privileged runner listener to an untrusted network. Forwarded headers are not trusted to authorize a browser origin. Reverse-proxy/TLS deployment needs a separately reviewed origin policy.

Controls use text-only server rendering, labels, keyboard focus and an accessible error panel. Pending actions are disabled. Superseded catalog, inspection and report reads cannot overwrite newer selections. Statuses are manual observations, not polling or correctness certification. A successful HTTP call does not by itself mean the guest passed.

## Verification scope

`tests/AgentChecks/HumanWorkflowChecks.cs` covers real-listener short references, ambiguity, durable identity, malformed input, HTTP error contracts and response bounds. `tests/test_human_workbench.py` exercises ten browser interactions against an offline HTTP-contract fixture: selection without start, start/pin confirmation, lost-start recovery, retained editor errors, out-of-order inspection, pagination/search/mobile/text safety, server comparisons, explicit raw links, pending-action exclusion and unsaved-input preservation. Both are CI gates.

These tests do not claim a live GPU run, production deployment, full accessibility certification or arbitrary reverse-proxy compatibility. The existing authorized executor, benchmark policy and baseline qualification rules remain authoritative.
