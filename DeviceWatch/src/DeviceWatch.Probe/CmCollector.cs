using System.Runtime.InteropServices;

namespace DeviceWatch.Probe;

internal sealed class CmCollector : IDisposable
{
    private readonly ProbeLog _log;
    private readonly List<IntPtr> _filters = [];
    private readonly List<IntPtr> _handles = [];
    private readonly CfgMgr.NotifyCallback _callback;
    private int _disposed;

    public CmCollector(ProbeLog log)
    {
        _log = log;
        _callback = OnNotify;
    }

    public bool RegistrationsSucceeded { get; private set; } = true;

    public void Start()
    {
        RegisterInstance();
        foreach (var (name, guid) in CfgMgr.InterfaceClasses)
        {
            RegisterInterface(name, guid);
        }
    }

    public void Snapshot()
    {
        var devices = CfgMgr.ListPresentDeviceIds();
        _log.Enqueue(RawEvent.Status("Snapshot", devices.ConfigRet == CfgMgr.Success ? "deviceList" : "deviceListFailed", new Dictionary<string, object?>
        {
            ["api"] = "CM_Get_Device_ID_List",
            ["flags"] = "CM_GETIDLIST_FILTER_PRESENT",
            ["configRet"] = devices.ConfigRet,
            ["configRetName"] = CfgMgr.ConfigRetName(devices.ConfigRet),
            ["count"] = devices.Items.Length,
        }));

        var (utc, qpc) = Clock.Observe();
        foreach (var instanceId in devices.Items)
        {
            _log.Enqueue(new RawEvent
            {
                RecordType = "snapshot",
                Source = "Snapshot",
                Action = "DEVICE",
                ObservedAtUtc = utc,
                ObservedQpc = qpc,
                InstanceId = instanceId,
                QueryDevice = true,
                Raw = new Dictionary<string, object?>
                {
                    ["api"] = "CM_Get_Device_ID_List",
                    ["instanceId"] = instanceId,
                },
            });
        }

        foreach (var (name, guid) in CfgMgr.InterfaceClasses)
        {
            var interfaces = CfgMgr.ListPresentInterfaces(guid);
            _log.Enqueue(RawEvent.Status("Snapshot", interfaces.ConfigRet == CfgMgr.Success ? "interfaceList" : "interfaceListFailed", new Dictionary<string, object?>
            {
                ["api"] = "CM_Get_Device_Interface_List",
                ["registrationName"] = name,
                ["classGuid"] = guid.ToString("B"),
                ["flags"] = "CM_GET_DEVICE_INTERFACE_LIST_PRESENT",
                ["configRet"] = interfaces.ConfigRet,
                ["configRetName"] = CfgMgr.ConfigRetName(interfaces.ConfigRet),
                ["count"] = interfaces.Items.Length,
            }));

            foreach (var path in interfaces.Items)
            {
                _log.Enqueue(new RawEvent
                {
                    RecordType = "snapshot",
                    Source = "Snapshot",
                    Action = "INTERFACE",
                    ObservedAtUtc = utc,
                    ObservedQpc = qpc,
                    InterfacePath = path,
                    QueryDevice = true,
                    Raw = new Dictionary<string, object?>
                    {
                        ["api"] = "CM_Get_Device_Interface_List",
                        ["registrationName"] = name,
                        ["classGuid"] = guid.ToString("B"),
                        ["interfacePath"] = path,
                    },
                });
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        foreach (var handle in _handles)
        {
            var result = CfgMgr.Unregister(handle);
            _log.Enqueue(RawEvent.Status("CmNotification", "unregistered", new Dictionary<string, object?>
            {
                ["configRet"] = result,
                ["configRetName"] = CfgMgr.ConfigRetName(result),
            }));
        }

        foreach (var filter in _filters)
        {
            Marshal.FreeHGlobal(filter);
        }

        _handles.Clear();
        _filters.Clear();
    }

    private void RegisterInstance()
    {
        var filter = CfgMgr.CreateDeviceInstanceFilter();
        _filters.Add(filter);
        var result = CfgMgr.Register(filter, _callback, out var handle);
        if (result == CfgMgr.Success)
        {
            _handles.Add(handle);
        }
        else
        {
            RegistrationsSucceeded = false;
        }

        _log.Enqueue(RawEvent.Status("CmDeviceInstance", result == CfgMgr.Success ? "registered" : "registerFailed", new Dictionary<string, object?>
        {
            ["api"] = "CM_Register_Notification",
            ["filterType"] = "DEVICEINSTANCE",
            ["flags"] = "CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES",
            ["cbSize"] = CfgMgr.FilterBytes,
            ["configRet"] = result,
            ["configRetName"] = CfgMgr.ConfigRetName(result),
        }));
    }

    private void RegisterInterface(string name, Guid classGuid)
    {
        var filter = CfgMgr.CreateInterfaceFilter(classGuid);
        _filters.Add(filter);
        var result = CfgMgr.Register(filter, _callback, out var handle);
        if (result == CfgMgr.Success)
        {
            _handles.Add(handle);
        }
        else
        {
            RegistrationsSucceeded = false;
        }

        _log.Enqueue(RawEvent.Status("CmDeviceInterface", result == CfgMgr.Success ? "registered" : "registerFailed", new Dictionary<string, object?>
        {
            ["api"] = "CM_Register_Notification",
            ["filterType"] = "DEVICEINTERFACE",
            ["registrationName"] = name,
            ["classGuid"] = classGuid.ToString("B"),
            ["cbSize"] = CfgMgr.FilterBytes,
            ["configRet"] = result,
            ["configRetName"] = CfgMgr.ConfigRetName(result),
        }));
    }

    private uint OnNotify(IntPtr notify, IntPtr context, int action, IntPtr eventData, int eventDataSize)
    {
        var (observedAt, qpc) = Clock.Observe();
        var copied = CopyBytes(eventData, eventDataSize);
        var (filterType, classGuid, text, missingNull) = CfgMgr.ReadNotifyPayload(eventData, eventDataSize);
        var isInterface = filterType == CfgMgr.NotifyTypeDeviceInterface;
        _log.Enqueue(new RawEvent
        {
            RecordType = "event",
            Source = isInterface ? "CmDeviceInterface" : "CmDeviceInstance",
            Action = CfgMgr.ActionName(action),
            ObservedAtUtc = observedAt,
            ObservedQpc = qpc,
            InstanceId = isInterface ? null : text,
            InterfacePath = isInterface ? text : null,
            QueryDevice = true,
            Raw = new Dictionary<string, object?>
            {
                ["api"] = "CM_Register_Notification",
                ["action"] = action,
                ["actionName"] = CfgMgr.ActionName(action),
                ["filterType"] = filterType,
                ["eventDataSize"] = eventDataSize,
                ["copiedBytes"] = copied.Length,
                ["truncated"] = eventDataSize > copied.Length,
                ["missingNullTerminator"] = missingNull,
                ["classGuid"] = classGuid?.ToString("B"),
                ["instanceId"] = isInterface ? null : text,
                ["interfacePath"] = isInterface ? text : null,
                ["eventDataBase64"] = copied,
            },
        });
        return 0;
    }

    private static byte[] CopyBytes(IntPtr source, int size)
    {
        if (source == IntPtr.Zero || size <= 0)
        {
            return [];
        }

        var length = Math.Min(size, 16 * 1024);
        var buffer = new byte[length];
        Marshal.Copy(source, buffer, 0, length);
        return buffer;
    }
}
