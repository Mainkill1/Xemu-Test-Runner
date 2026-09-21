namespace XemuTestRunner.Control;

public interface IXemuInputProvider : IDisposable
{
    string Name { get; }
    bool IsAvailable { get; }
    Task PressAsync(string hostKey, int holdMs, CancellationToken cancellationToken);
}
