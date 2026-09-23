using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Diagnostics;

public static class DiagnosticArchive
{
    private sealed record Entry(string Path, long Bytes, string Sha256);
    private sealed record Omission(string Path, string Reason);
    private static readonly string[] RootFiles = ["result.json", "assessment.json", "job.json", "launch.json", "input-manifest.json",
        "host-inventory.json", "preflight.json", "stdout.log", "stderr.log", "metrics.csv", "operator-events.jsonl", "segments.jsonl"];

    public static async Task<DiagnosticBundle> CreateAsync(string resultDirectory, string package, string? executable,
        string runId, string? executableSha256, CrashCaptureOptions options)
    {
        if (!options.Enabled) return new("disabled", null, null, 0, 0, 0, null, "notRequested", []);
        var target = Path.Combine(resultDirectory, "diagnostics.zip");
        var partial = target + ".part";
        var entries = new List<Entry>(); var omissions = new List<Omission>(); var issues = new List<string>();
        var result = new DiagnosticBundle("failed", null, null, 0, 0, 0, null, "notRequested", []);
        using var deadline = new CancellationTokenSource(options.BundleTimeoutMs);
        var ct = deadline.Token;
        try
        {
            var sources = new List<string>(RootFiles.Where(name => File.Exists(Path.Combine(resultDirectory, name))));
            foreach (var root in new[] { "crash", "diagnostics" })
            {
                var directory = Path.Combine(resultDirectory, root);
                if (!Directory.Exists(directory)) continue;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                { omissions.Add(new(root, "linkedPath")); continue; }
                var pending = new Stack<(string Path, int Depth)>(); pending.Push((directory, 0));
                var visited = 0;
                while (pending.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var next = pending.Pop();
                    if (++visited > 2048) { omissions.Add(new(root, "enumerationLimit")); break; }
                    foreach (var path in Directory.EnumerateFileSystemEntries(next.Path))
                    {
                        ct.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(resultDirectory, path).Replace('\\', '/');
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { omissions.Add(new(relative, "linkedPath")); continue; }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (next.Depth >= 8) omissions.Add(new(relative, "depthLimit"));
                            else pending.Push((path, next.Depth + 1));
                        }
                        else if (path.EndsWith(".part", StringComparison.Ordinal) || path.EndsWith(".tmp", StringComparison.Ordinal))
                            omissions.Add(new(relative, "unfinishedArtifact"));
                        else sources.Add(relative);
                        if (sources.Count >= options.MaxBundleFiles * 4) { omissions.Add(new(root, "enumerationLimit")); pending.Clear(); break; }
                    }
                }
            }
            long total = 0;
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var relative in sources.Distinct(StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    var source = Path.Combine(resultDirectory, relative);
                    CrashCapture.RejectLinks(source, resultDirectory);
                    var length = new FileInfo(source).Length;
                    if (entries.Count >= options.MaxBundleFiles || length > options.MaxArtifactBytes || length > options.MaxBundleBytes - total)
                    { omissions.Add(new(relative, "sizeOrCountLimit")); continue; }
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
                    if (input.Length != length) throw new IOException("Bundle input changed: " + relative);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using var output = zip.CreateEntry(relative, CompressionLevel.Fastest).Open();
                    var buffer = new byte[65536]; long written = 0;
                    while (written < length)
                    {
                        var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - written)), ct).ConfigureAwait(false);
                        if (count == 0) throw new IOException("Bundle input truncated: " + relative);
                        await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, count); written += count;
                    }
                    if (input.Length != length) throw new IOException("Bundle input grew: " + relative);
                    entries.Add(new(relative, length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant())); total += length;
                }
                await using var manifest = zip.CreateEntry("bundle-manifest.json", CompressionLevel.Fastest).Open();
                await JsonSerializer.SerializeAsync(manifest, new { schema = 1, runId, executableSha256, complete = omissions.Count == 0,
                    included = entries, omitted = omissions.Take(1024).ToArray(), moreOmissions = Math.Max(0, omissions.Count - 1024) }, cancellationToken: ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(partial, target, false);
            string digest;
            await using (var input = File.OpenRead(target)) digest = Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false)).ToLowerInvariant();
            result = new(omissions.Count == 0 ? "captured" : "partial", "diagnostics.zip", digest, new FileInfo(target).Length,
                entries.Count, omissions.Count, null, "notRequested", []);
            if (executable is not null)
            {
                var original = JobDefinition.ResolveInsidePackage(package, executable);
                var mirror = original + "." + runId + ".diagnostics.zip";
                CrashCapture.RejectLinks(Path.GetDirectoryName(original)!, package);
                var mirrorPart = mirror + ".part";
                try
                {
                    await using (var input = File.OpenRead(target))
                    await using (var output = new FileStream(mirrorPart, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    {
                        await input.CopyToAsync(output, ct).ConfigureAwait(false);
                        await output.FlushAsync(ct).ConfigureAwait(false);
                    }
                    File.Move(mirrorPart, mirror, false);
                    result = result with { BesideExecutable = Path.GetRelativePath(package, mirror).Replace('\\', '/'), MirrorState = "captured" };
                }
                finally { try { File.Delete(mirrorPart); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }
        catch (OperationCanceledException)
        { issues.Add("bundle_deadline"); result = result with { State = result.Artifact is null ? "timedOut" : result.State, MirrorState = "unavailable" }; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        { issues.Add(CrashCapture.Clip(error.Message)); result = result with { MirrorState = "failed" }; }
        finally { try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        if (result.Artifact is null)
        {
            // A large/slow dump must not cost us the small explanation of what
            // happened. Retry only packaging a fixed, tiny metadata set, not a
            // test or collector. This has its own two-second ceiling.
            result = await CreateMetadataFallbackAsync(resultDirectory, runId, executableSha256, issues).ConfigureAwait(false);
            if (executable is not null && result.Artifact is not null)
            {
                using var fallbackDeadline = new CancellationTokenSource(2000);
                string? mirrorPart = null;
                try
                {
                    var mirror = JobDefinition.ResolveInsidePackage(package, executable) + "." + runId + ".diagnostics.zip";
                    mirrorPart = mirror + ".part";
                    CrashCapture.RejectLinks(Path.GetDirectoryName(mirror)!, package);
                    await using (var source = File.OpenRead(target))
                    await using (var output = new FileStream(mirrorPart, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    {
                        await source.CopyToAsync(output, fallbackDeadline.Token).ConfigureAwait(false);
                        await output.FlushAsync(fallbackDeadline.Token).ConfigureAwait(false);
                    }
                    File.Move(mirrorPart, mirror, false);
                    result = result with { BesideExecutable = Path.GetRelativePath(package, mirror).Replace('\\', '/'), MirrorState = "captured" };
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
                { issues.Add("metadata_mirror_failed"); result = result with { MirrorState = "failed" }; }
                finally { if (mirrorPart is not null) { try { File.Delete(mirrorPart); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
            }
        }
        result = result with { Issues = issues };
        try { AtomicJson.Write(Path.Combine(resultDirectory, "diagnostic-bundle.json"), result); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { result = result with { Issues = [.. issues, "bundle_receipt_write_failed"] }; }
        return result;
    }
    private static async Task<DiagnosticBundle> CreateMetadataFallbackAsync(string root, string runId, string? sha, List<string> issues)
    {
        var target = Path.Combine(root, "diagnostics.zip");
        var partial = target + ".fallback.part";
        using var deadline = new CancellationTokenSource(2000);
        var entries = new List<Entry>();
        var omitted = new List<Omission> { new("diagnostic payload", "primaryBundleFailed: metadata-only fallback") };
        try
        {
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var name in new[] { "crash/report.json", "crash/report.txt", "assessment.json", "result.json", "job.json", "launch.json", "input-manifest.json", "stderr.log" })
                {
                    var path = Path.Combine(root, name);
                    if (!File.Exists(path)) { omitted.Add(new(name, "unavailable")); continue; }
                    CrashCapture.RejectLinks(path, root);
                    if (new FileInfo(path).Length > 65536) { omitted.Add(new(name, "metadataSizeLimit")); continue; }
                    await using var input = File.OpenRead(path);
                    if (input.Length > 65536) throw new IOException("Metadata changed during fallback.");
                    var bytes = new byte[(int)input.Length];
                    await input.ReadExactlyAsync(bytes, deadline.Token).ConfigureAwait(false);
                    await using var output = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                    await output.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
                    entries.Add(new(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
                }
                await using var manifest = zip.CreateEntry("bundle-manifest.json").Open();
                await JsonSerializer.SerializeAsync(manifest, new { schema = 1, runId, executableSha256 = sha, complete = false,
                    included = entries, omitted, issues = issues.Take(12).ToArray() }, cancellationToken: deadline.Token).ConfigureAwait(false);
            }
            deadline.Token.ThrowIfCancellationRequested();
            File.Move(partial, target, overwrite: true);
            await using var verify = File.OpenRead(target);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(verify, deadline.Token).ConfigureAwait(false)).ToLowerInvariant();
            issues.Add("metadata_only_fallback");
            return new("partial", "diagnostics.zip", digest, verify.Length, entries.Count, omitted.Count, null, "notRequested", issues);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException or InvalidDataException)
        { issues.Add("metadata_fallback_failed:" + CrashCapture.Clip(error.Message)); return new("failed", null, null, 0, 0, omitted.Count, null, "notRequested", issues); }
        finally { try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

}
