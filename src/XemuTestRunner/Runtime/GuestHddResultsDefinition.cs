using System.Text.Json.Serialization;

namespace XemuTestRunner.Runtime;

/// <summary>Opt-in extraction of the current attempt's guest result, never a general disk-management command.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class GuestHddResultsDefinition : IJsonOnDeserialized
{
    public string Image { get; set; } = "";
    public long PartitionOffsetBytes { get; set; }
    public long PartitionLengthBytes { get; set; }
    public string GuestPath { get; set; } = "xemu_perf_tests/results.txt";
    public string ExpectedResults { get; set; } = "";
    public string ExpectedResultsSha256 { get; set; } = "";
    public int MaximumResultBytes { get; set; } = 16 * 1024 * 1024;

    // Validate at API/config load time, not only after an expensive run.
    void IJsonOnDeserialized.OnDeserialized() => Validate();

    public void Validate()
    {
        foreach (var path in new[] { Image, GuestPath, ExpectedResults })
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Contains('\\') || path.Contains(':') ||
                path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Any(char.IsControl)))
                throw new InvalidDataException("Guest HDD paths must be non-empty relative forward-slash paths without traversal.");
        }
        if (PartitionOffsetBytes < 0 || PartitionOffsetBytes % 512 != 0 || PartitionLengthBytes < 8192 ||
            PartitionLengthBytes % 512 != 0 || PartitionOffsetBytes > (1L << 44) - PartitionLengthBytes)
            throw new InvalidDataException("Declare the actual FATX partition byte offset and length, aligned to 512 bytes.");
        if (MaximumResultBytes is < 1 or > 16 * 1024 * 1024)
            throw new InvalidDataException("MaximumResultBytes must be 1..16777216.");
        if (ExpectedResultsSha256 is null || ExpectedResultsSha256.Length != 64 || !ExpectedResultsSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("ExpectedResultsSha256 must pin the complete reference result file.");
    }
}
