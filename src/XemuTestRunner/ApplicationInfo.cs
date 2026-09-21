using System.Reflection;

namespace XemuTestRunner;

public static class ApplicationInfo
{
    public static string DisplayVersion { get; } =
        typeof(ApplicationInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(ApplicationInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
