using System.Text;
using System.Text.Json;

namespace XemuTestRunner.Reliability;

public sealed record RunEntry(string RunId, JsonElement? Result);
public sealed record ArtifactEntry(string Path, long Bytes);
public sealed record LogTail(long FileBytes, long Offset, int Bytes, string Text);

public sealed class EvidenceCatalog
{
    private readonly string _root;
    public EvidenceCatalog(string root) => _root = Path.GetFullPath(root);
    public IReadOnlyList<RunEntry> ListRuns(int limit)
    {
        if (!Directory.Exists(_root)) return [];
        return Directory.EnumerateDirectories(_root)
            .Where(p => !Path.GetFileName(p).StartsWith(".", StringComparison.Ordinal) && Path.GetFileName(p) != "_recovery")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 200))
            .Select(p => new RunEntry(Path.GetFileName(p), ReadResult(p))).ToArray();
    }
    public IReadOnlyList<ArtifactEntry> Artifacts(string runId)
    {
        var root = Resolve(runId, ".");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(runId);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden,
            IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        return Directory.EnumerateFiles(root, "*", options).Where(p => !Path.GetRelativePath(root, p).Split(Path.DirectorySeparatorChar).Any(x => x.StartsWith('.')))
            .Take(1000).Select(p => new ArtifactEntry(Path.GetRelativePath(root, p).Replace('\\', '/'), new FileInfo(p).Length)).ToArray();
    }
    public string Resolve(string runId, string relative)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId is "." or ".." || runId.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new InvalidDataException("Invalid run ID.");
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':')) throw new InvalidDataException("Invalid artifact path.");
        var root = Path.GetFullPath(Path.Combine(_root, runId));
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, cmp)) throw new InvalidDataException("Path escapes run evidence.");
        // Evidence downloads never follow a symlink/junction out to a different tree.
        for (var path = full; path is not null && path.Length >= root.Length; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked evidence paths are not supported.");
        return full;
    }
    public async Task<LogTail> TailAsync(string runId, string fileName, int count, CancellationToken ct)
    {
        if (fileName is not ("stdout.log" or "stderr.log" or "operator-events.jsonl")) throw new InvalidDataException("Unsupported log name.");
        await using var file = new FileStream(Resolve(runId, fileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous);
        var length = file.Length; var bytes = (int)Math.Min(Math.Clamp(count, 1, 65536), length); var offset = length - bytes;
        file.Seek(offset, SeekOrigin.Begin); var buffer = new byte[bytes];
        var read = 0;
        while (read < bytes) { var n = await file.ReadAsync(buffer.AsMemory(read), ct); if (n == 0) break; read += n; }
        // A tail may start partway through a UTF-8 character; replacement fallback
        // is intentional. The caller sees the exact byte offset for this slice.
        return new(length, offset, read, Encoding.UTF8.GetString(buffer, 0, read));
    }
    private static JsonElement? ReadResult(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "result.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(path)); return json.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}

public sealed record FileRange(long Start, long Length, bool Partial)
{
    public static FileRange Parse(string? header, long size)
    {
        if (header is null) return new(0, size, false);
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || size == 0 || header.Contains(',')) throw new InvalidDataException("Unsatisfiable range.");
        var pieces = header[6..].Split('-', 2);
        if (pieces.Length != 2) throw new InvalidDataException("Unsatisfiable range.");
        if (pieces[0].Length == 0 && long.TryParse(pieces[1], out var suffix) && suffix > 0)
        { var length = Math.Min(size, suffix); return new(size - length, length, true); }
        if (!long.TryParse(pieces[0], out var start) || start < 0 || start >= size) throw new InvalidDataException("Unsatisfiable range.");
        var end = size - 1;
        if (pieces[1].Length > 0 && (!long.TryParse(pieces[1], out end) || end < start)) throw new InvalidDataException("Unsatisfiable range.");
        end = Math.Min(end, size - 1);
        return new(start, end - start + 1, true);
    }
}
