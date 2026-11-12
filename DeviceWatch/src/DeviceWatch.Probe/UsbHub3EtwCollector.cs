using System.Runtime.InteropServices;
using System.Text;

namespace DeviceWatch.Probe;

internal sealed class UsbHub3EtwCollector : IDisposable
{
    private const string SessionName = "DeviceWatchProbeUsbHub3";
    private const string ProviderName = "Microsoft-Windows-USB-USBHUB3";
    private static readonly Guid ProviderId = new("ac52ad17-cc01-4f85-8df5-4dce4333c99b");
    private const ulong DefaultKeyword = 0x1;
    private const byte InformationLevel = 4;
    private const int PropertiesLength = 120;
    private const int PropertiesBufferLength = 1024;

    private readonly ProbeLog _log;
    private readonly EventRecordDelegate _eventRecord;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _opened = new(false);
    private IntPtr _properties;
    private IntPtr _loggerName;
    private IntPtr _logfileBuffer;
    private long _sessionHandle;
    private long _traceHandle = -1;
    private int _started;
    private int _disposed;
    private int _headerSizeLogged;

    public UsbHub3EtwCollector(ProbeLog log)
    {
        _log = log;
        _eventRecord = OnEventRecord;
        _thread = new Thread(ProcessLoop)
        {
            IsBackground = true,
            Name = "DeviceWatch.Probe.UsbHub3Etw",
        };
    }

    public void Start()
    {
        var logfileSize = Marshal.SizeOf<EventTraceLogfile>();
        var modeOffset = (int)Marshal.OffsetOf<EventTraceLogfile>(nameof(EventTraceLogfile.ProcessTraceMode));
        var callbackOffset = (int)Marshal.OffsetOf<EventTraceLogfile>(nameof(EventTraceLogfile.EventRecordCallback));
        var headerSize = Marshal.SizeOf<EventHeader>();
        var recordSize = Marshal.SizeOf<EventRecord>();
        if (logfileSize != 448 || modeOffset != 28 || callbackOffset != 424 || headerSize != 80 || recordSize != 112)
        {
            _log.Enqueue(RawEvent.Status("UsbHub3Etw", "layoutRejected", new Dictionary<string, object?>
            {
                ["eventTraceLogfile"] = logfileSize,
                ["processTraceModeOffset"] = modeOffset,
                ["eventRecordCallbackOffset"] = callbackOffset,
                ["eventHeader"] = headerSize,
                ["eventRecord"] = recordSize,
                ["expected"] = "logfile 448, mode 28, callback 424, header 80, record 112",
            }));
            _opened.Set();
            return;
        }

        _properties = CreateProperties();
        var start = StartTraceW(out _sessionHandle, SessionName, _properties);
        if (start == 183)
        {
            ControlTraceW(0, SessionName, _properties, 1);
            start = StartTraceW(out _sessionHandle, SessionName, _properties);
            _log.Enqueue(RawEvent.Status("UsbHub3Etw", "replacedExistingSession", new Dictionary<string, object?>
            {
                ["session"] = SessionName,
            }));
        }

        if (start != 0)
        {
            _log.Enqueue(RawEvent.Status("UsbHub3Etw", "startFailed", new Dictionary<string, object?>
            {
                ["api"] = "StartTraceW",
                ["session"] = SessionName,
                ["win32Error"] = start,
                ["provider"] = ProviderName,
                ["providerGuid"] = ProviderId.ToString("B"),
                ["matchAnyKeyword"] = "0x1",
                ["keywordNames"] = new[] { "Default" },
            }));
            _opened.Set();
            return;
        }

        var provider = ProviderId;
        var enable = EnableTraceEx2(_sessionHandle, ref provider, 1, InformationLevel, DefaultKeyword, 0, 0, IntPtr.Zero);
        _log.Enqueue(RawEvent.Status("UsbHub3Etw", enable == 0 ? "enabled" : "enableFailed", new Dictionary<string, object?>
        {
            ["api"] = "EnableTraceEx2",
            ["provider"] = ProviderName,
            ["providerGuid"] = ProviderId.ToString("B"),
            ["level"] = InformationLevel,
            ["levelName"] = "win:Informational",
            ["matchAnyKeyword"] = "0x1",
            ["keywordNames"] = new[] { "Default" },
            ["excludedKeywordNames"] = new[] { "HeadersBusTrace", "PartialDataBusTrace", "FullDataBusTrace", "StateMachine" },
            ["clock"] = "systemTime",
            ["win32Error"] = enable,
        }));
        if (enable != 0)
        {
            ControlTraceW(_sessionHandle, SessionName, _properties, 1);
            _sessionHandle = 0;
            _opened.Set();
            return;
        }

        _started = 1;
        _thread.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_started == 1)
        {
            _opened.Wait(TimeSpan.FromSeconds(5));
            if (_traceHandle != -1)
            {
                CloseTrace(_traceHandle);
            }

            if (_thread.IsAlive)
            {
                _thread.Join(TimeSpan.FromSeconds(5));
            }
        }

