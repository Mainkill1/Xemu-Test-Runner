# Five reliability additions

Goal: make the existing foreground Windows/Linux runner useful for unattended build qualification without making it a service or requiring an Xbox.

1. Preflight: validate the packaged executable, target OS, declared required files, optional expected SHA-256, and available result volume space before Process.Start. Expose the same validation through `validate` and retain its report.
2. Recovery: acquire one exclusive workspace lease; persist attempt and process identity; archive already-finalized attempts instead of replaying; hold ambiguous/live processes; bound retries. No process is killed just because a PID happens to match.
3. Watchdog: distinguish a non-responsive QMP control endpoint from normal process failure. Configurable grace/interval/request timeout/consecutive failures. Paused VMs still answer QMP. This cannot prove that a game itself is making progress.
4. Evidence browser: bounded recent-run listing, result/artifact links, and bounded log tails without downloading multi-gigabyte logs. Stream artifacts with single-range support and refuse paths outside the selected result.
5. Benchmark hygiene: one shared rate-limited preview cache per active run, including failure backoff; record operator input, pause, retained screenshots, previews and bulk transfers in a per-run journal and expose explicit intervention counts in result.json. Cached API reads do not create new hardware samples.

Validation: dependency-free .NET regression executable; source/API review; JavaScript syntax checks; manual Windows/Linux graphical xemu qualification remains separate. No GitHub Actions. Keep hashes and outcome records machine-readable. Do not claim process exit zero proves Xbox hardware correctness.

Important failure cases: malformed manifest, already-finalized recovery, PID reuse/live orphan, watchdog cancellation, simultaneous previews, run change during capture, multi-gigabyte log tails, path traversal, and interrupted PNG transfers. Avoid filesystem copies when queue directories can be renamed on the same volume.
