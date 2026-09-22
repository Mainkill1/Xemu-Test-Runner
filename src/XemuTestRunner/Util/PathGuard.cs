namespace XemuTestRunner.Util;

public static class PathGuard
{
    public static string ResolveFile(string root, string relative) =>
        ResolveRelativePath(root, Uri.UnescapeDataString(relative));

    /// <summary>Resolves a literal path without following child symlinks or junctions.</summary>
    public static string ResolveRelativePath(string root, string relative, bool allowRoot = false)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("A non-empty relative path is required.");
        }

        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relative));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + Path.DirectorySeparatorChar;
        var isRoot = string.Equals(fullPath, rootPath, comparison);
        if ((!isRoot && !fullPath.StartsWith(prefix, comparison)) || (isRoot && !allowRoot))
        {
            throw new UnauthorizedAccessException("Path escapes the configured directory or names the directory itself.");
        }

        // The configured root may deliberately be on another volume. Child
        // links, however, could make an otherwise valid relative path escape it.
        for (var current = fullPath;
             current is not null && !string.Equals(current, rootPath, comparison);
             current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Linked files and directories are not supported: " + relative);
                }
            }
            catch (FileNotFoundException)
            {
                // A new destination need not exist yet; still inspect its parents.
            }
            catch (DirectoryNotFoundException)
            {
                // A new destination need not exist yet; still inspect its parents.
            }
        }

        return fullPath;
    }
}
