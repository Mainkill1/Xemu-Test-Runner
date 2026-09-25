using System.Text.Json;
using XemuTestRunner.Queue;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

internal sealed partial class AgentJobStore
{
    private static string PortablePath(string value)
    {
        value = value.Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        ValidateRelative(value);
        return value;
    }

    // An explicit role supports renamed emulator binaries. Conventional xemu
    // names are recognized for the existing launcher-bundle contract. This is
    // declared payload identity; execution still verifies the actual file bytes.
    private static string EmulatorPayloadPath(JobDefinition job, IReadOnlyList<AgentFile> files)
    {
        var launch = PortablePath(job.Executable);
        var explicitPaths = job.Inputs.Where(input => input.Role.Equals("emulator", StringComparison.OrdinalIgnoreCase))
            .Select(input => PortablePath(input.Path)).Distinct(StringComparer.Ordinal).ToArray();
        if (explicitPaths.Length > 1) throw new InvalidDataException("Only one input may have Role=emulator.");
        if (explicitPaths.Length == 1)
        {
            if (!files.Any(file => file.Path == explicitPaths[0])) throw new InvalidDataException("Emulator input is not declared in the package.");
            return explicitPaths[0];
        }
        var extension = System.IO.Path.GetExtension(launch).ToLowerInvariant();
        if (extension is not (".sh" or ".cmd" or ".bat" or ".ps1" or ".py")) return launch;
        var candidates = files.Select(file => file.Path).Where(path =>
            System.IO.Path.GetFileName(path).Equals("xemu", StringComparison.OrdinalIgnoreCase) ||
            System.IO.Path.GetFileName(path).Equals("xemu.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length > 1) throw new InvalidDataException("Launcher bundle contains multiple xemu binaries. Select the payload with an explicit Role=emulator input.");
        return candidates.Length == 1 ? candidates[0] : launch;
    }

    private static string[] DefaultBuildSlots(JobDefinition job, IReadOnlyList<AgentFile> files) =>
        new[] { PortablePath(job.Executable), EmulatorPayloadPath(job, files) }.Distinct(StringComparer.Ordinal).ToArray();

    private static string RequireEmulatorBuildSlot(JobDefinition job, IReadOnlyList<AgentFile> files, IReadOnlyList<string> buildFiles)
    {
        var payload = EmulatorPayloadPath(job, files);
        if (!buildFiles.Contains(payload, StringComparer.Ordinal))
            throw BadRequest("emulator_build_slot_required", "The test can replace its launcher but not the native emulator: " + payload,
                "Create a new test revision whose BuildFiles include the launcher and native xemu payload. Older revisions remain inspectable, not silently repaired.");
        return payload;
    }

    private static void PinEmulatorInput(JobDefinition job, IReadOnlyList<AgentFile> files)
    {
        var payload = EmulatorPayloadPath(job, files);
        if (payload == PortablePath(job.Executable)) return;
        var declaration = files.Single(file => file.Path == payload);
        var input = job.Inputs.SingleOrDefault(value => PortablePath(value.Path) == payload);
        if (input is null) { input = new InputIdentityDefinition { Path = payload }; job.Inputs.Add(input); }
        input.Role = "emulator";
        input.Hash = true;
        input.ExpectedSha256 = declaration.Sha256;
    }

    private static void ValidateApplicationPayload(AgentTestDefinition definition, AgentJobRequest application)
    {
        var payload = RequireEmulatorBuildSlot(definition.Job, definition.Files, definition.BuildFiles);
        if (payload != PortablePath(definition.Job.Executable) && !application.Files.Any(file => file.Path == payload))
            throw BadRequest("emulator_payload_missing", "Application bundle does not provide required emulator build slot: " + payload,
                "Upload the candidate native binary with its launcher. Do not let the test reuse the source package's emulator.");
    }

    public object ApplicationIdentity(string id)
    {
        var app = ReadDocument(id).Request;
        var launch = PortablePath(app.Job.Executable);
        var payload = EmulatorPayloadPath(app.Job, app.Files);
        var file = app.Files.Single(item => item.Path == payload);
        var launcher = app.Files.Single(item => item.Path == launch);
        return new { id, sha256 = file.Sha256, executable = payload, launchExecutable = launch, launchSha256 = launcher.Sha256,
            identitySource = "immutableApplicationManifest", verification = "Actual payload hashes are checked during materialization and submit." };
    }

    private static string VerifiedEmulatorHash(JobDefinition job, IReadOnlyList<AgentFile> files, JsonElement manifest, string launchHash)
    {
        var payload = EmulatorPayloadPath(job, files);
        if (payload == PortablePath(job.Executable)) return launchHash;
        var declaration = files.Single(file => file.Path == payload);
        if (!manifest.TryGetProperty("Inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Launcher run has no verified native-emulator input manifest.");
        var matches = inputs.EnumerateArray().Where(input => input.TryGetProperty("Path", out var path) && path.ValueKind == JsonValueKind.String && PortablePath(path.GetString()!) == payload).ToArray();
        if (matches.Length != 1 || !matches[0].TryGetProperty("Verified", out var verified) || verified.ValueKind != JsonValueKind.True ||
            !matches[0].TryGetProperty("Sha256", out var hash) || hash.ValueKind != JsonValueKind.String ||
            !declaration.Sha256.Equals(hash.GetString(), StringComparison.OrdinalIgnoreCase) ||
            !matches[0].TryGetProperty("ExpectedSha256", out var expected) || expected.ValueKind != JsonValueKind.String ||
            !declaration.Sha256.Equals(expected.GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native-emulator identity is missing, unverified, or differs from the declared candidate. Historical evidence is not relabeled.");
        return declaration.Sha256.ToLowerInvariant();
    }
}