        if (_sessionHandle != 0 && _properties != IntPtr.Zero)
        {
            var stop = ControlTraceW(_sessionHandle, SessionName, _properties, 1);
            _log.Enqueue(RawEvent.Status("UsbHub3Etw", "stopped", new Dictionary<string, object?>
            {
                ["api"] = "ControlTraceW",
                ["win32Error"] = stop,
                ["eventsLost"] = Marshal.ReadInt32(_properties, 88),
                ["realTimeBuffersLost"] = Marshal.ReadInt32(_properties, 100),
            }));
        }

        if (_properties != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_properties);
            _properties = IntPtr.Zero;
        }

        if (_loggerName != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_loggerName);
            _loggerName = IntPtr.Zero;
        }

        if (_logfileBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_logfileBuffer);
            _logfileBuffer = IntPtr.Zero;
        }

        _opened.Dispose();
    }

    private void ProcessLoop()
    {
        try
        {
            var loggerName = new byte[2048];
            Encoding.Unicode.GetBytes(SessionName + "\0").CopyTo(loggerName, 0);
            _loggerName = Marshal.AllocHGlobal(loggerName.Length);
            Marshal.Copy(loggerName, 0, _loggerName, loggerName.Length);
            var logfile = new EventTraceLogfile
            {
                LoggerName = _loggerName,
                ProcessTraceMode = 0x10000100,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_eventRecord),
            };
            var logfileSize = Marshal.SizeOf<EventTraceLogfile>();
            _logfileBuffer = Marshal.AllocHGlobal(logfileSize);
            Marshal.StructureToPtr(logfile, _logfileBuffer, false);
            _traceHandle = OpenTraceW(_logfileBuffer);
            _opened.Set();
            if (_traceHandle is -1 or 0xFFFFFFFF)
            {
                _traceHandle = -1;
                _log.Enqueue(RawEvent.Status("UsbHub3Etw", "openFailed", new Dictionary<string, object?>
                {
                    ["api"] = "OpenTraceW",
                    ["win32Error"] = Marshal.GetLastWin32Error(),
                }));
                return;
            }

            var result = ProcessTrace([_traceHandle], 1, IntPtr.Zero, IntPtr.Zero);
            _log.Enqueue(RawEvent.Status("UsbHub3Etw", "processReturned", new Dictionary<string, object?>
            {
                ["api"] = "ProcessTrace",
                ["win32Error"] = result,
            }));
        }
        catch (Exception ex)
        {
            _opened.Set();
            _log.Enqueue(RawEvent.Status("UsbHub3Etw", "processFailed", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["detail"] = ex.Message,
            }));
        }
    }

    private void OnEventRecord(IntPtr recordPointer)
    {
        try
        {
            ReadEventRecord(recordPointer);
        }
        catch (Exception ex)
        {
            _log.Enqueue(RawEvent.Status("UsbHub3Etw", "recordFailed", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["detail"] = ex.Message,
            }));
        }
    }

    private void ReadEventRecord(IntPtr recordPointer)
    {
        var record = Marshal.PtrToStructure<EventRecord>(recordPointer);
        if (Interlocked.Exchange(ref _headerSizeLogged, 1) == 0)
        {
            _log.Enqueue(RawEvent.Status("UsbHub3Etw", "firstRecordHeader", new Dictionary<string, object?>
            {
                ["headerSize"] = record.Header.Size,
                ["headerType"] = record.Header.HeaderType,
            }));
        }

        var (observedAt, qpc) = Clock.Observe();
        DateTimeOffset? sourceTime = null;
        try
        {
            if (record.Header.TimeStamp > 0)
            {
                sourceTime = new DateTimeOffset(DateTime.FromFileTimeUtc(record.Header.TimeStamp), TimeSpan.Zero);
            }
        }
        catch (ArgumentException)
        {
            sourceTime = null;
        }

        var length = record.UserDataLength;
        byte[] payload = [];
        if (length > 0 && record.UserData != IntPtr.Zero)
        {
            payload = new byte[length];
            Marshal.Copy(record.UserData, payload, 0, length);
        }

        _log.Enqueue(new RawEvent
        {
            RecordType = "event",
            Source = "UsbHub3Etw",
            Action = "EVENT_" + record.Header.Id,
            ObservedAtUtc = observedAt,
            ObservedQpc = qpc,
            SourceTimeUtc = sourceTime,
            QueryDevice = false,
            Raw = new Dictionary<string, object?>
            {
                ["providerGuid"] = record.Header.ProviderId.ToString("B"),
                ["eventId"] = record.Header.Id,
                ["version"] = record.Header.Version,
                ["channel"] = record.Header.Channel,
                ["level"] = record.Header.Level,
                ["opcode"] = record.Header.Opcode,
                ["task"] = record.Header.Task,
                ["keyword"] = "0x" + record.Header.Keyword.ToString("X"),
                ["processId"] = record.Header.ProcessId,
                ["threadId"] = record.Header.ThreadId,
                ["rawTimestamp"] = record.Header.TimeStamp,
                ["timestampClock"] = "systemTime",
                ["userDataLength"] = length,
                ["userDataBase64"] = payload,
            },
        });
    }

    private static IntPtr CreateProperties()
    {
        var name = Encoding.Unicode.GetBytes(SessionName + "\0");
        var bytes = new byte[PropertiesBufferLength];
        Write(bytes, 0, PropertiesBufferLength);
        Write(bytes, 40, 2);
        Write(bytes, 44, 0x00020000);
        Write(bytes, 48, 64);
        Write(bytes, 52, 8);
        Write(bytes, 56, 32);
        Write(bytes, 64, 0x00000100);
        Write(bytes, 68, 1);
        Write(bytes, 116, PropertiesLength);
        Buffer.BlockCopy(name, 0, bytes, PropertiesLength, name.Length);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, memory, bytes.Length);
        return memory;
    }

    private static void Write(byte[] buffer, int offset, int value)
        => BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), value);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct EventTraceHeader
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public byte Type;
        public byte Level;
        public ushort Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public uint KernelTime;
        public uint UserTime;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct EventTrace
    {
        public EventTraceHeader Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 172)]
    private struct TimeZoneInformation
    {
        private int _bias;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct TraceLogfileHeader
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public uint StartBuffers;
        public uint PointerSize;
        public uint EventsLost;
        public uint CpuSpeedInMHz;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public TimeZoneInformation TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct EventTraceLogfile
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EventTrace CurrentEvent;
        public TraceLogfileHeader LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHeader
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
        public long ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventRecord
    {
        public EventHeader Header;
        public byte ProcessorNumber;
        public byte Alignment;
        public ushort LoggerId;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordDelegate(IntPtr eventRecord);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int StartTraceW(out long sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(long sessionHandle, string sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnableTraceEx2(
        long traceHandle,
        ref Guid providerId,
        uint controlCode,
        byte level,
        ulong matchAnyKeyword,
        ulong matchAllKeyword,
        uint timeout,
        IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern long OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(long[] handles, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(long traceHandle);
}
