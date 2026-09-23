namespace XemuTestRunner.Networking;

internal sealed partial class AgentJobStore
{
    public bool HasPendingOperations
    {
        get { lock (_gate) return _runningOperations.Count != 0 || _busy.Count != 0; }
    }
}
