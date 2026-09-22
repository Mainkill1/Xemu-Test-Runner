using System.Runtime.InteropServices;
using System.Text.Json;

namespace XemuTestRunner.Workstation;

public sealed class WorkstationStateMonitor : IDisposable
{
    private const uint WmDestroy = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WmTimer = 0x0113;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmWtsSessionChange = 0x02B1;

    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int PbtPowerSettingChange = 0x8013;

    private const int WtsSessionLock = 0x7;
    private const int WtsSessionUnlock = 0x8;
    private const int NotifyForThisSession = 0;
    private const int DeviceNotifyWindowHandle = 0;

    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;

    private static readonly Guid ConsoleDisplayState =
        new("6fe69556-704a-47a0-8f24-c28d936fda47");
    private static readonly Guid SessionDisplayStatus =
        new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");

    private static readonly object WindowMapGate = new();
    private static readonly Dictionary<IntPtr, WorkstationStateMonitor> WindowMap = [];

    private readonly object _gate = new();
    private readonly Action<WorkstationStateSnapshot> _onState;
    private readonly string? _eventLogPath;
    private readonly WndProc _wndProc;
    private Thread? _thread;
    private IntPtr _window;
    private IntPtr _consolePowerNotification;
    private IntPtr _sessionPowerNotification;
    private StreamWriter? _eventWriter;
    private bool _disposed;
    private long _eventSequence;
    private WorkstationStateSnapshot _state = WorkstationStateSnapshot.Unsupported;

    public WorkstationStateMonitor(
        Action<WorkstationStateSnapshot> onState,
        string? eventLogPath = null)
    {
        _onState = onState;
        _eventLogPath = eventLogPath;
        _wndProc = StaticWndProc;
    }

    public WorkstationStateSnapshot Snapshot
    {
        get { lock (_gate) return _state; }
    }

