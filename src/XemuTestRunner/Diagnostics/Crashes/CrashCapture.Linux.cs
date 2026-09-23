using System.Globalization;
using System.Text.Json;

namespace XemuTestRunner.Diagnostics;

public sealed partial class CrashCapture
{
    private async Task<CrashReport> CollectLinuxAsync(CrashReport report, int pid, CancellationToken ct)
    {
        string? boot = null;
        try { boot = (await File.ReadAllTextAsync("/proc/sys/kernel/random/boot_id", ct).ConfigureAwait(false)).Trim().Replace("-", ""); }
        catch (IOException) { }
        if (boot is null) { _issues.Add("boot_identity_unavailable"); return report; }
        var matches = new[] { "MESSAGE_ID=fc2e22bc6ee647b6b90729ab34a250b1", "COREDUMP_PID=" + pid,
            "COREDUMP_EXE=" + _executable, "_BOOT_ID=" + boot };
        var end = DateTimeOffset.UtcNow.AddMilliseconds(_options.ProviderWaitMs);
        JsonElement? metadata = null;
        while (true)
        {
            var tool = await CrashTool.RunAsync(_options.JournalctlExecutable,
                new[] { "--no-pager", "--quiet", "--output=json", "--output-fields=COREDUMP_PID,COREDUMP_EXE,COREDUMP_SIGNAL,COREDUMP_TIMESTAMP,COREDUMP_TRUNCATED,_BOOT_ID,MESSAGE", "--lines=8", "--since=@" + _started.ToUnixTimeSeconds() }.Concat(matches), _directory, ct).ConfigureAwait(false);
            if (tool.State == "captured")
            {
                foreach (var line in tool.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    using var document = JsonDocument.Parse(line);
                    var value = document.RootElement;
                    if (Field(value, "COREDUMP_PID") == pid.ToString(CultureInfo.InvariantCulture) &&
                        Field(value, "COREDUMP_EXE") == _executable && Field(value, "_BOOT_ID") == boot &&
                        long.TryParse(Field(value, "COREDUMP_TIMESTAMP"), out var stamp) &&
                        stamp >= _started.ToUnixTimeSeconds() * 1000000 && stamp <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000)
                        metadata = value.Clone();
                }
            }
            if (metadata is not null || tool.State == "unavailable" || DateTimeOffset.UtcNow >= end)
            {
                if (metadata is null)
                {
                    _issues.Add("linux_core_metadata_unavailable:" + tool.State);
                    await SaveToolAsync("journal-query", tool, ct).ConfigureAwait(false);
                }
                break;
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        if (metadata is not JsonElement found) return report;
        _osReport = true;
        await File.WriteAllTextAsync(Path.Combine(_directory, "systemd-coredump.json"), found.GetRawText(), ct).ConfigureAwait(false);
        var nativeText = Field(found, "MESSAGE") ?? "systemd-coredump metadata captured; backtrace absent.";
        await File.WriteAllTextAsync(Path.Combine(_directory, "systemd-report.txt"), nativeText, ct).ConfigureAwait(false);
        ExtractFrames(nativeText);
        if (int.TryParse(Field(found, "COREDUMP_SIGNAL"), out var signal))
        {
            var fatal = signal is 3 or 4 or 5 or 6 or 7 or 8 or 11 or 24 or 25 or 31;
            if (fatal) report = report with { Crashed = true, Source = "systemd-coredump", Code = CrashClassification.SignalName(signal) };
        }
        if (!_options.CollectDumps) { _dump = "notRequested"; return report; }
        var core = Path.Combine(_directory, "xemu.core");
        var partial = core + ".part";
        var exact = matches.Append("COREDUMP_TIMESTAMP=" + Field(found, "COREDUMP_TIMESTAMP")).ToArray();
        try
        {
            var dump = await CrashTool.RunAsync(_options.CoredumpctlExecutable,
                new[] { "--no-pager", "dump" }.Concat(exact), _directory, ct, partial, _options.MaxArtifactBytes).ConfigureAwait(false);
            _dump = dump.State;
            if (dump.State != "captured") { _issues.Add("core_export:" + dump.State + ":" + Clip(dump.Error)); return report; }
            await using (var stream = File.OpenRead(partial))
            {
                var header = new byte[4];
                await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                if (!header.SequenceEqual(new byte[] { 127, 69, 76, 70 })) throw new InvalidDataException("Export is not an ELF core.");
            }
            File.Move(partial, core, false);
            if (Field(found, "COREDUMP_TRUNCATED") == "1") { _dump = "truncated"; _issues.Add("core_truncated_by_host"); }
            if (!_options.AnalyzeDumps) { _analysis = "notRequested"; return report; }
            var analysis = await CrashTool.RunAsync(_diagnostics.GdbExecutable,
                ["-nx", "-batch", "-iex", "set auto-load off", "-iex", "set debuginfod enabled off",
                 "-iex", "set pagination off", "-iex", "set confirm off", "-iex", "set print elements 64",
                 "-ex", "info registers", "-ex", "thread apply all bt 32", "-ex", "info sharedlibrary",
                 "--se", _executable, "--core", core], _directory, ct).ConfigureAwait(false);
            _analysis = analysis.State;
            await SaveToolAsync("gdb-backtrace", analysis, ct).ConfigureAwait(false);
            ExtractFrames(analysis.Output);
        }
        finally { try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return report;
    }

    private static string? Field(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var item)) return null;
        return item.ValueKind == JsonValueKind.String ? item.GetString() : item.ValueKind == JsonValueKind.Number ? item.GetRawText() : null;
    }
}
