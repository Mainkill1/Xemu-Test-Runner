using System.Text.Json;

namespace XemuTestRunner.Runtime;

internal sealed record GuestRecordSummary(string Id, string Kind, bool Correct, int Samples,
    double? MeanUs, double? MedianUs, double? MinUs, double? MaxUs, double? P95Us);
internal sealed record GuestEvaluation(IReadOnlyList<AssessmentCheck> Checks,
    IReadOnlyList<ReportedMeasurement> Measurements, IReadOnlyList<GuestRecordSummary> Records);

/// <summary>
/// Adapter for xemu-perf-tests schema 1 results.txt. Match stable IDs, work
/// parameters and pinned functional hashes before making guest timings eligible.
/// Raw samples stay in the original result artifact; no host/guest clock mixing.
/// </summary>
internal static class GuestResultParser
{
    private static readonly string[] FixedFields = ["schema_version", "revision", "kind", "iterations", "sample_count",
        "measurement_iterations_multiplier", "warmup_iterations", "gpu_completion_mode"];
    private static readonly string[] HashFields = ["source_kat", "work_checksum", "result_checksum", "framebuffer_fnv1a64"];

    public static GuestEvaluation Evaluate(byte[] raw, byte[] reference, CancellationToken ct)
    {
        using var actualDocument = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
        using var expectedDocument = JsonDocument.Parse(reference, new JsonDocumentOptions { MaxDepth = 32 });
        var actual = Records(actualDocument.RootElement, ct);
        var expected = Records(expectedDocument.RootElement, ct);
        if (expected.Count == 0) throw new InvalidDataException("Guest reference contains no expected records.");
        var checks = new List<AssessmentCheck>();
        var metrics = new List<ReportedMeasurement>();
        var summaries = new List<GuestRecordSummary>();
        var sameSet = actual.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Keys);
        checks.Add(new("guest_record_set", sameSet, "evidence",
            $"Expected {expected.Count} stable IDs; received {actual.Count}. Missing {expected.Keys.Except(actual.Keys).Count()}, extra {actual.Keys.Except(expected.Keys).Count()}."));
        var sampleBudget = 1000000;
        foreach (var (id, baseline) in expected.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!actual.TryGetValue(id, out var candidate)) continue;
            var kind = Text(baseline, "kind");
            if (kind is not ("leaf" or "group")) throw new InvalidDataException("Unsupported guest record kind: " + kind);
            var baselineMetadata = Metadata(baseline);
            var candidateMetadata = Metadata(candidate);
            var correct = FixedFields.All(field => baseline.TryGetProperty(field, out var a) && candidate.TryGetProperty(field, out var b) && Scalar(a) == Scalar(b));
            correct &= NoFailure(baseline) && NoFailure(candidate) && NoFailure(baselineMetadata) && NoFailure(candidateMetadata);
            var checkedHashes = 0;
            foreach (var field in HashFields)
            {
                var expectedHash = Field(baseline, baselineMetadata, field);
                if (expectedHash is null) continue;
                if (field == "framebuffer_fnv1a64" && IsFalse(baselineMetadata, "framebuffer_comparison_eligible")) continue;
                checkedHashes++;
                var candidateHash = Field(candidate, candidateMetadata, field);
                correct &= candidateHash == expectedHash;
                if (field == "framebuffer_fnv1a64" && IsFalse(candidateMetadata, "framebuffer_comparison_eligible")) correct = false;
            }
            // Group records prove structural completion, not independent work or
            // timing. A leaf with no reference oracle is explicitly unverified.
            if (kind == "leaf" && checkedHashes == 0) correct = false;
            checks.Add(new("guest:" + id, correct, "correctness", correct
                ? $"Matched work contract and {checkedHashes} applicable reference hashes."
                : "Guest outcome, fixed work, or reference hash mismatch; missing oracles are not a pass."));
            if (kind == "group")
            {
                summaries.Add(new(id, kind, correct, 0, null, null, null, null, null));
                continue;
            }
            if (!candidate.TryGetProperty("raw_results", out var samples) || samples.ValueKind != JsonValueKind.Array ||
                !candidate.TryGetProperty("sample_count", out var declared) || !declared.TryGetInt32(out var count) ||
                count < 1 || count > 262144 || count != samples.GetArrayLength() || (sampleBudget -= count) < 0)
                throw new InvalidDataException("Missing, excessive or inconsistent timing samples for " + id);
            var values = new double[count];
            var i = 0;
            foreach (var sample in samples.EnumerateArray())
            {
                if (sample.ValueKind != JsonValueKind.Number || !sample.TryGetDouble(out var number) ||
                    !double.IsFinite(number) || number < 0 || number > 9e15)
                    throw new InvalidDataException("Invalid guest timing sample for " + id);
                values[i++] = number;
            }
            Array.Sort(values);
            var mean = values.Average();
            var median = values.Length % 2 == 1 ? values[count / 2] : values[count / 2 - 1] / 2 + values[count / 2] / 2;
            var p95 = values[Math.Max(0, (int)Math.Ceiling(count * 0.95) - 1)];
            summaries.Add(new(id, kind, correct, count, mean, median, values[0], values[^1], p95));
            // Preserve all rows in normalized evidence, but emit no metrics for
            // an incorrect leaf. The overall assessment blocks failed cohorts.
            if (!correct || !sameSet) continue;
            var prefix = "xiso/" + id.Replace('.', '/') + "/";
            foreach (var (name, value) in new[] { ("mean_us", mean), ("median_us", median), ("min_us", values[0]), ("max_us", values[^1]), ("p95_us", p95) })
                metrics.Add(new(prefix + name, value, "us", "lower", "result:guest/results.txt#" + id));
        }
        return new(checks, metrics, summaries);
    }

    private static Dictionary<string, JsonElement> Records(JsonElement root, CancellationToken ct)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 512)
            throw new InvalidDataException("Guest schema 1 requires a complete array of at most 512 records.");
        RejectDuplicates(root, ct);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var record in root.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("schema_version", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1)
                throw new InvalidDataException("Guest result has an unsupported or missing schema_version.");
            var id = Text(record, "id");
            if (id.Length is < 1 or > 96 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ||
                id.StartsWith('.') || id.EndsWith('.') || id.Contains("..", StringComparison.Ordinal) || !values.TryAdd(id, record))
                throw new InvalidDataException("Invalid or duplicate guest stable ID.");
        }
        return values;
    }
    private static void RejectDuplicates(JsonElement value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate guest JSON property: " + property.Name);
                RejectDuplicates(property.Value, ct);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item, ct);
    }
    private static string Text(JsonElement value, string property) => value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
        ? item.GetString()! : throw new InvalidDataException("Guest result is missing " + property);
    private static string? Scalar(JsonElement value) => value.ValueKind is JsonValueKind.String ? value.GetString()?.Trim().ToLowerInvariant() :
        value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? value.GetRawText() : null;
    private static string? Field(JsonElement record, JsonElement metadata, string name)
    {
        if (record.TryGetProperty(name, out var value)) return Scalar(value);
        return metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(name, out value) ? Scalar(value) : null;
    }
    private static JsonElement Metadata(JsonElement record)
    {
        if (!record.TryGetProperty("metadata", out var metadata)) return default;
        if (metadata.ValueKind == JsonValueKind.Object) return metadata;
        if (metadata.ValueKind != JsonValueKind.String) throw new InvalidDataException("Guest metadata must be an object or encoded object.");
        using var document = JsonDocument.Parse(metadata.GetString()!, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Guest metadata is not an object.");
        RejectDuplicates(document.RootElement, CancellationToken.None);
        return document.RootElement.Clone();
    }
    private static bool IsFalse(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.False;
    private static bool NoFailure(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return true;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name is "outcome" or "oracle_status")
            {
                if (property.Value.ValueKind != JsonValueKind.String || !string.Equals(property.Value.GetString(), "PASS", StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (property.Value.ValueKind == JsonValueKind.Object && !NoFailure(property.Value)) return false;
        }
        return true;
    }
}
