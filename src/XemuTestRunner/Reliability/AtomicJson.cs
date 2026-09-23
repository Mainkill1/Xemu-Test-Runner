using System.Text.Json;
using XemuTestRunner.Config;

namespace XemuTestRunner.Reliability;

public static class AtomicJson
{
    // A same-directory rename publishes only a complete JSON document. Flush
    // requests persistence; it is not a guarantee against every power loss.
    public static void Write(string path, object value)
    {
        var stage = "create directory";
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            stage = "write temporary file";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, ConfigLoader.JsonOptions);
                file.Flush(flushToDisk: true);
            }
            stage = "publish replacement";
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Atomic JSON {stage} failed for '{Path.GetFileName(path)}' ({error.GetType().Name}, 0x{error.HResult:X8}): {error.Message}", error);
        }
        finally
        {
            // Cleanup must not hide the original publication error.
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
