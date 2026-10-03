# Standalone component example

Supply a real `component-benchmark` executable and required dependencies. This
example intentionally includes no executable or invented measurement results.
Its fixed-work program should emit one JSON object such as
`{"correct":true,"elapsedUs":123}` after validating its output. The example
value illustrates the schema; it is not benchmark evidence.

Submit through the maintained HTTP client. The target needs no QMP endpoint and
terminates by returning its actual exit code. The runner preserves stdout,
stderr, native exit status, timeout/failure evidence and normal operation policy.
Use pinned saved revisions and balanced physical run orders for comparisons.
This is a component contract with no xemu shader/guest-state qualification.
