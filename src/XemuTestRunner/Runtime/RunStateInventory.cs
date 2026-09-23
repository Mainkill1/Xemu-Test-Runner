using System.Security.Cryptography;
using System.Text.Json;

namespace XemuTestRunner.Runtime;

public static class RunStateInventory
{
    public static async Task<RunStateSnapshot> CaptureAsync(string root, int maximumFiles,
        long maximumBytes, CancellationToken ct, IReadOnlyCollection<string>? topLevelNames = null)
    {
        if (maximumFiles < 1 || maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        var files = new List<RunStateFile>();
        var issues = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        var visited = 0;
        try
        {
            NoLinks(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("State directory is unavailable.");
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((Path.GetFullPath(root), 0));
            while (pending.Count != 0)
            {
                ct.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path))
                {
                    ct.ThrowIfCancellationRequested();
                    if (directory.Depth == 0 && topLevelNames is not null && !topLevelNames.Contains(Path.GetFileName(path))) continue;
                    if (++visited > maximumFiles * 4 + 256) { issues.Add("entry_limit"); pending.Clear(); break; }
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (relative.Length > 512) { issues.Add("path_limit"); continue; }
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { issues.Add("linked_path"); continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (directory.Depth >= 16) issues.Add("depth_limit");
                        else pending.Push((path, directory.Depth + 1));
                        continue;
                    }
                    if (files.Count >= maximumFiles) { issues.Add("file_limit"); pending.Clear(); break; }
                    var before = new FileInfo(path);
                    var length = before.Length;
                    var modified = before.LastWriteTimeUtc;
                    if (length > maximumBytes - total) { issues.Add("byte_limit"); pending.Clear(); break; }
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[65536];
                    long read = 0;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                        if (count == 0) break;
                        read += count;
                        if (read > length) throw new InvalidDataException("State file changed during inventory.");
                        hash.AppendData(buffer, 0, count);
                    }
                    var after = new FileInfo(path);
                    if (read != length || after.Length != length || after.LastWriteTimeUtc != modified)
                        throw new InvalidDataException("State file changed during inventory.");
                    files.Add(new(relative, length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
                    total += length;
                }
            }
        }
        catch (OperationCanceledException) { issues.Add("deadline_or_cancelled"); }
        catch (InvalidDataException) { issues.Add("invalid_or_changed_state"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { issues.Add("state_unreadable"); }
        var sorted = files.OrderBy(value => value.Path, StringComparer.Ordinal).ToArray();
        return new(issues.Count == 0, Hash(JsonSerializer.SerializeToUtf8Bytes(sorted)), total, sorted, issues.OrderBy(value => value).ToArray());
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked state paths are not supported.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    internal static async Task CopyAsync(string source, string target, long expectedLength, string expectedHash, CancellationToken ct)
    {
        NoLinks(source); NoLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (input.Length != expectedLength) throw new InvalidDataException("Seed file length changed.");
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long copied = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;
            copied += count;
            if (copied > expectedLength) throw new InvalidDataException("Seed file grew while copying.");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        if (copied != expectedLength || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Seed content identity changed while copying.");
    }
}
