var checks = new List<(string Name, Func<Task> Run)>();
ObservationChecks.Register(checks);
TemplateChecks.Register(checks);
var failures = 0;
foreach (var (name, run) in checks)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
Console.WriteLine($"Agent API checks: {checks.Count - failures}/{checks.Count} passed.");
return failures == 0 ? 0 : 1;
