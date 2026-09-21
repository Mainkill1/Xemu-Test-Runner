using System.Runtime.InteropServices;

namespace XemuTestRunner.Control;

public sealed class WindowsKeyboardInputProvider : IXemuInputProvider
{
    private readonly int _pid;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _closed;
    public string Name => "Windows SendInput";
    public bool IsAvailable => OperatingSystem.IsWindows();
    public WindowsKeyboardInputProvider(int processId) => _pid = processId;
    public async Task PressAsync(string hostKey, int holdMs, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operation.WaitAsync(linked.Token);
        try
        {
            cancellationToken = linked.Token;
            if (!IsAvailable) throw new PlatformNotSupportedException();
            var key = Resolve(hostKey);
            IntPtr window = IntPtr.Zero;
            EnumWindows((h, unused) =>
            {
                GetWindowThreadProcessId(h, out var owner);
                if (owner == _pid && IsWindowVisible(h)) { window = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new InvalidOperationException("No visible xemu window for the active PID.");
            ShowWindow(window, 9); SetForegroundWindow(window);
            await Task.Delay(25, cancellationToken);
            GetWindowThreadProcessId(GetForegroundWindow(), out var focusedPid);
            if (focusedPid != _pid) throw new InvalidOperationException("Windows did not grant xemu foreground focus; input was not sent.");
            Send(key, true);
            try { await Task.Delay(holdMs, cancellationToken); }
            finally { Send(key, false); } // Cancellation must never leave the host key held.
        }
        finally { _operation.Release(); }
    }
    private static void Send(Key key, bool down)
    {
        var input = new Input { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput
        { ScanCode = key.ScanCode, Flags = 8u | (key.Extended ? 1u : 0u) | (down ? 0u : 2u) } } };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException($"SendInput failed: {Marshal.GetLastWin32Error()}.");
    }
    private static Key Resolve(string key) => key.Trim().ToLowerInvariant() switch
    {
        "a" => new(0x1e), "b" => new(0x30), "x" => new(0x2d), "y" => new(0x15),
        "e" => new(0x12), "s" => new(0x1f), "f" => new(0x21), "d" => new(0x20), "w" => new(0x11),
        "i" => new(0x17), "j" => new(0x24), "l" => new(0x26), "k" => new(0x25), "o" => new(0x18),
        "1" => new(2), "2" => new(3), "3" => new(4), "4" => new(5), "5" => new(6),
        "enter" or "return" => new(0x1c), "backspace" => new(0x0e),
        "up" => new(0x48, true), "down" => new(0x50, true), "left" => new(0x4b, true), "right" => new(0x4d, true),
        _ => throw new InvalidDataException("Unsupported host key: " + key)
    };
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _lifetime.Cancel(); _operation.Wait(); _operation.Release();
    }
    private readonly record struct Key(ushort ScanCode, bool Extended = false);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    // INPUT's union is sized by MOUSEINPUT, even when only keyboard input is used.
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort VirtualKey, ScanCode; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
}
