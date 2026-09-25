using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XisoSuiteRequest(string Id, string TestId, string Revision,
    string? Target = null, XisoSettings? Settings = null);
internal sealed record XisoSuiteData(string Id, string TestId, string TemplateRevision,
    string? Target, string Qualification, string IsoSha256, XisoCatalog Catalog,
    XisoSettings Settings, AgentTestDefinition Template);
internal sealed record XisoSuite(string Revision, XisoSuiteData Data, DateTimeOffset CreatedUtc);
internal sealed record XisoTarget(string Id, string Source, string IsoSha256, string CatalogId,
    int LeafCount, string Qualification);

internal sealed partial class AgentJobStore
{
    private readonly SemaphoreSlim _xisoRegistration = new(1, 1);
    private readonly object _xisoGate = new();
    private string XisoSuiteRoot => System.IO.Path.Combine(_paths.Pending, ".xiso-suites");
    internal static readonly XisoTarget[] XisoTargets = [new(
        "shader-pilot-51bc23d", "51bc23d3706dea771b76545594256376d8588364",
        "b944d317035779afb15b7f7a0d90b0e35dd93b2257addba78c073438979cbf14",
        "sha256:8307fde80201084ce39706e9490cbdf16cddfb1aa21fc19fecf34942fa2f3dd4", 160, "candidate")];

