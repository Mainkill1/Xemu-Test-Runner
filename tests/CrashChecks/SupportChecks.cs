using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using XemuTestRunner.Diagnostics;
using XemuTestRunner.Networking;

internal static class SupportChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("collector deadline kills a hanging tool without hanging the caller", async () =>
        {
            using var stop = new CancellationTokenSource(200);
            var watch = Stopwatch.StartNew();
            var result = await CrashTool.RunAsync(Environment.ProcessPath!, ["--target", "hang"], AppContext.BaseDirectory, stop.Token);
            Require(result.State == "timedOut", "Hanging collector did not report its own timeout.");
            Require(watch.Elapsed < TimeSpan.FromSeconds(4), "Collector timeout was unbounded.");
        });
        await check("legacy diagnostic timeout also bounds descendant-held output pipes", async () =>
        {
            var root = NewRoot(); var pidFile = Path.Combine(root, "child.pid");
            using var deadline = new CancellationTokenSource(3000);
            var watch = Stopwatch.StartNew();
            Exception? failure = null;
            try
            {
                try { await ToolProcess.RunAsync(Environment.ProcessPath!, ["--target", "hold-pipe", pidFile], root, 500, deadline.Token); }
                catch (Exception error) when (error is TimeoutException or OperationCanceledException) { failure = error; }
                Require(failure is TimeoutException, "Diagnostic waited for the caller's cancellation, not its own timeout.");
                Require(watch.Elapsed < TimeSpan.FromSeconds(2.5), "Descendant-held pipe defeated the tool deadline.");
            }
            finally
            {
                if (File.Exists(pidFile) && int.TryParse(await File.ReadAllTextAsync(pidFile), out var id))
                {
                    try
                    {
                        using var child = Process.GetProcessById(id);
                        child.Kill(true);
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException or TimeoutException) { }
                }
                Directory.Delete(root, true);
            }
        });
        await check("collector output is bounded without buffering a full dump", async () =>
        {
            using var stop = new CancellationTokenSource(3000);
            var result = await CrashTool.RunAsync(Environment.ProcessPath!, ["--target", "output"], AppContext.BaseDirectory, stop.Token, maxBytes: 1024);
            Require(result.State == "sizeLimit", "Oversized output was not bounded.");
            Require(result.Output.Length <= 1024, "Collector exceeded its stdout budget.");
        });
        await check("missing collectors retain the crash and reject copied stale reports", async () =>
        {
            var root = NewRoot();
            try
            {
                var package = Path.Combine(root, "package"); var results = Path.Combine(root, "results");
                Directory.CreateDirectory(package); Directory.CreateDirectory(results);
                await File.WriteAllTextAsync(Path.Combine(package, "crash.txt"), "a report from another attempt");
                var options = new DiagnosticsOptions { CrashReports = new CrashCaptureOptions
                {
                    ProviderWaitMs = 0, JournalctlExecutable = Path.Combine(root, "missing"), WindowsDumpDirectory = root,
                    ReportFiles = ["crash.txt"]
                }};
                var capture = new CrashCapture(options, "missing-tools", package, Path.Combine(package, "xemu.exe"), new string('a',64), results, DateTimeOffset.UtcNow);
                var native = new NativeExitStatus("fixture", 12345, null, 11, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var report = await capture.CollectAsync(12345, unchecked((int)0xC0000005), native, false, true, "failed");
                Require(report.Crashed, "Missing dump collector erased the native crash.");
                Require(report.Issues.Any(issue => issue.StartsWith("stale_report_excluded", StringComparison.Ordinal)), "Stale application report was included.");
                Require(File.Exists(Path.Combine(results, "crash", "report.json")), "No explanatory report was written.");
            }
            finally { Directory.Delete(root, true); }
        });
        await check("ZIP size limits and hashes are explicit and mirrored beside executable", async () =>
        {
            var root = NewRoot();
            try
            {
                var package = Path.Combine(root, "package"); var results = Path.Combine(root, "results");
                Directory.CreateDirectory(package); Directory.CreateDirectory(Path.Combine(results, "crash"));
                await File.WriteAllTextAsync(Path.Combine(package,"xemu.exe"), "fixture");
                await File.WriteAllTextAsync(Path.Combine(results,"result.json"), "{}");
                await File.WriteAllTextAsync(Path.Combine(results,"crash","report.json"), "{}");
                await File.WriteAllBytesAsync(Path.Combine(results,"crash","huge.core"), new byte[2048]);
                var bundle = await DiagnosticArchive.CreateAsync(results, package, "xemu.exe", "bounded", new string('a',64),
                    new CrashCaptureOptions { MaxArtifactBytes = 1024, MaxBundleBytes = 4096 });
                Require(bundle.State == "partial" && bundle.OmittedFiles == 1, "Oversized core omission is hidden.");
                Require(bundle.MirrorState == "captured" && bundle.BesideExecutable is not null, "ZIP was not mirrored.");
                var data = await File.ReadAllBytesAsync(Path.Combine(results,"diagnostics.zip"));
                Require(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() == bundle.Sha256, "ZIP digest is wrong.");
                var mirrorBytes = await File.ReadAllBytesAsync(Path.Combine(package,bundle.BesideExecutable!));
                Require(data.SequenceEqual(mirrorBytes), "Mirror bytes differ.");
                using var zip = ZipFile.OpenRead(Path.Combine(results,"diagnostics.zip"));
                Require(zip.GetEntry("crash/huge.core") is null, "Oversized core entered the ZIP.");
                using var manifest = JsonDocument.Parse(zip.GetEntry("bundle-manifest.json")!.Open());
                Require(!manifest.RootElement.GetProperty("complete").GetBoolean(), "Partial ZIP reports full capture.");
                foreach (var item in manifest.RootElement.GetProperty("included").EnumerateArray())
                {
                    using var stream = zip.GetEntry(item.GetProperty("Path").GetString()!)!.Open();
                    Require(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() == item.GetProperty("Sha256").GetString(), "Entry digest differs from archive data.");
                }
            }
            finally { Directory.Delete(root, true); }
        });
        await check("failed ZIP capture returns an error instead of blocking execution", async () =>
        {
            var root = NewRoot();
            try
            {
                var result = Path.Combine(root,"results"); Directory.CreateDirectory(result);
                Directory.CreateDirectory(Path.Combine(result,"diagnostics.zip"));
                await File.WriteAllTextAsync(Path.Combine(result,"result.json"), "{}");
                var value = await DiagnosticArchive.CreateAsync(result, root, null, "failure", null, new CrashCaptureOptions());
                Require(value.State == "failed", "Invalid ZIP destination was reported as successful.");
                Require(value.Issues.Count > 0, "ZIP failure was not explained.");
            }
            finally { Directory.Delete(root, true); }
        });
    }
    private static string NewRoot() { var value = Path.Combine(Path.GetTempPath(),"crash-support-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(value); return value; }
    private static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
}
