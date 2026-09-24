using System.Net;
using System.Text;
using static AgentFixture;

internal static class ActionBodyChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("parameterless actions consume empty JSON but reject unexpected fields", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("body-contract");
            foreach (var action in new[] { "submit", "validate", "withdraw" })
            {
                var error = await host.Json($"/api/v1/jobs/body-contract/{action}", HttpMethod.Post,
                    new { unexpected = true }, HttpStatusCode.BadRequest);
                Require(error.GetProperty("code").GetString() == "action_body_invalid", "Action silently ignored body fields.");
            }
            Require((await host.Json("/api/v1/jobs/body-contract?view=summary")).GetProperty("state").GetString() == "draft", "Rejected body changed job state.");
            // Withdrawal of an existing draft is idempotent. A padded empty
            // object exercises actual body consumption without queue mutation.
            using var content = new StringContent(new string(' ', 2048) + "{}", Encoding.UTF8, "application/json");
            using var response = await host.Client.PostAsync("/api/v1/jobs/body-contract/withdraw", content);
            Require(response.StatusCode == HttpStatusCode.OK, "Empty JSON action body was rejected.");
            Require((await response.Content.ReadAsStringAsync()).Contains("body-contract", StringComparison.Ordinal), "Action response was not fully delivered.");
        }));
    }
}
