# Completion waiting during queue handoff

The runner's package-stability check is an admission phase. It emits `package_stabilizing` while a new Pending package settles; that is not failed work and does not require another Start. A completion wait now keeps following that request through this phase.

Only `package_stabilizing` with `Retryable=true` and `HoldsTesting=false` is treated as expected progress. Missing inputs, access denial, `package_busy`, corrupt claims and any Testing hold still produce attention immediately. A real queue-head problem can legitimately block a later request; its original package and code are included in the wait response's `queueIssue` object. The request ID always remains the requested target.

Beginning a newly owned job atomically clears the earlier queue issue. Observers of a running target do not inherit a predecessor's issue. No filesystem scan, target probe, diagnostic capture, caller timer or automatic retry/start was added to polling.

`runner_tests.py wait ID` still has no overall deadline. Transport heartbeats repeat automatically. `--updates` only changes output visibility, never timing or execution. Read `/api/v1/status` for the full issue message; preserve the response's cause, package and detection time when reporting a real blocker.

Regression tests exercise the real JobQueue stabilization producer while a wait spans two requested attempts, pre-materialization waiting, immediate actionable failures, and a Testing hold that must not be masked by a progress-like name. No live machine deployment is part of those fixtures.
