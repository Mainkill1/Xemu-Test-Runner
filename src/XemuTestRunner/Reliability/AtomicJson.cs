using System.Text.Json;
using XemuTestRunner.Config;

namespace XemuTestRunner.Reliability;

public static class AtomicJson
{
    // A same-directory rename publishes only a complete JSON document. Flush asks
    // the OS to persist file contents; this is not a promise against every power loss.
    public static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, ConfigLoader.JsonOptions);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
