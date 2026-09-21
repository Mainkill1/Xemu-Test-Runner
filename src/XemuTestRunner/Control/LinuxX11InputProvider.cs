using System.Runtime.InteropServices;

namespace XemuTestRunner.Control;

public sealed class LinuxX11InputProvider : IXemuInputProvider
{
    private readonly int _pid;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _closed;
    private IntPtr _display;
    public string Name => "Linux X11/XTest";
    public bool IsAvailable => _display != IntPtr.Zero;
    // Keep the delegate rooted. A disappearing window should become an input
    // error, not Xlib's default process-exit behavior.
    private static readonly ErrorHandler IgnoreWindowError = (_, _) => 0;
    public LinuxX11InputProvider(int processId)
    {
        _pid = processId;
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))) return;
        try
        {
            if (XInitThreads() == 0) return;
            XSetErrorHandler(IgnoreWindowError);
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display != IntPtr.Zero && XTestQueryExtension(_display, out _, out _, out _, out _) == 0) Dispose();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { Dispose(); }
    }
    public async Task PressAsync(string hostKey, int holdMs, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operation.WaitAsync(linked.Token);
        try
        {
            cancellationToken = linked.Token;
            if (!IsAvailable) throw new InvalidOperationException("X11 input requires DISPLAY, libX11 and libXtst. Native Wayland is not supported.");
            var window = FindWindow(XDefaultRootWindow(_display), XInternAtom(_display, "_NET_WM_PID", false), 0);
            if (window == IntPtr.Zero) throw new InvalidOperationException("No X11 xemu window for the active PID.");
            var name = hostKey.Trim().ToLowerInvariant() switch
            {
                "enter" or "return" => "Return", "backspace" => "BackSpace", "up" => "Up", "down" => "Down", "left" => "Left", "right" => "Right",
                var s when s.Length == 1 => s, _ => throw new InvalidDataException("Unsupported X11 key.")
            };
            var keycode = XKeysymToKeycode(_display, XStringToKeysym(name));
            if (keycode == 0) throw new InvalidOperationException("X11 cannot resolve the configured key.");
            XRaiseWindow(_display, window); XSetInputFocus(_display, window, 2, UIntPtr.Zero); XSync(_display, false);
            XGetInputFocus(_display, out var focused, out _);
            if (focused != window) throw new InvalidOperationException("xemu did not receive input focus; no key was sent.");
            if (XTestFakeKeyEvent(_display, keycode, true, UIntPtr.Zero) == 0) throw new InvalidOperationException("XTest key down failed.");
            XFlush(_display);
            try { await Task.Delay(holdMs, cancellationToken); }
            finally
            {
                if (XTestFakeKeyEvent(_display, keycode, false, UIntPtr.Zero) == 0) throw new InvalidOperationException("XTest key up failed.");
                XFlush(_display);
            }
        }
        finally { _operation.Release(); }
    }
    private IntPtr FindWindow(IntPtr window, IntPtr pidAtom, int depth)
    {
        if (depth > 32) return IntPtr.Zero;
        var status = XGetWindowProperty(_display, window, pidAtom, IntPtr.Zero, new IntPtr(1), false, new IntPtr(6),
            out _, out var format, out var count, out _, out var property);
        try
        {
            if (status == 0 && property != IntPtr.Zero && format == 32 && count != UIntPtr.Zero)
            {
                var pid = IntPtr.Size == 8 ? Marshal.ReadInt64(property) : Marshal.ReadInt32(property);
                if (unchecked((int)pid) == _pid) return window;
            }
        }
        finally { if (property != IntPtr.Zero) XFree(property); }
        if (XQueryTree(_display, window, out _, out _, out var children, out var childCount) == 0 || children == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            for (uint i = 0; i < childCount; i++)
            {
                var found = FindWindow(Marshal.ReadIntPtr(children, checked((int)i * IntPtr.Size)), pidAtom, depth + 1);
                if (found != IntPtr.Zero) return found;
            }
        }
        finally { XFree(children); }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _lifetime.Cancel(); _operation.Wait();
        try { if (_display != IntPtr.Zero) XCloseDisplay(_display); _display = IntPtr.Zero; }
        finally { _operation.Release(); }
    }
    private delegate int ErrorHandler(IntPtr display, IntPtr error);
    [DllImport("libX11.so.6")] private static extern int XInitThreads();
    [DllImport("libX11.so.6")] private static extern IntPtr XSetErrorHandler(ErrorHandler handler);
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)] private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XQueryTree(IntPtr display, IntPtr window, out IntPtr root, out IntPtr parent, out IntPtr children, out uint count);
    [DllImport("libX11.so.6")] private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr offset, IntPtr length, bool delete,
        IntPtr requestedType, out IntPtr actualType, out int format, out UIntPtr count, out UIntPtr bytesAfter, out IntPtr value);
    [DllImport("libX11.so.6")] private static extern int XFree(IntPtr data);
    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)] private static extern IntPtr XStringToKeysym(string name);
    [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(IntPtr display, IntPtr keysym);
    [DllImport("libX11.so.6")] private static extern int XRaiseWindow(IntPtr display, IntPtr window);
    [DllImport("libX11.so.6")] private static extern int XSetInputFocus(IntPtr display, IntPtr window, int revertTo, UIntPtr time);
    [DllImport("libX11.so.6")] private static extern int XGetInputFocus(IntPtr display, out IntPtr focus, out int revertTo);
    [DllImport("libX11.so.6")] private static extern int XSync(IntPtr display, bool discard);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
    [DllImport("libXtst.so.6")] private static extern int XTestQueryExtension(IntPtr display, out int eventBase, out int errorBase, out int major, out int minor);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, bool down, UIntPtr delay);
}
