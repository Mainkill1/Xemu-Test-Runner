using System.Security.Cryptography;
using System.Text.Json;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Runtime;

internal static class GuestHddResults
{
    public static GuestEvaluation Evaluate(GuestHddResultsDefinition definition, string package, string results,
        RuntimeMaterialization? runtime, CancellationToken ct)
    {
        var checks = new List<AssessmentCheck>();
        try
        {
            definition.Validate();
            var attempt = AttemptJournal.Read(package);
            if (attempt is null || attempt.Phase is not ("exited" or "finalized") ||
                attempt.RunId != Path.GetFileName(Path.GetFullPath(results).TrimEnd(Path.DirectorySeparatorChar)) ||
                AttemptJournal.RecoveryDecision(attempt, false, 0) == RecoveryAction.Hold)
                throw new InvalidDataException("guest_image_owned: extraction requires the matching attempt's confirmed stopped process.");
            if (runtime is null) throw new InvalidDataException("guest_runtime_missing: use a declared private RuntimeState image.");
            var file = runtime.Files.SingleOrDefault(item => item.Destination.Replace('\\', '/') == definition.Image)
                ?? throw new InvalidDataException("guest_image_undeclared: Image is not a materialized runtime file.");
            var image = Inside(runtime.Directory, definition.Image);
            var seed = Inside(package, file.Source.Replace('\\', '/'));
            var referencePath = Inside(package, definition.ExpectedResults);
            var reference = ReadBounded(referencePath, definition.MaximumResultBytes);
            if (!Hash(reference).Equals(definition.ExpectedResultsSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("guest_reference_mismatch: reference result SHA-256 does not match the pinned test.");
            using var seedDisk = GuestDiskReader.Open(seed, ct);
            var seedReader = new FatxResultReader(seedDisk, definition.PartitionOffsetBytes, definition.PartitionLengthBytes);
            if (seedReader.ReadFile(definition.GuestPath, definition.MaximumResultBytes) is not null)
                throw new InvalidDataException("guest_result_stale: seed contains the result path. Use a clean immutable seed; no disk is reformatted automatically.");
            using var disk = GuestDiskReader.Open(image, ct);
            var reader = new FatxResultReader(disk, definition.PartitionOffsetBytes, definition.PartitionLengthBytes);
            var raw = reader.ReadFile(definition.GuestPath, definition.MaximumResultBytes)
                ?? throw new InvalidDataException("guest_result_missing: the stopped guest did not write the configured result path.");
            var destination = Inside(results, "guest/results.txt");
            Preserve(destination, raw);
            AtomicJson.Write(Inside(results, "guest/extraction.json"), new
            {
                schemaVersion = 1, runId = attempt.RunId, image = definition.Image, format = disk.Format,
                guestPath = definition.GuestPath, bytes = raw.Length, sha256 = Hash(raw),
                referenceSha256 = Hash(reference), imageBytesRead = disk.BytesRead + seedDisk.BytesRead,
                freshness = "result-path-absent-from-private-seed", parser = "xemu-perf-schema-1"
            });
            // Preserve bytes and their extraction receipt BEFORE parsing; partial
            // or failed guest output remains downloadable as diagnostic evidence.
            var evaluation = GuestResultParser.Evaluate(raw, reference, ct);
            AtomicJson.Write(Inside(results, "guest/normalized-results.json"), new
            {
                schemaVersion = 1, runId = attempt.RunId, sourceSha256 = Hash(raw),
                records = evaluation.Records, checks = evaluation.Checks,
                measurements = evaluation.Measurements
            });
            checks.Add(new("guest_hdd_extraction", true, "evidence", "Read-only extraction retained byte-exact guest output and validated its pinned reference."));
            checks.AddRange(evaluation.Checks);
            return new(checks, evaluation.Measurements, evaluation.Records);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
            ArgumentException or InvalidOperationException or OverflowException or TimeoutException)
        {
            checks.Add(new("guest_hdd_extraction", false, "evidence", error.Message));
            return new(checks, [], []);
        }
    }

    private static string Inside(string root, string relative)
    {
        var result = RuntimeStateManager.ResolveInside(root, relative);
        for (var path = result; path is not null; path = Path.GetDirectoryName(path))
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked guest image/reference/output paths are not supported.");
        }
        return result;
    }
    private static byte[] ReadBounded(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw new InvalidDataException("Guest metadata exceeds the configured byte limit.");
        var data = new byte[checked((int)file.Length)];
        file.ReadExactly(data);
        return data;
    }
    private static void Preserve(string path, byte[] raw)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (!Hash(ReadBounded(path, 16 * 1024 * 1024)).Equals(Hash(raw), StringComparison.Ordinal))
                throw new InvalidDataException("guest_evidence_conflict: this attempt already has different extracted evidence.");
            return;
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(raw); output.Flush(true); }
            File.Move(temporary, path); // Never overwrite another attempt's evidence.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
