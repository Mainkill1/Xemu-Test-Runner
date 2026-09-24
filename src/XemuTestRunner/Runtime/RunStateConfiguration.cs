using System.Text;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Runtime;

internal static class RunStateConfiguration
{
    public static async Task<(List<string> Arguments, string SemanticHash)> PrepareAsync(JobDefinition job, string executable,
        string workingDirectory, IReadOnlyList<string> arguments, string resultDirectory, string evidenceDirectory, RunStorageReport report, CancellationToken ct)
    {
        var policy = job.RuntimeState.Isolation!;
        var package = job.PackageDirectory ?? workingDirectory;
        var marker = Path.Combine(Path.GetDirectoryName(executable)!, "xemu.toml");
        RunStateInventory.NoLinks(marker);
        if (!File.Exists(marker)) throw new InvalidDataException("Managed xemu state requires a portable xemu.toml beside the executable. A config override alone does not isolate its cache base.");
        var args = new List<string>(); string? configured = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            var item = arguments[i];
            if (item == "-config_path" || item.StartsWith("-config_path=", StringComparison.Ordinal))
            {
                if (configured is not null) throw new InvalidDataException("Multiple -config_path arguments are ambiguous.");
                if (item == "-config_path") { if (++i == arguments.Count) throw new InvalidDataException("-config_path needs a filename."); configured = arguments[i]; }
                else configured = item[13..];
            }
            else
            {
                if (new[] { "-drive", "-hda", "-hdb", "-hdc", "-hdd", "-blockdev", "-readconfig" }.Any(option => item == option || item.StartsWith(option + "=", StringComparison.Ordinal)))
                    throw new InvalidDataException("Storage/config command-line overrides are not supported by the managed xemu profile; declare paths in its pinned TOML.");
                args.Add(item);
            }
        }
        var source = configured is null ? marker : Path.GetFullPath(configured, workingDirectory);
        EnsureInside(package, source); RunStateInventory.NoLinks(source);
        var bytes = await ReadSmallAsync(source, ct).ConfigureAwait(false);
        report.ConfigSourcePath = source; report.ConfigSha256 = RunStateInventory.Hash(bytes);
        await File.WriteAllBytesAsync(Path.Combine(evidenceDirectory, "config-before.toml"), bytes, ct).ConfigureAwait(false);
        TomlTable model;
        try { model = Toml.ToModel(Encoding.UTF8.GetString(bytes)); }
        catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
        { throw new InvalidDataException("The supplied xemu TOML could not be parsed.", error); }
        Table(model, "perf")["cache_shaders"] = policy.CacheShaders;
        var runtime = ReadRuntime(resultDirectory);
        report.RuntimeSeeds = runtime?.Files ?? [];
        var files = Table(Table(model, "sys"), "files");
        await SharedReadOnlyInputs.PrepareAsync(policy, runtime, report, ct).ConfigureAwait(false);
        // The semantic procedure pins shared bytes, not deployment-specific paths.
        // With no selected shared assets, existing contract hashes are unchanged.
        if (report.ReadOnlyAssets is { } shared)
            foreach (var asset in shared) files[asset.Field] = "catalog:" + asset.ExpectedSha256;
        var semanticHash = RunStateInventory.Hash(Encoding.UTF8.GetBytes(Toml.FromModel(model)));
        foreach (var name in new[] { "bootrom_path", "flashrom_path", "dvd_path", "hdd_path", "eeprom_path" })
        {
            var selected = report.ReadOnlyAssets?.SingleOrDefault(asset => asset.Field == name);
            if (selected is not null)
            {
                files[name] = selected.Path; report.StoragePaths[name] = selected.Path; continue;
            }
            var mutable = name is "hdd_path" or "eeprom_path";
            if (!files.TryGetValue(name, out var raw) || raw is not string text || string.IsNullOrWhiteSpace(text))
            {
                if (mutable && policy.RequirePrivateGuestState) throw new InvalidDataException("Managed private guest state requires explicit " + name + " and its materialized seed.");
                if (mutable) AddUncontrolled(report, "guest-" + name);
                continue;
            }
            text = RuntimeStateManager.Expand(text, package, resultDirectory, Path.GetFileName(resultDirectory), runtime);
            var path = Path.GetFullPath(text, workingDirectory); RunStateInventory.NoLinks(path);
            if (mutable && policy.RequirePrivateGuestState)
            {
                if (runtime is null || !runtime.Files.Any(file => SamePath(RuntimeStateManager.ResolveInside(runtime.Directory, file.Destination), path)))
                    throw new InvalidDataException(name + " does not refer to a declared private runtime materialization.");
                if (!File.Exists(path)) throw new InvalidDataException("Private guest state is missing: " + name);
            }
            else if (mutable) AddUncontrolled(report, "guest-" + name);
            else EnsureInside(package, path);
            files[name] = path; report.StoragePaths[name] = path;
        }
        var screenshots = Path.Combine(resultDirectory, "screenshots"); RunStateInventory.NoLinks(screenshots); Directory.CreateDirectory(screenshots);
        Table(model, "general")["screenshot_dir"] = screenshots; report.StoragePaths["screenshots"] = screenshots;
        var effective = Encoding.UTF8.GetBytes(Toml.FromModel(model));
        var effectivePath = Path.Combine(evidenceDirectory, "effective-live.toml");
        await File.WriteAllBytesAsync(effectivePath, effective, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(evidenceDirectory, "config-effective.toml"), effective, ct).ConfigureAwait(false);
        report.EffectiveConfigPath = effectivePath; report.EffectiveConfigSha256 = RunStateInventory.Hash(effective);
        args.Add("-config_path"); args.Add(effectivePath); return (args, semanticHash);
    }
    private static TomlTable Table(TomlTable parent, string name)
    {
        if (!parent.TryGetValue(name, out var value)) { var created = new TomlTable(); parent[name] = created; return created; }
        return value as TomlTable ?? throw new InvalidDataException("TOML section must be a table: " + name);
    }
    private static RuntimeMaterialization? ReadRuntime(string result)
    {
        var path = Path.Combine(result, "runtime-state.json"); if (!File.Exists(path)) return null;
        RunStateInventory.NoLinks(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Runtime metadata exceeds 1 MiB.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var document = JsonDocument.Parse(file);
        var root = document.RootElement;
        if (!root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True) return null;
        var directory = root.GetProperty("directory").GetString() ?? throw new InvalidDataException("Missing runtime directory.");
        var files = root.GetProperty("files").Deserialize<List<RuntimeFileMaterialization>>(ConfigLoader.JsonOptions) ?? throw new InvalidDataException("Missing runtime files.");
        RunStateInventory.NoLinks(directory); return new(directory, files);
    }
    internal static async Task<byte[]> ReadSmallAsync(string path, CancellationToken ct)
    {
        RunStateInventory.NoLinks(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length > 1024 * 1024) throw new InvalidDataException("Configuration exceeds 1 MiB.");
        var bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        if (file.ReadByte() != -1) throw new InvalidDataException("Configuration grew while reading.");
        return bytes;
    }
    internal static void EnsureInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Managed state/config path escapes the selected package.");
    }
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    internal static void AddUncontrolled(RunStorageReport report, string issue) { if (!report.Uncontrolled.Contains(issue)) report.Uncontrolled.Add(issue); }
}
