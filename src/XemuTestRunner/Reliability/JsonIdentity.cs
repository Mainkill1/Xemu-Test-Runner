using System.Security.Cryptography;
using System.Text.Json;
using XemuTestRunner.Config;

namespace XemuTestRunner.Reliability;

internal static class JsonIdentity
{
    private static readonly JsonSerializerOptions Portable = Options("\n");
    private static readonly JsonSerializerOptions LegacyWindows = Options("\r\n");

    public static string Hash(object value) => Hash(value, Portable);

    public static bool Matches(object value, string expected) =>
        string.Equals(Hash(value), expected, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Hash(value, LegacyWindows), expected, StringComparison.OrdinalIgnoreCase);

    private static JsonSerializerOptions Options(string newLine) =>
        new(ConfigLoader.JsonOptions) { NewLine = newLine };

    private static string Hash(object value, JsonSerializerOptions options) =>
        Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(value, options))).ToLowerInvariant();
}