    public async Task<object> RegisterXisoSuiteAsync(XisoSuiteRequest request, CancellationToken ct)
    {
        if (!IsId(request.Id)) throw new InvalidDataException("Invalid XISO suite ID.");
        if (request.Target is not null && !XisoTargets.Any(x => x.Id == request.Target))
            throw new InvalidDataException("Unknown pinned XISO target. Read /api/v1/xiso-targets.");
        await _xisoRegistration.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var baked = ReadTest(request.TestId, request.Revision);
            var source = baked.Definition;
            using var lease = Reserve(source.SourceJobId);
            var location = Locate(source.SourceJobId);
            RequireStableSource(location.State);
            var job = JsonSerializer.Deserialize<JobDefinition>(JsonSerializer.Serialize(source.Job, ConfigLoader.JsonOptions), ConfigLoader.JsonOptions)!;
            var extraction = job.Workload.GuestHddResults ?? throw new InvalidDataException("The suite template needs GuestHddResults, partition geometry, and a pinned oracle file. Configure this once, not for every campaign.");
            if (job.SnapshotName is not null || job.StartPaused || job.Arguments.Contains("-snapshot", StringComparer.Ordinal))
                throw new InvalidDataException("XISO campaigns boot the disc normally and retain writes until extraction; no loadvm, StartPaused or -snapshot.");
            if (job.RuntimeState.Isolation is null || !job.Arguments.Contains("-config_path", StringComparer.Ordinal))
                throw new InvalidDataException("Use a managed template with a runner-authored effective xemu configuration.");
            if (job.RuntimeState.Xiso is not null || extraction.Xiso is not null)
                throw new InvalidDataException("Register a base template, not a generated campaign child.");
            var files = source.Files.ToList();
            var catalog = new DiskAssetCatalog(_paths.Workspace);
            var shared = job.RuntimeState.Isolation.ReadOnlyAssets ??= [];
            var dvd = shared.SingleOrDefault(x => x.Field == "dvd_path");
            DiskAssetManifest iso;
            if (dvd is not null)
            {
                iso = catalog.GetRequired(dvd.AssetId);
                if (iso.Kind != "readonly-input" || !iso.Sha256.Equals(dvd.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The shared DVD must match its pinned read-only asset.");
            }
            else
            {
                var declared = files.Where(x => x.Path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (declared.Length != 1) throw new InvalidDataException("The template must have one declared ISO or a shared dvd_path asset.");
                iso = await ImportXisoPayloadAsync(declared[0], location.Package, "readonly-input", ct).ConfigureAwait(false);
                RemovePackagedInput(declared[0].Path);
                shared.Add(new ReadOnlyAssetDefinition { Field = "dvd_path", AssetId = iso.Id, ExpectedSha256 = iso.Sha256 });
            }
            byte[] catalogBytes;
            using (var image = OpenVerifiedAsset(catalog, iso)) catalogBytes = XisoCatalogReader.Read(image);
            var suiteCatalog = XisoCatalog.Parse(catalogBytes);
            var target = XisoTargets.FirstOrDefault(x => x.Id == request.Target);
            if (target is not null && (target.IsoSha256 != iso.Sha256 || target.CatalogId != suiteCatalog.Id || target.LeafCount != suiteCatalog.Leaves.Length))
                throw new InvalidDataException("The uploaded XISO/catalog is not the selected target. Source names or current-checkout catalogs cannot substitute for artifact identity.");

            DiskAssetManifest disk;
            var diskReference = job.RuntimeState.DiskAssets.SingleOrDefault(x => x.Destination == extraction.Image);
            if (diskReference is not null)
            {
                disk = catalog.GetRequired(diskReference.AssetId);
                if (disk.Kind != "xiso-seed" || !disk.Sha256.Equals(diskReference.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("XISO campaigns require a pinned clean xiso-seed, not a snapshot carrier.");
                diskReference.Retention = "deleteAfterEvidence";
            }
            else
            {
                var runtimeFile = job.RuntimeState.Files.SingleOrDefault(x => x.Destination == extraction.Image)
                    ?? throw new InvalidDataException("Guest result Image must be a declared private runtime disk.");
                var sourcePath = RelativeInput(location.Package, runtimeFile.Source);
                var declared = files.Single(x => x.Path == sourcePath);
                disk = await ImportXisoPayloadAsync(declared, location.Package, "xiso-seed", ct).ConfigureAwait(false);
                job.RuntimeState.Files.Remove(runtimeFile);
                RemovePackagedInput(sourcePath);
                job.RuntimeState.DiskAssets.Add(new RuntimeDiskAssetDefinition {
                    AssetId = disk.Id, ExpectedSha256 = disk.Sha256, Destination = extraction.Image, Retention = "deleteAfterEvidence" });
            }
            using (var verified = OpenVerifiedAsset(catalog, disk)) { }
            _ = XisoPlanInjector.Inspect(catalog.ContentPath(disk.Id), extraction.PartitionOffsetBytes, extraction.PartitionLengthBytes, ct);
            // Read the pinned reference only during registration. Its fixed-work fields
            // provide the defaults; changing them later must not silently relabel an oracle.
            var referencePath = ResolveFile(location.Package, extraction.ExpectedResults);
            var referenceBytes = File.ReadAllBytes(referencePath);
            if (referenceBytes.Length > extraction.MaximumResultBytes || XisoData.Sha(referenceBytes) != extraction.ExpectedResultsSha256.ToLowerInvariant())
                throw new InvalidDataException("Pinned XISO reference bytes are unavailable or changed.");
            var defaults = (request.Settings ?? new XisoSettings()).Resolve(ReferenceSettings(referenceBytes));
            // CLI DVD overrides would defeat the single authoritative effective config.
            for (var i = job.Arguments.Count - 1; i >= 0; i--)
                if (job.Arguments[i] == "-dvd_path")
                {
                    if (i + 1 >= job.Arguments.Count) throw new InvalidDataException("Incomplete -dvd_path argument.");
                    job.Arguments.RemoveRange(i, 2);
                }
            var template = source with { Job = job, Files = files.ToArray(), BuildFiles = source.BuildFiles.Where(path => files.Any(x => x.Path == path)).ToArray() };
            var data = new XisoSuiteData(request.Id, request.TestId, baked.Revision, request.Target,
                target?.Qualification ?? "unverified", iso.Sha256, suiteCatalog, defaults, template);
            var revision = HashJson(data);
            lock (_xisoGate)
            {
                var path = XisoSuitePath(request.Id);
                if (File.Exists(path))
                {
                    var current = ReadXisoSuite(request.Id);
                    if (current.Revision != revision) throw Conflict("xiso_suite_conflict", "This suite name already pins different media, defaults or template.", "Register a new name; existing campaigns keep their original suite.");
                    return XisoSuiteView(current);
                }
                var value = new XisoSuite(revision, data, DateTimeOffset.UtcNow);
                AtomicJson.Write(path, value);
                return XisoSuiteView(value);
            }
            void RemovePackagedInput(string path)
            {
                if (source.BuildFiles.Contains(path, StringComparer.Ordinal)) throw new InvalidDataException("Fixed XISO media cannot be replaceable application slots.");
                files.RemoveAll(x => x.Path == path);
                job.RequiredFiles.RemoveAll(x => RelativeInput(location.Package, x) == path);
                job.Inputs.RemoveAll(x => RelativeInput(location.Package, x.Path) == path);
            }
        }
        finally { _xisoRegistration.Release(); }
    }

    private async Task<DiskAssetManifest> ImportXisoPayloadAsync(AgentFile file, string package, string kind, CancellationToken ct)
    {
        var catalog = new DiskAssetCatalog(_paths.Workspace);
        var id = (kind == "xiso-seed" ? "xs-hdd-" : "xs-iso-") + file.Sha256[..40].ToLowerInvariant();
        var manifest = catalog.CreateOrGet(id, kind, file.Length, file.Sha256, "XISO campaign shared input", out _);
        if (!catalog.IsReady(manifest))
        {
            await using var source = new FileStream(ResolveFile(package, file.Path), FileMode.Open, FileAccess.Read, FileShare.Read,
                _bufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length != file.Length) throw new InvalidDataException("Shared XISO source length changed.");
            await _uploads.ReceiveAsync(source, catalog.ContentPath(id), file.Length, null, file.Sha256, null, _bufferBytes, ct).ConfigureAwait(false);
        }
        return manifest;
    }
    private static FileStream OpenVerifiedAsset(DiskAssetCatalog catalog, DiskAssetManifest manifest)
    {
        var path = catalog.ContentPath(manifest.Id);
        RunStateInventory.NoLinks(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (file.Length != manifest.Length || Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant() != manifest.Sha256)
                throw new InvalidDataException("XISO catalog asset SHA-256/length mismatch.");
            file.Position = 0; return file;
        }
        catch { file.Dispose(); throw; }
    }
    private static XisoSettings ReferenceSettings(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var leaves = document.RootElement.EnumerateArray().Where(x => x.TryGetProperty("kind", out var kind) && kind.GetString() == "leaf").ToArray();
        if (leaves.Length == 0) return new XisoSettings().Resolve();
        var values = leaves.Select(x => new XisoSettings(
            x.TryGetProperty("warmup_iterations", out var w) ? w.GetInt32() : 0,
            x.TryGetProperty("measurement_iterations_multiplier", out var m) ? m.GetInt32() : 1,
            x.TryGetProperty("gpu_completion_mode", out var c) ? c.GetString() : "per_iteration")).Distinct().ToArray();
        if (values.Length != 1) throw new InvalidDataException("Reference has mixed work settings. Register separate suite templates instead of guessing defaults.");
        return values[0].Resolve();
    }
    private string XisoSuitePath(string id)
    {
        if (!IsId(id)) throw new InvalidDataException("Invalid suite ID.");
        RunStateInventory.NoLinks(XisoSuiteRoot);
        var path = System.IO.Path.Combine(XisoSuiteRoot, id + ".json");
        RunStateInventory.NoLinks(path); return path;
    }
    private XisoSuite ReadXisoSuite(string? id)
    {
        if (id is null)
        {
            var ids = Directory.Exists(XisoSuiteRoot) ? Directory.EnumerateFiles(XisoSuiteRoot, "*.json").Take(2).ToArray() : [];
            if (ids.Length != 1) throw new AgentRequestException(409, "xiso_suite_required", "Select a suite when zero or multiple suites are registered.", "Read /api/v1/xiso-suites; no implicit latest-version selection is made.");
            id = System.IO.Path.GetFileNameWithoutExtension(ids[0]);
        }
        var path = XisoSuitePath(id);
        if (!File.Exists(path)) throw new AgentRequestException(404, "xiso_suite_not_found", "Unknown XISO suite.", "Register a pinned base template first.");
        var value = ReadJson<XisoSuite>(path);
        if (value.Data.Id != id || HashJson(value.Data) != value.Revision) throw new InvalidDataException("Stored XISO suite identity mismatch.");
        return value;
    }
    private static object XisoSuiteView(XisoSuite suite) => new {
        id = suite.Data.Id, suite.Revision, suite.Data.Qualification, suite.Data.Target,
        suite.Data.IsoSha256, catalogId = suite.Data.Catalog.Id, catalogSha256 = suite.Data.Catalog.Sha256,
        leafCount = suite.Data.Catalog.Leaves.Length, groupCount = suite.Data.Catalog.Groups.Length,
        settings = suite.Data.Settings, categories = "/api/v1/xiso-suites/" + suite.Data.Id + "/categories",
        tests = "/api/v1/xiso-suites/" + suite.Data.Id + "/tests"
    };
    public object ListXisoSuites(int offset, int limit)
    {
        RunStateInventory.NoLinks(XisoSuiteRoot);
        var paths = Directory.Exists(XisoSuiteRoot) ? Directory.EnumerateFiles(XisoSuiteRoot, "*.json").Order(StringComparer.Ordinal).Skip(offset).Take(limit + 1).ToArray() : [];
        return new { items = paths.Take(limit).Select(path => XisoSuiteView(ReadXisoSuite(System.IO.Path.GetFileNameWithoutExtension(path)))).ToArray(), nextOffset = paths.Length > limit ? (int?)(offset + limit) : null };
    }
    public object DescribeXisoSuite(string id) => XisoSuiteView(ReadXisoSuite(id));
    public object XisoCategories(string id)
    {
        var catalog = ReadXisoSuite(id).Data.Catalog;
        return new { items = XisoCatalog.Categories.Select(category => new {
            id = category.Id, name = category.Name, count = catalog.Leaves.Count(x => x.Category == category.Id)
        }).Where(x => x.count > 0).ToArray() };
    }
    public object XisoTests(string id, string? category, string? query, int offset, int limit)
    {
        if (category is not null && !XisoCatalog.Categories.Any(x => x.Id == category)) throw new InvalidDataException("Unknown XISO category.");
        if (query?.Length > 96) throw new InvalidDataException("Test search exceeds 96 characters.");
        var found = ReadXisoSuite(id).Data.Catalog.Leaves.Where(x => (category is null || x.Category == category) &&
            (query is null || x.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Skip(offset).Take(limit + 1).ToArray();
        return new { items = found.Take(limit).ToArray(), nextOffset = found.Length > limit ? (int?)(offset + limit) : null };
    }
}
