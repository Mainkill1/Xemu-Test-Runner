namespace XemuTestRunner.Networking;

internal sealed partial class AgentJobStore
{
    public FileStream OpenFile(string id, string relative)
    {
        var document = ReadDocument(id);
        _ = FindFile(document, relative);
        var path = ResolveFile(Locate(id).Package, relative);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            _bufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
}
