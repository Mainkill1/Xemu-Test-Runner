# Local snapshot carrier import — source checkpoint

Addresses the shader-workbench test blocker: an existing tester-side QCOW2 HDD
can be imported over HTTP without manually packaging it or changing its source.

Implemented: tracked asynchronous acquisition/status/cancellation, approved local
roots, retained SHA-256 verification, immutable import definitions, shutdown
ownership draining, restart reconciliation against the exact published identity,
and bounded QCOW2 snapshot-name/VM-state-presence enumeration. Matching Python
commands never start xemu. See docs/DISK-ASSETS.md.

Verification on Linux with .NET SDK10.0.401:

- Missing-route reproduction: new3 cases fail404; original120 checks pass.
- Final embedded HTTP suite:128/128 pass, including cancellation, external root,
  shutdown, persisted ID protection and exact-publication restart recovery.
- Operation checks9/9, Runner checks35/35, Disk asset lifecycle5/5,
  actual-listener Python integration3/3 pass.
- Python client contracts66/66 pass; inherited SSH variables are removed only
  for these local loopback fixture processes. Production LAN clients keep the
  existing SSH-loopback refusal and no shell fallback.
- Independent review found two Important issues (persistent-ID takeover and
  missing shutdown drain). Both corrected; ID takeover reproduced before fix.
  Recovery clears stale errors. No Critical or parser defect identified.
- Warning-as-error builds and whitespace gate pass.

Commands: dotnet run --project tests/{OperationChecks,RunnerChecks,DiskAssetChecks,AgentChecks} -c Release;
dotnet run --project tests/AgentChecks -c Release -- --client;
python -m unittest discover -s tests -p 'test_runner_api*.py' -v.

These are fixture results. Windows CI/build qualification and installing the
matching server on the Windows tester remain deployment gates. No actual GBTG
HDD bytes, snapshot names, VM compatibility or game/viewer result are claimed.
The original game HDD, live test queue and baseline were not edited.

Agent assistance: Codex / GPT-6.

Native CI at21a1cd2: Linux/browser passed; Windows passed127/128 and exposed a test polling cancellation receipt before cleanup released ownership. The follow-up test waits for that boundary before asserting persistent-ID refusal. Product import code is unchanged in this follow-up.
