using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Control.Gamepad;
using XemuTestRunner.Queue;
using static AgentFixture;

internal static class ControllerDefinitionChecks
{
    private static JobDefinition Load(object value)
    {
        var directory = Path.Combine(Path.GetTempPath(), "controller-definition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "job.json"), JsonSerializer.Serialize(value));
            return JobDefinition.LoadPackage(directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("legacy job omits controller mode on serialization", async () =>
        {
            var raw = JsonSerializer.Serialize(new JobDefinition { Id = "legacy", Executable = "xemu" }, ConfigLoader.JsonOptions);
            Require(!raw.Contains("ControllerInput", StringComparison.OrdinalIgnoreCase),
                "Adding the native route changed the serialization of old jobs.");
            Require(Load(new { id = "legacy", executable = "xemu" }).ControllerInput is null,
                "An old job was silently converted to a controller test.");
            await Task.CompletedTask;
        }));

        checks.Add(("native controller mode rejects unsupported binding and launch modes", async () =>
        {
            var valid = Load(new {
                id = "native", executable = "xemu", requireInput = true,
                runtimeState = new { isolation = new { cacheMode = "cold" } },
                controllerInput = new { backend = "native-os-gamepad", controllerIndex = 0,
                    mappingProfile = "runner-xbox-port1-v1" },
                plan = new[] { new { type = "button", button = "A", durationMs = 250 } }
            });
            Require(valid.ControllerInput is { ControllerIndex: 0 }, "Native controller definition was lost.");
            var missingIndexRejected = false;
            try { _ = Load(new { id = "missing-index", executable = "xemu", requireInput = true,
                runtimeState = new { isolation = new { cacheMode = "cold" } },
                controllerInput = new { backend = "native-os-gamepad", mappingProfile = "runner-xbox-port1-v1" } }); }
            catch (InvalidDataException) { missingIndexRejected = true; }
            Require(missingIndexRejected, "An omitted controller index was treated as an explicit port selection.");
            foreach (var invalid in new[] {
                new { backend = "keyboard", controllerIndex = 0, mappingProfile = "runner-xbox-port1-v1" },
                new { backend = "native-os-gamepad", controllerIndex = 1, mappingProfile = "runner-xbox-port1-v1" },
                new { backend = "native-os-gamepad", controllerIndex = 0, mappingProfile = "unqualified" }
            })
            {
                var rejected = false;
                try { _ = Load(new { id = "bad", executable = "xemu", requireInput = true,
                    runtimeState = new { isolation = new { cacheMode = "cold" } }, controllerInput = invalid }); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "Unqualified controller mode was accepted.");
            }
            await Task.CompletedTask;
        }));

        checks.Add(("native button names map to full controller state without host keys", async () =>
        {
            Require(ControllerButtonMap.Resolve("A").Buttons == XboxControllerButtons.A,
                "A did not map to the gamepad button.");
            Require(ControllerButtonMap.Resolve("Start").Buttons == XboxControllerButtons.Start,
                "Start did not map to the gamepad button.");
            Require(ControllerButtonMap.Resolve("RTrigger").RightTrigger == byte.MaxValue,
                "RTrigger did not map to analog full scale.");
            Require(ControllerButtonMap.Resolve("LStickRight").LeftX == short.MaxValue,
                "Left stick direction did not map to analog full scale.");
            var rejected = false;
            try { _ = ControllerButtonMap.Resolve("Guide"); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Unsupported Guide input was accepted.");
            await Task.CompletedTask;
        }));

        checks.Add(("full native states preserve analog combinations and reject invalid buttons", async () =>
        {
            var state = new ControllerStateDefinition {
                Buttons = ["A", "Start"], RightTrigger = 255, LeftX = 16000
            }.ToState();
            Require(state.Buttons == (XboxControllerButtons.A | XboxControllerButtons.Start) &&
                state.RightTrigger == 255 && state.LeftX == 16000,
                "Combined full state was not preserved.");
            foreach (var invalid in new[] {
                new ControllerStateDefinition { Buttons = ["DPadUp", "DPadDown"] },
                new ControllerStateDefinition { Buttons = ["A", "A"] },
                new ControllerStateDefinition { Buttons = ["Guide"] },
                new ControllerStateDefinition { RightTrigger = 256 }
            })
            {
                var rejected = false;
                try { _ = invalid.ToState(); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "Invalid full controller state was accepted.");
            }
            await Task.CompletedTask;
        }));
    }
}
