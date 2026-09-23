internal static class ReuseStressChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("repeated large-plan reuse while polling preserves publication", async () =>
        {
            var originals = new List<(string Name, Func<Task> Run)>();
            TemplateChecks.Register(originals);
            var exercise = originals.Single(check => check.Name == "pre-baked test expands a large plan from a small request");
            for (var iteration = 1; iteration <= 12; iteration++)
            {
                try { await exercise.Run(); }
                catch (Exception error) { throw new InvalidOperationException($"Reuse stress iteration {iteration}: {error.Message}", error); }
            }
        }));
    }
}
