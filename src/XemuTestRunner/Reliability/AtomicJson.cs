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
            Publish(temporary, path);
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

    private static void Publish(string temporary, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception error) when (
                OperatingSystem.IsWindows() && attempt < 6 &&
                error is IOException or UnauthorizedAccessException &&
                (error.HResult & 0xffff) is 5 or 32 or 33)
            {
                // Only retry publication of this already-written document.
                // Never repeat a job, upload, callback or validation operation.
                // Six delays total 630 ms; persistent access denial still fails
                // with the old complete destination and original error intact.
                Thread.Sleep(10 << attempt);
            }
        }
    }
}
