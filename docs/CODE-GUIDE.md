# Code guide

Start with the [README workflow](../README.md). This guide shows where that workflow lives in the code; it does not define a second way to run tests.

## Follow one request

```text
Agent/build machine                        Tester
runner_tests.py
  parse command
  check capability
  upload / select -----------------------> store application and test request
  start ---------------------------------> record execution intent
                                           wait for current work
                                           prepare private inputs
                                           validate and queue the package
                                           RunnerEngine launches xemu
  wait ----------------------------------> observe that same request
                                           collect guest/crash/state evidence
                                           clean transient disks and archive
  result / compare ----------------------> read server-computed results
```

Only the explicit start/submit paths authorize execution. An HTTP reconnect, configuration upload or result read does not.

## Client code

| File | Responsibility |
| --- | --- |
| [runner_tests.py](../scripts/runner_tests.py) | Argument help and routing. `_execute_test_command` handles configs and selections; `_execute_disk_command` handles catalog operations. |
| [runner_transport.py](../scripts/runner_transport.py) | HTTP, structured errors, hashing declarations and resumable transfers. No test scheduling or result arithmetic. |
| [runner_wait.py](../scripts/runner_wait.py) | Repeated read-only waits, transient connection recovery, reply validation and optional heartbeat output. |
| [runner_test_results.py](../scripts/runner_test_results.py) | Request server reports or explicitly collect CSV/diagnostic ZIP artifacts. |
| [runner_api.py](../scripts/runner_api.py) | Lower-level package/control operations. Its submit/run commands intentionally authorize execution. |

Within `runner_tests.py`, read `parser()` and `execute()` first. Then follow the named handler for the command being changed. `catalog()` lists configurations, `selection()` resolves exact revisions, `upload_application()` stores bytes, and `select_tests()` creates request IDs before optional start calls. They are separate because saving inputs must not accidentally become execution permission.

Within `runner_wait.py`, `execute()` owns the observation loop. `_validate_reply()` prevents another ID or malformed response being accepted as completion. `_pause_before_retry()` limits connection retry frequency, not the duration of the test. There is no overall wait deadline or agent-facing timer.

## Server code

| Area | Start here |
| --- | --- |
| HTTP routing | [EmbeddedHttpServer.cs](../src/XemuTestRunner/Networking/EmbeddedHttpServer.cs) and [agent extensions](../src/XemuTestRunner/Networking/EmbeddedHttpServer.AgentExtensions.cs). |
| Requested tests | [AgentJobStore.RequestedTests.cs](../src/XemuTestRunner/Networking/AgentJobStore.RequestedTests.cs) stores start intent and materializes packages; [requested-test routes](../src/XemuTestRunner/Networking/EmbeddedHttpServer.RequestedTests.cs) expose it. |
| Queue and execution | [JobQueue.cs](../src/XemuTestRunner/Queue/JobQueue.cs) owns package transitions. [RunnerEngine.cs](../src/XemuTestRunner/Runtime/RunnerEngine.cs) owns process lifetime and finalization. |
| Completion observation | [EmbeddedHttpServer.CompletionWait.cs](../src/XemuTestRunner/Networking/EmbeddedHttpServer.CompletionWait.cs). |
| Runtime disks and caches | [RuntimeStateManager.cs](../src/XemuTestRunner/Runtime/RuntimeStateManager.cs) materializes private inputs; [RunStorageSession.cs](../src/XemuTestRunner/Runtime/RunStorageSession.cs) manages the effective configuration and cache evidence. |
| Evidence and comparison | [GuestHddResults.cs](../src/XemuTestRunner/Runtime/GuestHddResults.cs), [DiagnosticArchive.cs](../src/XemuTestRunner/Diagnostics/Crashes/DiagnosticArchive.cs), and [BuildResultStore.cs](../src/XemuTestRunner/Networking/BuildResultStore.cs). |

### Execution and finalization

In `RunnerEngine`, `RunAsync` owns the workspace and queue loop. `ExecuteJobAsync` freezes the job configuration, performs preflight, prepares inputs, launches xemu and waits for exit, plan failure, cancellation or watchdog action. Finalization then collects evidence, records the assessment, handles transient state and archives the package.

The distinction between an observation and ownership is important. A stale process-handle read must not undo a previously confirmed native exit. `ConfirmTargetExit` preserves that fact. `ShouldCaptureFailure` applies one rule to automatic failure diagnostics and the fallback screenshot: completed runs are not eligible. Do not bypass those helpers when adding diagnostics.

A held target is not archived completion. Cleanup may proceed only when process ownership and evidence state permit it. A failed diagnostic collector does not change a confirmed crash into a passing test.

### Completion-wait code

Read the wait endpoint in this order:

1. `TryCompletionWaitRouteAsync`: recognize the URL, reject timer parameters, then write the response.
2. `ReadCompletionState`: resolve the requested selection or materialized job's authoritative state.
3. `WaitForCompletionOrHeartbeatAsync`: hold a bounded transport read using the shared waiter limit.
4. `ReadCompletionAssessment`: read the canonical result only after archival; missing evidence stays explicit.

The server's heartbeat releases one HTTP response, not the logical wait. The Python helper follows the same ID indefinitely. Wire events stay `heartbeat`, `finished` and `attention`; terminal state does not mean guest correctness.

## Validate a change

Run the checks relevant to the edited layer; CI also runs the existing platform suites:

```sh
python -m unittest discover -s tests -p 'test_runner_api*.py' -v
dotnet run --project tests/RunnerChecks -c Release
dotnet run --project tests/AgentChecks -c Release
dotnet run --project tests/AgentChecks -c Release -- --client
dotnet run --project tests/FinalizationChecks -c Release
```

Python tests record actual client HTTP calls; AgentChecks exercises the embedded listener. FinalizationChecks covers real child-process shutdown and capture invocation, not actual desktop photographs. Add assertions for outputs and side effects, not private method layout.

`test_runner_api_guidance.py` checks README command syntax against the real parser, required client modules, local guide links and the explicit-start/default-wait instructions. This keeps the short starting guide from falling behind the code again.
