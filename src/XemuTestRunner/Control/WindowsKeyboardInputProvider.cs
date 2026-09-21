using System.Runtime.InteropServices;

namespace XemuTestRunner.Control;

public sealed class WindowsKeyboardInputProvider : IXemuInputProvider
{
    private readonly int _processId;
    public string Name => "Windows SendInput";
    public bool IsAvailable => OperatingSystem.IsWindows();

    public WindowsKeyboardInputProvider(int processId) => _processId = processId;

    public async Task PressAsync(string hostKey, int holdMs, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new PlatformNotSupportedException("Windows SendInput is only available on Windows.");

        var key = ResolveKey(hostKey);
        var window = FindMainWindow(_processId);
        if (window == IntPtr.Zero)
            throw new InvalidOperationException($"Could not find a visible xemu window for process {_processId}.");

        ShowWindow(window, 9);
        _ = SetForegroundWindow(window);
        await Task.Delay(25, cancellationToken).ConfigureAwait(false);

        SendKey(key, keyDown: true);
        await Task.Delay(holdMs, cancellationToken).ConfigureAwait(false);
        SendKey(key, keyDown: false);
    }

    private static void SendKey(KeySpec key, bool keyDown)
    {
        var flags = KeyEventF.Scancode;
        if (key.Extended)
            flags |= KeyEventF.ExtendedKey;
        if (!keyDown)
            flags |= KeyEventF.KeyUp;

        var input = new Input
        {
            Type = 1,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = 0,
                    ScanCode = key.ScanCode,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = UIntPtr.Zero
                }
            }
        };

        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException($"SendInput failed with Win32 error {Marshal.GetLastWin32Error()}.");
    }

    private static IntPtr FindMainWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            _ = GetWindowThreadProcessId(window, out var pid);
            if (pid == processId && IsWindowVisible(window))
            {
                found = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static KeySpec ResolveKey(string key) => key.Trim().ToLowerInvariant() switch
    {
        "a" => new(0x1E),
        "b" => new(0x30),
        "x" => new(0x2D),
        "y" => new(0x15),
        "e" => new(0x12),
        "s" => new(0x1F),
        "f" => new(0x21),
        "d" => new(0x20),
        "w" => new(0x11),
        "i" => new(0x17),
        "j" => new(0x24),
        "l" => new(0x26),
        "k" => new(0x25),
        "o" => new(0x18),
        "1" => new(0x02),
        "2" => new(0x03),
        "3" => new(0x04),
        "4" => new(0x05),
        "5" => new(0x06),
        "enter" or "return" => new(0x1C),
        "backspace" => new(0x0E),
        "up" => new(0x48, true),
        "down" => new(0x50, true),
        "left" => new(0x4B, true),
        "right" => new(0x4D, true),
        _ => throw new InvalidDataException($"Unsupported Windows host key '{key}'.")
    };

    public void Dispose() { }

    private readonly record struct KeySpec(ushort ScanCode, bool Extended = false);

    [Flags]
    private enum KeyEventF : uint
    {
        ExtendedKey = 0x0001,
        KeyUp = 0x0002,
        Scancode = 0x0008
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public KeyEventF Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, [In] Input[] inputs, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int command);
}
