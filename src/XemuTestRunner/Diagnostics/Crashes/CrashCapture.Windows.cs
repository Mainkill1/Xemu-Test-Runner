using System.Globalization;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace XemuTestRunner.Diagnostics;

public sealed partial class CrashCapture
{
    [SupportedOSPlatform("windows")]
    private async Task<CrashReport> CollectWindowsAsync(CrashReport report, int pid, CancellationToken ct)
    {
        var end = DateTimeOffset.UtcNow.AddMilliseconds(_options.ProviderWaitMs);
        var executableName = Path.GetFileName(_executable);
        while (true)
        {
            var query = "*[System[Provider[@Name='Application Error'] and (EventID=1000) and TimeCreated[@SystemTime>='" +
                _started.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + "']]]";
            var events = await CrashTool.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wevtutil.exe"),
                ["qe", "Application", "/q:" + query, "/f:xml", "/c:32", "/rd:true"], _directory, ct).ConfigureAwait(false);
            if (events.State == "captured" && !string.IsNullOrWhiteSpace(events.Output))
            {
                using var reader = XmlReader.Create(new StringReader("<events>" + events.Output + "</events>"),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 524288 });
                var xml = XDocument.Load(reader);
                foreach (var item in xml.Descendants().Where(element => element.Name.LocalName == "Event"))
                {
                    var data = item.Descendants().Where(element => element.Name.LocalName == "Data" && element.Attribute("Name") is not null)
                        .GroupBy(element => element.Attribute("Name")!.Value).ToDictionary(group => group.Key, group => group.First().Value);
                    if (!data.TryGetValue("ProcessId", out var id) || ParseWindowsNumber(id) != (ulong)pid ||
                        !data.TryGetValue("AppPath", out var path) || !path.Equals(_executable, StringComparison.OrdinalIgnoreCase)) continue;
                    _osReport = true;
                    await File.WriteAllTextAsync(Path.Combine(_directory, "windows-event.xml"), item.ToString(), ct).ConfigureAwait(false);
                    if (data.TryGetValue("ExceptionCode", out var code))
                        report = report with { Crashed = true, Source = "windowsApplicationError", Code = "0x" + code.Replace("0x", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant() };
                    if (data.TryGetValue("ModuleName", out var module)) _frames.Add("Faulting module: " + Clip(module));
                    if (data.TryGetValue("FaultingOffset", out var offset)) _frames.Add("Faulting offset: " + Clip(offset));
                }
            }
            if (_osReport || events.State == "unavailable" || DateTimeOffset.UtcNow >= end) break;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        if (!_osReport) _issues.Add("windows_event_unavailable");
        if (!_options.CollectDumps) { _dump = "notRequested"; return report; }
        var folder = _options.WindowsDumpDirectory;
        if (folder is null)
        {
            using var app = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\" + executableName);
            using var global = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps");
            folder = app?.GetValue("DumpFolder") as string ?? global?.GetValue("DumpFolder") as string;
            if (app is null && global is null) _issues.Add("wer_local_dumps_not_configured");
            folder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");
        }
        folder = Environment.ExpandEnvironmentVariables(folder);
        var dump = Path.Combine(folder, executableName + "." + pid + ".dmp");
        if (!File.Exists(dump)) { _issues.Add("matching_windows_dump_unavailable"); return report; }
        RejectLinks(dump, folder);
        if (File.GetLastWriteTimeUtc(dump) < _started.UtcDateTime.AddSeconds(-1) ||
            !MiniDumpIdentity.Matches(dump, pid, _executable, _started))
        { _issues.Add("stale_or_unmatched_windows_dump_excluded"); return report; }
        var target = Path.Combine(_directory, "xemu.dmp");
        if (!await CopyBoundedAsync(dump, target, ct).ConfigureAwait(false)) { _dump = "sizeLimit"; return report; }
        _dump = "captured"; _osReport = true;
        if (!_options.AnalyzeDumps) { _analysis = "notRequested"; return report; }
        var analysis = await CrashTool.RunAsync(_options.CdbExecutable,
            ["-z", target, "-y", Path.GetDirectoryName(_executable)!, "-c", ".ecxr; !analyze -v; ~* kb; q"], _directory, ct).ConfigureAwait(false);
        _analysis = analysis.State;
        await SaveToolAsync("windows-analysis", analysis, ct).ConfigureAwait(false);
        _frames.AddRange(analysis.Output.Split('\n').Where(line => line.Contains("!", StringComparison.Ordinal) && !line.TrimStart().StartsWith("0:")).Take(6).Select(Clip));
        return report;
    }

    private static ulong? ParseWindowsNumber(string text) => ulong.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
}

// Filename/time alone are not sufficient correlation: inspect the native dump
// process and image identity before copying an OS-wide artifact into this run.
internal static class MiniDumpIdentity
{
    public static bool Matches(string path, int pid, string executable, DateTimeOffset started)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new BinaryReader(file);
            if (reader.ReadUInt32() != 0x504d444d) return false;
            _ = reader.ReadUInt32();
            var count = reader.ReadUInt32(); var directory = reader.ReadUInt32();
            if (count > 128 || directory > file.Length - count * 12L) return false;
            var matchedPid = false; var matchedImage = false;
            for (var i = 0; i < count; i++)
            {
                file.Position = directory + i * 12L;
                var type = reader.ReadUInt32(); var size = reader.ReadUInt32(); var rva = reader.ReadUInt32();
                if (rva > file.Length - size) return false;
                if (type == 15 && size >= 24)
                {
                    file.Position = rva; _ = reader.ReadUInt32(); var flags = reader.ReadUInt32();
                    var processId = reader.ReadUInt32(); var created = reader.ReadUInt32();
                    matchedPid = (flags & 1) != 0 && processId == pid;
                    if ((flags & 2) != 0 && created < started.ToUnixTimeSeconds() - 3) return false;
                }
                if (type == 4 && size >= 112)
                {
                    file.Position = rva; var modules = reader.ReadUInt32();
                    if (modules > 4096 || size < 4L + modules * 108L) return false;
                    for (var module = 0; module < modules; module++)
                    {
                        file.Position = rva + 4L + module * 108L + 20;
                        var nameRva = reader.ReadUInt32();
                        if (nameRva > file.Length - 4) return false;
                        file.Position = nameRva; var bytes = reader.ReadUInt32();
                        if (bytes > 32768 || bytes % 2 != 0 || bytes > file.Length - file.Position) return false;
                        var name = System.Text.Encoding.Unicode.GetString(reader.ReadBytes((int)bytes));
                        if (name.Equals(executable, StringComparison.OrdinalIgnoreCase)) matchedImage = true;
                    }
                }
            }
            return matchedPid && matchedImage;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
