namespace XemuTestRunner.Util;

public static class PathGuard
{
    public static string ResolveFile(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var decoded = Uri.UnescapeDataString(relative).Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(decoded))
            throw new InvalidDataException("A file path is required.");

        var full = Path.GetFullPath(Path.Combine(rootFull, decoded));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, comparison))
            throw new UnauthorizedAccessException("Path escapes the configured file root.");

        return full;
    }
}