    public void Start()
    {
        if (_thread is not null)
            return;

        if (!OperatingSystem.IsWindows())
        {
            Publish(WorkstationStateSnapshot.Unsupported with
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                LastEvent = "workstation monitoring is currently implemented for Windows only"
            }, writeEvent: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_eventLogPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_eventLogPath)!);
            _eventWriter = new StreamWriter(
                new FileStream(
                    _eventLogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    16384),
                new System.Text.UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "WorkstationStateMonitor"
        };
        _thread.Start();
    }

    private void MessageLoop()
    {
        var className = "XemuTestRunner.Workstation." + Guid.NewGuid().ToString("N");
        var module = GetModuleHandleW(null);
        var windowClass = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            Instance = module,
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            ClassName = className
        };

        if (RegisterClassExW(ref windowClass) == 0)
        {
            Publish(WindowsInitialState("RegisterClassEx failed: " + Marshal.GetLastWin32Error()), true);
            return;
        }

        try
        {
            _window = CreateWindowExW(
                0,
                className,
                "Xemu Test Runner Workstation Monitor",
                0,
                0, 0, 0, 0,
                IntPtr.Zero,
                IntPtr.Zero,
                module,
                IntPtr.Zero);

            if (_window == IntPtr.Zero)
            {
                Publish(WindowsInitialState("CreateWindowEx failed: " + Marshal.GetLastWin32Error()), true);
                return;
            }

            lock (WindowMapGate)
                WindowMap[_window] = this;

            _ = WTSRegisterSessionNotification(_window, NotifyForThisSession);
            var consoleDisplayState = ConsoleDisplayState;
            var sessionDisplayStatus = SessionDisplayStatus;
            _consolePowerNotification = RegisterPowerSettingNotification(
                _window,
                ref consoleDisplayState,
                DeviceNotifyWindowHandle);
            _sessionPowerNotification = RegisterPowerSettingNotification(
                _window,
                ref sessionDisplayStatus,
                DeviceNotifyWindowHandle);
            _ = SetTimer(_window, new UIntPtr(1), 1000, IntPtr.Zero);

            Publish(WindowsInitialState("monitor_started"), true);

            while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessageW(ref message);
            }
        }
        finally
        {
            if (_window != IntPtr.Zero)
            {
                _ = KillTimer(_window, new UIntPtr(1));
                _ = WTSUnRegisterSessionNotification(_window);
                if (_consolePowerNotification != IntPtr.Zero)
                    _ = UnregisterPowerSettingNotification(_consolePowerNotification);
                if (_sessionPowerNotification != IntPtr.Zero)
                    _ = UnregisterPowerSettingNotification(_sessionPowerNotification);

                lock (WindowMapGate)
                    WindowMap.Remove(_window);

                if (IsWindow(_window))
                    _ = DestroyWindow(_window);

                _window = IntPtr.Zero;
            }

            _ = UnregisterClassW(className, module);
        }
    }

    private static IntPtr StaticWndProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        WorkstationStateMonitor? monitor;
        lock (WindowMapGate)
            WindowMap.TryGetValue(hwnd, out monitor);

        return monitor is null
            ? DefWindowProcW(hwnd, message, wParam, lParam)
            : monitor.WindowProc(hwnd, message, wParam, lParam);
    }

    private IntPtr WindowProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        switch (message)
        {
            case WmTimer:
                RefreshDynamicState();
                return IntPtr.Zero;

            case WmWtsSessionChange:
                if (wParam.ToInt32() == WtsSessionLock)
                    SetSessionLocked(true, "session_locked");
                else if (wParam.ToInt32() == WtsSessionUnlock)
                    SetSessionLocked(false, "session_unlocked");
                return IntPtr.Zero;

            case WmPowerBroadcast:
                HandlePowerBroadcast(wParam.ToInt32(), lParam);
                return new IntPtr(1);

            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void HandlePowerBroadcast(int code, IntPtr data)
    {
        var now = DateTimeOffset.UtcNow;

        if (code == PbtApmSuspend)
        {
            Publish(Snapshot with
            {
                PowerState = "suspending",
                LastSuspendUtc = now,
                TimestampUtc = now,
                LastEvent = "system_suspend"
            }, true);
            return;
        }

        if (code is PbtApmResumeAutomatic or PbtApmResumeSuspend)
        {
            Publish(RefreshPower(Snapshot) with
            {
                PowerState = "awake",
                LastResumeUtc = now,
                TimestampUtc = now,
                LastEvent = "system_resume"
            }, true);
            return;
        }

        if (code != PbtPowerSettingChange || data == IntPtr.Zero)
            return;

        var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(data);
        if (setting.DataLength < sizeof(int))
            return;

        var value = Marshal.ReadInt32(data, Marshal.SizeOf<PowerBroadcastSetting>());
        if (setting.PowerSetting == ConsoleDisplayState ||
            setting.PowerSetting == SessionDisplayStatus)
        {
            var display = value switch
            {
                0 => "off",
                1 => "on",
                2 => "dimmed",
                _ => "unknown"
            };

            var current = Snapshot;
            if (!string.Equals(current.DisplayState, display, StringComparison.Ordinal))
            {
                Publish(current with
                {
                    DisplayState = display,
                    LastDisplayChangeUtc = DateTimeOffset.UtcNow,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    LastEvent = "display_" + display
                }, true);
            }
        }
    }

    private WorkstationStateSnapshot WindowsInitialState(string lastEvent)
    {
        var desktop = GetInputDesktopName();
        var locked = DesktopImpliesLocked(desktop);

        return RefreshPower(new WorkstationStateSnapshot
        {
            Supported = true,
            Platform = "windows",
            SessionLocked = locked,
            InputDesktop = desktop,
            DisplayState = "unknown",
            PowerState = "awake",
            TimestampUtc = DateTimeOffset.UtcNow,
            LastEvent = lastEvent
        }) with
        {
            UserIdleSeconds = GetUserIdleSeconds()
        };
    }

    private void RefreshDynamicState()
    {
        var current = Snapshot;
        var desktop = GetInputDesktopName();
        var locked = DesktopImpliesLocked(desktop);
        var idle = GetUserIdleSeconds();
        var next = RefreshPower(current) with
        {
            InputDesktop = desktop,
            UserIdleSeconds = idle,
            TimestampUtc = DateTimeOffset.UtcNow
        };

        var sessionChanged = locked.HasValue && locked != current.SessionLocked;
        if (sessionChanged)
        {
            next = next with
            {
                SessionLocked = locked,
                LastSessionChangeUtc = DateTimeOffset.UtcNow,
                LastEvent = locked == true ? "session_locked" : "session_unlocked"
            };
        }

        Publish(next, sessionChanged);
    }

    private void SetSessionLocked(bool locked, string eventName)
    {
        var current = Snapshot;
        Publish(current with
        {
            SessionLocked = locked,
            InputDesktop = GetInputDesktopName(),
            LastSessionChangeUtc = DateTimeOffset.UtcNow,
            TimestampUtc = DateTimeOffset.UtcNow,
            LastEvent = eventName
        }, true);
    }

    private static WorkstationStateSnapshot RefreshPower(
        WorkstationStateSnapshot state)
    {
        if (!GetSystemPowerStatus(out var power))
            return state;

        var source = power.ACLineStatus switch
        {
            0 => "battery",
            1 => "ac",
            _ => "unknown"
        };

        return state with
        {
            PowerSource = source,
            BatteryPercent = power.BatteryLifePercent <= 100
                ? power.BatteryLifePercent
                : null,
            BatterySaver = power.SystemStatusFlag == 1
        };
    }

    private static long? GetUserIdleSeconds()
    {
        var info = new LastInputInfo
        {
            Size = (uint)Marshal.SizeOf<LastInputInfo>()
        };
        if (!GetLastInputInfo(ref info))
            return null;

        var now = unchecked((uint)Environment.TickCount);
        return unchecked(now - info.Time) / 1000L;
    }

    private static string? GetInputDesktopName()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero)
            return null;

        try
        {
            _ = GetUserObjectInformationW(
                desktop,
                UoiName,
                IntPtr.Zero,
                0,
                out var needed);

            if (needed == 0 || needed > 4096)
                return null;

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetUserObjectInformationW(
                        desktop,
                        UoiName,
                        buffer,
                        needed,
                        out _))
                    return null;

                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseDesktop(desktop);
        }
    }

    private static bool? DesktopImpliesLocked(string? desktop)
    {
        if (desktop is null)
            return null;

        return !desktop.Equals("Default", StringComparison.OrdinalIgnoreCase);
    }

    private void Publish(
        WorkstationStateSnapshot snapshot,
        bool writeEvent)
    {
        if (writeEvent)
            snapshot = snapshot with { EventSequence = Interlocked.Increment(ref _eventSequence) };

        lock (_gate)
            _state = snapshot;

        _onState(snapshot);

        if (writeEvent && _eventWriter is not null)
        {
            lock (_gate)
            {
                _eventWriter.WriteLine(JsonSerializer.Serialize(snapshot));
                _eventWriter.Flush();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_window != IntPtr.Zero)
            _ = PostMessageW(_window, WmClose, IntPtr.Zero, IntPtr.Zero);

        if (_thread is not null &&
            !_thread.Join(TimeSpan.FromSeconds(3)))
        {
            // Background thread is allowed to end with the process if Windows
            // refuses delivery during shutdown.
        }

        lock (_gate)
        {
            _eventWriter?.Dispose();
            _eventWriter = null;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string? ClassName;
        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint MessageId;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(
        out Message message,
        IntPtr hwnd,
        uint min,
        uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(
        IntPtr hwnd,
        UIntPtr id,
        uint interval,
        IntPtr callback);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(
        IntPtr recipient,
        ref Guid powerSettingGuid,
        int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(
        uint flags,
        bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformationW(
        IntPtr handle,
        int index,
        IntPtr information,
        uint length,
        out uint needed);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(
        IntPtr hwnd,
        int flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
