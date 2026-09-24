using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Runtime;

public sealed record DiskAssetManifest(
    int SchemaVersion,
    string Id,
    string Kind,
    long Length,
    string Sha256,
    string? Description,
    DateTimeOffset CreatedUtc);

public sealed class DiskAssetCatalog
{
    public const string ContentFileName = "disk.qcow2";
    private readonly string _root;

    public DiskAssetCatalog(string workspace)
    {
        _root = Path.Combine(Path.GetFullPath(workspace), "DiskAssets");
    }

    public string Root => _root;

    public static bool IsValidId(string? value) =>
        value is { Length: >= 1 and <= 64 } &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        char.IsAsciiLetterOrDigit(value[^1]) &&
        value.All(ch => (ch >= 'a' && ch <= 'z') || char.IsAsciiDigit(ch) || ch == '-');

    public static void ValidateKind(string kind)
    {
        if (kind is not ("xiso-seed" or "snapshot-carrier"))
            throw new InvalidDataException("Disk asset Kind must be xiso-seed or snapshot-carrier.");
    }

    public DiskAssetManifest CreateOrGet(
        string id, string kind, long length, string sha256, string? description, out bool created)
    {
        Validate(id, kind, length, sha256, description);
        RunStateInventory.NoLinks(_root);
        Directory.CreateDirectory(_root);
        var home = Home(id);
        var manifestPath = Path.Combine(home, "asset.json");
        if (File.Exists(manifestPath))
        {
            var existing = Read(manifestPath);
            created = false;
            if (existing.Id == id && existing.Kind == kind && existing.Length == length &&
                existing.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase) &&
                existing.Description == description)
                return existing;
            throw new InvalidOperationException("This disk asset ID already belongs to a different immutable definition.");
        }
        if (Directory.Exists(home) && Directory.EnumerateFileSystemEntries(home).Any())
            throw new InvalidOperationException("Disk asset directory exists without a valid immutable manifest.");
        Directory.CreateDirectory(home);
        var value = new DiskAssetManifest(1, id, kind, length, sha256.ToLowerInvariant(),
            description, DateTimeOffset.UtcNow);
        AtomicJson.Write(manifestPath, value);
        created = true;
        return value;
    }

    public DiskAssetManifest? TryGet(string id)
    {
        if (!IsValidId(id)) throw new InvalidDataException("Invalid disk asset ID.");
        var path = Path.Combine(Home(id), "asset.json");
        return File.Exists(path) ? Read(path) : null;
    }

    public DiskAssetManifest GetRequired(string id) =>
        TryGet(id) ?? throw new FileNotFoundException("Disk asset does not exist: " + id);

    public IReadOnlyList<DiskAssetManifest> List(int offset, int limit, out int? nextOffset)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        RunStateInventory.NoLinks(_root);
        if (!Directory.Exists(_root)) { nextOffset = null; return []; }
        var homes = Directory.EnumerateDirectories(_root)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Skip(offset).Take(limit + 1).ToArray();
        var values = homes.Take(limit).Select(path =>
        {
            RunStateInventory.NoLinks(path);
            var id = Path.GetFileName(path);
            if (!IsValidId(id)) throw new InvalidDataException("Catalog contains an invalid asset directory.");
            return Read(Path.Combine(path, "asset.json"));
        }).ToArray();
        nextOffset = homes.Length > limit ? offset + limit : null;
        return values;
    }

    public string ContentPath(string id)
    {
        var home = Home(id);
        RunStateInventory.NoLinks(home);
        return Path.Combine(home, ContentFileName);
    }

    public bool IsReady(DiskAssetManifest manifest)
    {
        var path = ContentPath(manifest.Id);
        if (!File.Exists(path)) return false;
        RunStateInventory.NoLinks(path);
        return new FileInfo(path).Length == manifest.Length;
    }

    public void Delete(string id)
    {
        var home = Home(id);
        RunStateInventory.NoLinks(home);
        if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
    }

    private string Home(string id)
    {
        if (!IsValidId(id)) throw new InvalidDataException("Invalid disk asset ID.");
        var path = Path.Combine(_root, id);
        RunStateInventory.NoLinks(path);
        return path;
    }

    private static DiskAssetManifest Read(string path)
    {
        RunStateInventory.NoLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Disk asset manifest is missing.", path);
        if (info.Length is < 2 or > 64 * 1024) throw new InvalidDataException("Disk asset manifest exceeds 64 KiB.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var value = JsonSerializer.Deserialize<DiskAssetManifest>(file, ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException("Disk asset manifest is empty.");
        Validate(value.Id, value.Kind, value.Length, value.Sha256, value.Description);
        if (value.SchemaVersion != 1) throw new InvalidDataException("Unsupported disk asset schema.");
        return value with { Sha256 = value.Sha256.ToLowerInvariant() };
    }

    private static void Validate(string id, string kind, long length, string sha256, string? description)
    {
        if (!IsValidId(id)) throw new InvalidDataException(
            "Disk asset IDs must be 1..64 lowercase letters, digits or hyphens and start/end with a letter or digit.");
        ValidateKind(kind);
        if (length is < 1 or > (1L << 44))
            throw new InvalidDataException("Disk asset Length must be between 1 byte and 16 TiB.");
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Disk asset Sha256 must be 64 hexadecimal characters.");
        if (description is { Length: > 240 } || description?.Any(char.IsControl) == true)
            throw new InvalidDataException("Disk asset Description must be at most 240 non-control characters.");
    }
}
