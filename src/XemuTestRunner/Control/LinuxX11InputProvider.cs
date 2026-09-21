using System.Runtime.InteropServices;

namespace XemuTestRunner.Control;

public sealed class LinuxX11InputProvider : IXemuInputProvider
{
    private const int RevertToParent = 2;
    private readonly int _processId;
    private readonly IntPtr _display;
    private IntPtr _window;

    public string Name => "Linux X11/XTest";
    public bool IsAvailable => OperatingSystem.IsLinux() && _display != IntPtr.Zero;

    public LinuxX11InputProvider(int processId)
    {
        _processId = processId;

        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            return;

        try
        {
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display != IntPtr.Zero)
                _window = FindWindowForPid(_display, _processId);
        }
        catch (DllNotFoundException)
        {
            _display = IntPtr.Zero;
            _window = IntPtr.Zero;
        }
    }

    public async Task PressAsync(string hostKey, int holdMs, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Linux X11/XTest input is unavailable. DISPLAY, libX11 and libXtst are required and the xemu window must be visible.");

        if (_window == IntPtr.Zero)
            _window = FindWindowForPid(_display, _processId);
        if (_window == IntPtr.Zero)
            throw new InvalidOperationException($"Could not find an X11 xemu window for process {_processId}.");

        var keysymName = ToKeysymName(hostKey);
        var keysym = XStringToKeysym(keysymName);
        if (keysym == IntPtr.Zero)
            throw new InvalidDataException($"Unsupported X11 host key '{hostKey}'.");

        var keycode = XKeysymToKeycode(_display, keysym);
        if (keycode == 0)
            throw new InvalidOperationException($"X11 did not resolve a keycode for '{hostKey}'.");

        XRaiseWindow(_display, _window);
        XSetInputFocus(_display, _window, RevertToParent, UIntPtr.Zero);
        XFlush(_display);

        if (XTestFakeKeyEvent(_display, keycode, true, UIntPtr.Zero) == 0)
            throw new InvalidOperationException("XTest failed to inject key down.");
        XFlush(_display);

        await Task.Delay(holdMs, cancellationToken).ConfigureAwait(false);

        if (XTestFakeKeyEvent(_display, keycode, false, UIntPtr.Zero) == 0)
            throw new InvalidOperationException("XTest failed to inject key up.");
        XFlush(_display);
    }

    private static string ToKeysymName(string key) => key.Trim().ToLowerInvariant() switch
    {
        "enter" or "return" => "Return",
        "backspace" => "BackSpace",
        "up" => "Up",
        "down" => "Down",
        "left" => "Left",
        "right" => "Right",
        _ when key.Length == 1 => key,
        _ => throw new InvalidDataException($"Unsupported X11 host key '{key}'.")
    };

    private static IntPtr FindWindowForPid(IntPtr display, int pid)
    {
        var root = XDefaultRootWindow(display);
        var pidAtom = XInternAtom(display, "_NET_WM_PID", false);
        return FindWindowRecursive(display, root, pidAtom, pid);
    }

    private static IntPtr FindWindowRecursive(IntPtr display, IntPtr window, IntPtr pidAtom, int pid)
    {
        if (WindowHasPid(display, window, pidAtom, pid))
            return window;

        if (XQueryTree(display, window, out _, out _, out var children, out var count) == 0 || children == IntPtr.Zero)
            return IntPtr.Zero;

        try
        {
            for (uint i = 0; i < count; i++)
            {
                var child = Marshal.ReadIntPtr(children, checked((int)i * IntPtr.Size));
                var found = FindWindowRecursive(display, child, pidAtom, pid);
                if (found != IntPtr.Zero)
                    return found;
            }
        }
        finally
        {
            XFree(children);
        }

        return IntPtr.Zero;
    }

    private static bool WindowHasPid(IntPtr display, IntPtr window, IntPtr pidAtom, int expectedPid)
    {
        var status = XGetWindowProperty(
            display,
            window,
            pidAtom,
            IntPtr.Zero,
            new IntPtr(1),
            false,
            new IntPtr(6),
            out _,
            out var format,
            out var items,
            out _,
            out var property);

        if (status != 0 || property == IntPtr.Zero || items == UIntPtr.Zero)
            return false;

        try
        {
            if (format != 32)
                return false;

            var value = IntPtr.Size == 8 ? Marshal.ReadInt64(property) : Marshal.ReadInt32(property);
            return unchecked((int)value) == expectedPid;
        }
        finally
        {
            XFree(property);
        }
    }

    public void Dispose()
    {
        if (_display != IntPtr.Zero)
            XCloseDisplay(_display);
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern IntPtr XInternAtom(IntPtr display, string atomName, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [DllImport("libX11.so.6")]
    private static extern int XQueryTree(IntPtr display, IntPtr window, out IntPtr rootReturn, out IntPtr parentReturn, out IntPtr childrenReturn, out uint childCountReturn);

    [DllImport("libX11.so.6")]
    private static extern int XGetWindowProperty(
        IntPtr display,
        IntPtr window,
        IntPtr property,
        IntPtr longOffset,
        IntPtr longLength,
        [MarshalAs(UnmanagedType.Bool)] bool delete,
        IntPtr requestedType,
        out IntPtr actualTypeReturn,
        out int actualFormatReturn,
        out UIntPtr itemCountReturn,
        out UIntPtr bytesAfterReturn,
        out IntPtr propertyReturn);

    [DllImport("libX11.so.6")]
    private static extern int XFree(IntPtr data);

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern IntPtr XStringToKeysym(string value);

    [DllImport("libX11.so.6")]
    private static extern byte XKeysymToKeycode(IntPtr display, IntPtr keysym);

    [DllImport("libX11.so.6")]
    private static extern int XRaiseWindow(IntPtr display, IntPtr window);

    [DllImport("libX11.so.6")]
    private static extern int XSetInputFocus(IntPtr display, IntPtr focus, int revertTo, UIntPtr time);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);

    [DllImport("libXtst.so.6")]
    private static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, [MarshalAs(UnmanagedType.Bool)] bool isPress, UIntPtr delay);
}
