using System.Runtime.InteropServices;

namespace DeviceWatch.Probe;

internal sealed class DeviceChangeWindow : IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const uint DeviceNotifyWindowHandle = 0;
    private const int DbchDeviceTypeOffset = 4;
    private const int DeviceInterfaceGuidOffset = 12;
    private const int DeviceInterfaceNameOffset = 28;
    private const int DevTypDeviceInterface = 5;

    private readonly ProbeLog _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WndProcDelegate _wndProc;
    private readonly List<IntPtr> _notifications = [];
    private volatile bool _stop;
    private int _disposed;
    private uint _threadId;
    private IntPtr _className;

    public DeviceChangeWindow(ProbeLog log)
    {
        _log = log;
        _wndProc = WindowProc;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "DeviceWatch.Probe.DeviceChange",
        };
    }

    public bool Ready { get; private set; }

    public void Start()
    {
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            _log.Enqueue(RawEvent.Status("WmDeviceChange", "windowTimeout", new Dictionary<string, object?>
            {
                ["detail"] = "hidden window thread did not become ready within 10 seconds",
            }));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _stop = true;
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        }

        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        if (_className != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_className);
            _className = IntPtr.Zero;
        }

        _ready.Dispose();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        try
        {
            _className = Marshal.StringToHGlobalUni("DeviceWatchProbeWindow");
            var instance = GetModuleHandle(null);
            var windowClass = new WndClass
            {
                Style = 0,
                WindowProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                Instance = instance,
                ClassName = _className,
            };
            var atom = RegisterClassW(ref windowClass);
            if (atom == 0)
            {
                Fail("RegisterClassW", Marshal.GetLastWin32Error());
                return;
            }

            var hwnd = CreateWindowExW(
                0x08000080,
                _className,
                "DeviceWatch.Probe",
                0x80000000,
                0,
                0,
                1,
                1,
                IntPtr.Zero,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Fail("CreateWindowExW", Marshal.GetLastWin32Error());
                return;
            }

            var registrationsOk = true;
            foreach (var (name, guid) in CfgMgr.InterfaceClasses)
            {
                registrationsOk &= RegisterInterface(hwnd, name, guid);
            }

            Ready = registrationsOk;
            _log.Enqueue(RawEvent.Status("WmDeviceChange", registrationsOk ? "windowReady" : "windowPartial", new Dictionary<string, object?>
            {
                ["api"] = "RegisterDeviceNotificationW",
                ["window"] = "top-level hidden WS_POPUP",
                ["extendedStyle"] = "WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE",
            }));
            _ready.Set();

            var message = Marshal.AllocHGlobal(64);
            try
            {
                while (!_stop)
                {
                    var result = GetMessageW(message, IntPtr.Zero, 0, 0);
                    if (result <= 0)
                    {
                        break;
                    }

                    TranslateMessage(message);
                    DispatchMessageW(message);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(message);
            }

            foreach (var notification in _notifications)
            {
                UnregisterDeviceNotification(notification);
            }

            DestroyWindow(hwnd);
        }
        finally
        {
            _ready.Set();
        }
    }

    private bool RegisterInterface(IntPtr hwnd, string name, Guid classGuid)
    {
        var filter = new DeviceInterfaceBroadcast
        {
            Size = Marshal.SizeOf<DeviceInterfaceBroadcast>(),
            DeviceType = DevTypDeviceInterface,
            ClassGuid = classGuid,
        };
        var handle = RegisterDeviceNotificationW(hwnd, ref filter, DeviceNotifyWindowHandle);
        var error = handle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        if (handle != IntPtr.Zero)
        {
            _notifications.Add(handle);
        }

        _log.Enqueue(RawEvent.Status("WmDeviceChange", handle != IntPtr.Zero ? "registered" : "registerFailed", new Dictionary<string, object?>
        {
            ["api"] = "RegisterDeviceNotificationW",
            ["registrationName"] = name,
            ["classGuid"] = classGuid.ToString("B"),
            ["filterSize"] = filter.Size,
            ["win32Error"] = error,
        }));
        return handle != IntPtr.Zero;
    }

    private void Fail(string api, int error)
    {
        _log.Enqueue(RawEvent.Status("WmDeviceChange", "windowFailed", new Dictionary<string, object?>
        {
            ["api"] = api,
            ["win32Error"] = error,
        }));
        Ready = false;
        _ready.Set();
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmDeviceChange)
        {
            var (observedAt, qpc) = Clock.Observe();
            var raw = new Dictionary<string, object?>
            {
                ["api"] = "WM_DEVICECHANGE",
                ["wParam"] = unchecked((long)wParam),
                ["wParamName"] = DeviceChangeName(unchecked((int)wParam)),
            };
            string? interfacePath = null;
            if (lParam != IntPtr.Zero)
            {
                var size = Marshal.ReadInt32(lParam, 0);
                if (size is > 0 and <= 65536)
                {
                    var bytes = new byte[size];
                    Marshal.Copy(lParam, bytes, 0, size);
                    raw["dbch_size"] = size;
                    raw["dbch_devicetype"] = Marshal.ReadInt32(lParam, DbchDeviceTypeOffset);
                    raw["broadcastBase64"] = bytes;
                    if (Marshal.ReadInt32(lParam, DbchDeviceTypeOffset) == DevTypDeviceInterface && size >= DeviceInterfaceNameOffset)
                    {
                        var guidBytes = new byte[16];
                        Marshal.Copy(IntPtr.Add(lParam, DeviceInterfaceGuidOffset), guidBytes, 0, 16);
                        raw["classGuid"] = new Guid(guidBytes).ToString("B");
                        interfacePath = ReadUtf16(IntPtr.Add(lParam, DeviceInterfaceNameOffset), size - DeviceInterfaceNameOffset);
                        raw["interfacePath"] = interfacePath;
                    }
                }
                else
                {
                    raw["detail"] = "dbch_size was outside 1..65536";
                    raw["dbch_size"] = size;
                }
            }

            _log.Enqueue(new RawEvent
            {
                RecordType = "event",
                Source = "WmDeviceChange",
                Action = DeviceChangeName(unchecked((int)wParam)),
                ObservedAtUtc = observedAt,
                ObservedQpc = qpc,
                InterfacePath = interfacePath,
                QueryDevice = !string.IsNullOrWhiteSpace(interfacePath),
                Raw = raw,
            });
            return (IntPtr)1;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static string DeviceChangeName(int wParam) => wParam switch
    {
        0x0007 => "DBT_DEVNODES_CHANGED",
        0x0017 => "DBT_QUERYCHANGECONFIG",
        0x0018 => "DBT_CONFIGCHANGED",
        0x0019 => "DBT_CONFIGCHANGECANCELED",
        0x8000 => "DBT_DEVICEARRIVAL",
        0x8001 => "DBT_DEVICEQUERYREMOVE",
        0x8002 => "DBT_DEVICEQUERYREMOVEFAILED",
        0x8003 => "DBT_DEVICEREMOVEPENDING",
        0x8004 => "DBT_DEVICEREMOVECOMPLETE",
        0x8005 => "DBT_DEVICETYPESPECIFIC",
        0x8006 => "DBT_CUSTOMEVENT",
        _ => "WM_DEVICECHANGE_" + wParam.ToString("X"),
    };

    private static string ReadUtf16(IntPtr pointer, int byteLength)
    {
        if (byteLength < 2)
        {
            return "";
        }

        var chars = new char[byteLength / 2];
        Marshal.Copy(pointer, chars, 0, chars.Length);
        var end = Array.IndexOf(chars, '\0');
        return end >= 0 ? new string(chars, 0, end) : new string(chars);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public IntPtr WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public IntPtr MenuName;
        public IntPtr ClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInterfaceBroadcast
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public Guid ClassGuid;
        public short Name;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WndClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint extendedStyle,
        IntPtr className,
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
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageW(IntPtr message, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(IntPtr message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(IntPtr message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotificationW(IntPtr recipient, ref DeviceInterfaceBroadcast filter, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
