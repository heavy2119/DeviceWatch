using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace DeviceWatch.Probe;

internal sealed class KernelPnpCollector : IDisposable
{
    private static readonly string[] Channels =
    [
        "Microsoft-Windows-Kernel-PnP/Configuration",
        "Microsoft-Windows-Kernel-PnP/Device Management",
    ];

    private readonly ProbeLog _log;
    private readonly EvtSubscribeCallback _callback;
    private readonly List<IntPtr> _subscriptions = [];
    private readonly List<GCHandle> _contexts = [];
    private int _disposed;

    public KernelPnpCollector(ProbeLog log)
    {
        _log = log;
        _callback = OnEvent;
    }

    public void Start()
    {
        foreach (var channel in Channels)
        {
            var context = GCHandle.Alloc(channel);
            var subscription = EvtSubscribe(IntPtr.Zero, IntPtr.Zero, channel, "*", IntPtr.Zero, GCHandle.ToIntPtr(context), _callback, 1);
            if (subscription == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                context.Free();
                _log.Enqueue(RawEvent.Status("KernelPnpEventLog", "subscribeFailed", new Dictionary<string, object?>
                {
                    ["api"] = "EvtSubscribe",
                    ["channel"] = channel,
                    ["query"] = "*",
                    ["flags"] = "EvtSubscribeToFutureEvents",
                    ["win32Error"] = error,
                }));
                continue;
            }

            _subscriptions.Add(subscription);
            _contexts.Add(context);
            _log.Enqueue(RawEvent.Status("KernelPnpEventLog", "subscribed", new Dictionary<string, object?>
            {
                ["api"] = "EvtSubscribe",
                ["channel"] = channel,
                ["query"] = "*",
                ["flags"] = "EvtSubscribeToFutureEvents",
            }));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        foreach (var subscription in _subscriptions)
        {
            EvtClose(subscription);
        }

        foreach (var context in _contexts)
        {
            if (context.IsAllocated)
            {
                context.Free();
            }
        }

        _subscriptions.Clear();
        _contexts.Clear();
    }

    private uint OnEvent(int action, IntPtr userContext, IntPtr eventHandle)
    {
        var channel = "Microsoft-Windows-Kernel-PnP";
        if (userContext != IntPtr.Zero)
        {
            var handle = GCHandle.FromIntPtr(userContext);
            if (handle.Target is string name)
            {
                channel = name;
            }
        }

        if (action == 0)
        {
            _log.Enqueue(RawEvent.Status("KernelPnpEventLog", "subscriptionError", new Dictionary<string, object?>
            {
                ["channel"] = channel,
                ["win32Error"] = unchecked((int)eventHandle.ToInt64()),
            }));
            return 0;
        }

        var (observedAt, qpc) = Clock.Observe();
        var xml = RenderXml(eventHandle, out var renderError);
        DateTimeOffset? sourceTime = null;
        string? instanceId = null;
        string? eventId = null;
        if (xml is not null)
        {
            ParseOriginalFields(xml, out sourceTime, out instanceId, out eventId);
        }

        _log.Enqueue(new RawEvent
        {
            RecordType = "event",
            Source = "KernelPnpEventLog",
            Action = eventId is null ? "EVENT" : "EVENT_" + eventId,
            ObservedAtUtc = observedAt,
            ObservedQpc = qpc,
            SourceTimeUtc = sourceTime,
            InstanceId = instanceId,
            QueryDevice = !string.IsNullOrWhiteSpace(instanceId),
            Raw = new Dictionary<string, object?>
            {
                ["api"] = "EvtSubscribe",
                ["channel"] = channel,
                ["eventId"] = eventId,
                ["renderWin32Error"] = renderError,
                ["xml"] = xml,
            },
        });
        return 0;
    }

    private static string? RenderXml(IntPtr eventHandle, out int error)
    {
        error = 0;
        if (!EvtRender(IntPtr.Zero, eventHandle, 1, 0, IntPtr.Zero, out var needed, out _))
        {
            error = Marshal.GetLastWin32Error();
            if (error != 122 || needed <= 0 || needed > 1_048_576)
            {
                return null;
            }
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EvtRender(IntPtr.Zero, eventHandle, 1, needed, buffer, out var used, out _))
            {
                error = Marshal.GetLastWin32Error();
                return null;
            }

            error = 0;
            var text = Marshal.PtrToStringUni(buffer, used / 2) ?? "";
            return text.TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ParseOriginalFields(string xml, out DateTimeOffset? sourceTime, out string? instanceId, out string? eventId)
    {
        sourceTime = null;
        instanceId = null;
        eventId = null;
        try
        {
            var document = XDocument.Parse(xml);
            var time = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "TimeCreated")
                ?.Attribute("SystemTime")?.Value;
            if (DateTimeOffset.TryParse(time, out var parsed))
            {
                sourceTime = parsed.ToUniversalTime();
            }

            eventId = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "EventID")?.Value;
            instanceId = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "Data" &&
                element.Attribute("Name")?.Value == "DeviceInstanceId")?.Value;
        }
        catch (Exception ex)
        {
            eventId = "xmlParseFailed:" + ex.GetType().Name;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint EvtSubscribeCallback(int action, IntPtr userContext, IntPtr eventHandle);

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr EvtSubscribe(
        IntPtr session,
        IntPtr signalEvent,
        string channelPath,
        string query,
        IntPtr bookmark,
        IntPtr context,
        EvtSubscribeCallback callback,
        int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    private static extern bool EvtRender(
        IntPtr context,
        IntPtr fragment,
        int flags,
        int bufferSize,
        IntPtr buffer,
        out int bufferUsed,
        out int propertyCount);

    [DllImport("wevtapi.dll")]
    private static extern bool EvtClose(IntPtr handle);
}
