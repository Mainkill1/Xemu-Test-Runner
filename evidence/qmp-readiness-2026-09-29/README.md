# QMP startup timeout regression

GBTG debug runs on Windows failed control initialization with `QMP query-status
exceeded 1500 ms`, despite a longer configured readiness deadline. The per-attempt
timeout escaped `WaitUntilReadyAsync` rather than entering its existing retry loop.

The correction retries `TimeoutException` through that loop. It does not change
the configured deadline, QMP transport, game inputs, or screenshot behavior.

## Verification

- RED: a real TCP endpoint accepts its first connection without greeting it;
  the next connection supports capabilities and status. Existing code aborts
  at 1500 ms instead of reaching the ready endpoint (`red.log`).
- GREEN: 38 RunnerChecks pass, including timeout recovery and a subsequent
  status query, overall readiness exhaustion, and caller cancellation
  (`green.log`). The cancellation fixture has a bounded accept.
- Operation 9/9, DiskAsset 5/5, Agent HTTP 128/128, State 14/14,
  Finalization 6/6, XISO 14/14, Crash 9/9, Python clients 66/66,
  and embedded-listener Python integration 3/3 pass locally.
- The first Python fixture invocation inherited SSH session environment
  variables and reported 46 failures and 2 errors through its loopback guard.
  Local fixtures were rerun with those variables removed. Actual remote
  Windows operations continue through direct LAN HTTP.
- Independent code review found no critical or important findings; its
  unbounded test-accept observation was addressed before the final run.

This is a tester readiness repair, not a GBTG graphics correction. Native
diagnostic execution remains a separate qualification step after deployment.
