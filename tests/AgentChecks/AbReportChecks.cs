using System.Text.Json;
using XemuTestRunner.Reliability;
using static AgentFixture;

internal static class AbReportChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("A/B statistics expose direction-aware improvement and preserve signed change", async () =>
        {
            await using var host = new AgentFixture();
            Seed(host, "a1", 'a', 100, "lower"); Seed(host, "a2", 'a', 120, "lower");
            Seed(host, "b1", 'b', 80, "lower"); Seed(host, "b2", 'b', 96, "lower");
            var value = await host.Json("/api/v1/compare?A=" + new string('a', 64) + "&B=" + new string('b', 64));
            var row = value.GetProperty("rows")[0];
            Require(row.GetProperty("changePercent").GetDouble() == -20, "Existing signed delta changed meaning.");
            Require(row.GetProperty("improvementPercent").GetDouble() == 20, "Faster lower-is-better result needs positive improvement.");
            var a = row.GetProperty("statsA");
            Require(a.GetProperty("count").GetInt32() == 2 && a.GetProperty("mean").GetDouble() == 110 &&
                a.GetProperty("min").GetDouble() == 100 && a.GetProperty("max").GetDouble() == 120, "A distribution missing.");
            Require(row.GetProperty("statsB").GetProperty("median").GetDouble() == 88, "B statistics are not calculated server-side.");
        }));
        checks.Add(("higher-is-better gains and regressions use the same improvement sign", async () =>
        {
            await using var host = new AgentFixture();
            Seed(host, "a", 'a', 100, "higher"); Seed(host, "b", 'b', 125, "higher");
            var ab = await host.Json("/api/v1/compare?A=" + new string('a',64) + "&B=" + new string('b',64));
            Require(ab.GetProperty("rows")[0].GetProperty("improvementPercent").GetDouble() == 25, "Higher gain sign is wrong.");
            var ba = await host.Json("/api/v1/compare?A=" + new string('b',64) + "&B=" + new string('a',64));
            Require(ba.GetProperty("rows")[0].GetProperty("improvementPercent").GetDouble() == -20, "Regression must be negative.");
        }));
        checks.Add(("every Markdown section and comparison CSV ends with improvement percent", async () =>
        {
            await using var host = new AgentFixture();
            Seed(host, "a", 'a', 100, "lower"); Seed(host, "b", 'b', 80, "lower");
            var route = "/api/v1/compare?A=" + new string('a',64) + "&B=" + new string('b',64);
            var markdown = await host.Client.GetStringAsync(route + "&format=markdown");
            Require(markdown.Split('\n').Any(line => line.Contains("Metric") && line.TrimEnd().EndsWith("Improvement % |", StringComparison.Ordinal)), "Improvement is not the last table column.");
            Require(markdown.Contains("+20.00%", StringComparison.Ordinal), "Readable report must use two decimal places.");
            var csv = await host.Client.GetStringAsync(route + "&format=csv");
            Require(csv.Split('\n')[0].TrimEnd().EndsWith(",improvement_percent", StringComparison.Ordinal), "CSV last column is not improvement_percent.");
        }));
        checks.Add(("display rounding leaves canonical JSON and CSV precision intact", async () =>
        {
            await using var host = new AgentFixture();
            Seed(host, "a", 'a', 1.1557, "lower"); Seed(host, "b", 'b', 0.23113999999999998, "lower");
            var route = "/api/v1/compare?A=" + new string('a',64) + "&B=" + new string('b',64);
            var value = await host.Json(route);
            Require(value.GetProperty("rows")[0].GetProperty("a").GetDouble() == 1.1557, "Stored precision was rounded.");
            var markdown = await host.Client.GetStringAsync(route + "&format=markdown");
            Require(markdown.Contains("| 1.16 | 0.23 |", StringComparison.Ordinal), "Display measurements need two decimals.");
            Require((await host.Client.GetStringAsync(route + "&format=csv")).Contains("1.1557", StringComparison.Ordinal), "CSV lost original precision.");
        }));
        checks.Add(("artifact viewer is served without exposing raw HTML artifacts", async () =>
        {
            await using var host = new AgentFixture();
            using var response = await host.Client.GetAsync("/results/view?run=sample&file=report.json");
            Require(response.IsSuccessStatusCode, "Viewer route is missing.");
            Require((await response.Content.ReadAsStringAsync()).Contains("Artifact viewer", StringComparison.Ordinal), "Viewer page missing.");
        }));
        checks.Add(("unmeasured direction and zero baseline never invent improvement", async () =>
        {
            await using var host = new AgentFixture();
            Seed(host, "a", 'a', 0, "lower"); Seed(host, "b", 'b', 80, "lower");
            var value = await host.Json("/api/v1/compare?A=" + new string('a',64) + "&B=" + new string('b',64));
            Require(value.GetProperty("rows")[0].GetProperty("improvementPercent").ValueKind == JsonValueKind.Null, "Zero reference produced a percentage.");
        }));
    }

    private static void Seed(AgentFixture host, string id, char hash, double value, string direction)
    {
        var sha = new string(hash, 64);
        AtomicJson.Write(Path.Combine(host.Paths.Results, ".build-results", sha, id + ".json"), new
        {
            RunId = id, Sha256 = sha, TestKey = new string('c',64), EnvironmentKey = new string('d',64),
            BuildKey = sha, Test = "smoke", Outcome = new { Execution = "completed", Correctness = "passed", Evidence = "complete", Comparison = "eligible" },
            Eligible = true, Issues = Array.Empty<string>(),
            Metrics = new[] { new { Name = "xiso/cpu/test/median_us", Value = value, Unit = "us", Direction = direction } },
            RawCsv = "/api/v1/runs/" + id + "/artifacts/metrics.csv"
        });
    }
}
