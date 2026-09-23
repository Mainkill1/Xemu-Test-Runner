var checks = new List<(string Name, Func<Task> Run)>();
if (args.Contains("--client", StringComparer.Ordinal))
{
    ClientChecks.Register(checks);
    RequestedClientChecks.Register(checks);
}
else
{
    ObservationChecks.Register(checks);
    TemplateChecks.Register(checks);
    EvidenceChecks.Register(checks);
    ActionBodyChecks.Register(checks);
    RequestedTestChecks.Register(checks);
    HashResultChecks.Register(checks);
    ReuseStressChecks.Register(checks);
    BuildIndexRecoveryChecks.Register(checks);
    CrashApiChecks.Register(checks);
}
var failures = 0;
foreach (var (name, run) in checks)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
Console.WriteLine($"Agent API checks: {checks.Count - failures}/{checks.Count} passed.");
return failures == 0 ? 0 : 1;
