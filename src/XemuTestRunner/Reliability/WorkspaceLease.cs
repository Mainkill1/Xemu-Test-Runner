using System.Text;

namespace XemuTestRunner.Reliability;

public sealed class WorkspaceLease : IDisposable
{
    private readonly FileStream _file;
    private WorkspaceLease(FileStream file) => _file = file;

    public static WorkspaceLease Acquire(string workspace)
    {
        Directory.CreateDirectory(workspace);
        FileStream file;
        try
        {
            file = new FileStream(Path.Combine(workspace, ".runner.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException("This workspace is already owned by another runner, or its lock cannot be opened.", ex);
        }
        try
        {
            file.SetLength(0);
            file.Write(Encoding.UTF8.GetBytes($"pid={Environment.ProcessId}\nhost={Environment.MachineName}\nstartedUtc={DateTimeOffset.UtcNow:O}\n"));
            file.Flush(true);
            return new WorkspaceLease(file);
        }
        catch { file.Dispose(); throw; }
    }

    // Keep the pathname in place. Deleting a locked pathname can let another
    // process create a new inode and bypass ownership on Unix.
    public void Dispose() => _file.Dispose();
}
