using System.Runtime.InteropServices;
using System.Text;

namespace DeviceWatch.Probe;

internal static class CfgMgr
{
    internal const int Success = 0;
    internal const int BufferSmall = 0x1A;
    internal const int NoSuchValue = 0x25;

    internal const int FilterBytes = 16 + (MaxDeviceIdLen * 2);
    internal const int MaxDeviceIdLen = 200;

    internal const int NotifyTypeDeviceInterface = 0;
    internal const int NotifyTypeDeviceInstance = 2;
    internal const int NotifyAllDeviceInstances = 0x00000002;

    internal const int GetIdListPresent = 0x00000100;
    internal const int InterfaceListPresent = 0;
    internal const int LocateNormal = 0;
    internal const int LocatePhantom = 1;

    internal const uint DevPropTypeString = 0x12;
    internal const uint DevPropTypeStringList = 0x2012;
    internal const uint DevPropTypeInt32 = 6;
    internal const uint DevPropTypeUInt32 = 7;
    internal const uint DevPropTypeGuid = 13;
    internal const uint DevPropTypeNtStatus = 0x17;

    internal static readonly Guid UsbDevice = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    internal static readonly Guid UsbHub = new("F18A0E88-C30C-11D0-8815-00A0C906BED8");
    internal static readonly Guid UsbHostController = new("3ABF6F2D-71C4-462A-8A92-1E6861E6AF27");
    internal static readonly Guid Hid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    internal static readonly (string Name, Guid Guid)[] InterfaceClasses =
    [
        ("USB_DEVICE", UsbDevice),
        ("USB_HUB", UsbHub),
        ("USB_HOST_CONTROLLER", UsbHostController),
        ("HID", Hid),
    ];

    private static readonly DevPropKey InstanceIdKey = new("78c34fc8-104a-4aca-9ea4-524d52996e57", 256);
    private static readonly DevPropKey DeviceDescKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    private static readonly DevPropKey HardwareIdsKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 3);
    private static readonly DevPropKey ServiceKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 6);
    private static readonly DevPropKey ClassKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 9);
    private static readonly DevPropKey FriendlyNameKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    private static readonly DevPropKey EnumeratorNameKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 24);
    private static readonly DevPropKey ProblemCodeKey = new("4340a6c5-93fa-4706-972c-7b648008a5a7", 3);
    private static readonly DevPropKey ParentKey = new("4340a6c5-93fa-4706-972c-7b648008a5a7", 8);
    private static readonly DevPropKey ChildrenKey = new("4340a6c5-93fa-4706-972c-7b648008a5a7", 9);
    private static readonly DevPropKey ProblemStatusKey = new("4340a6c5-93fa-4706-972c-7b648008a5a7", 12);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint NotifyCallback(
        IntPtr notify,
        IntPtr context,
        int action,
        IntPtr eventData,
        int eventDataSize);

    internal static int Register(IntPtr filter, NotifyCallback callback, out IntPtr handle)
        => CM_Register_Notification(filter, IntPtr.Zero, callback, out handle);

    internal static int Unregister(IntPtr handle)
        => handle == IntPtr.Zero ? Success : CM_Unregister_Notification(handle);

    internal static IntPtr CreateDeviceInstanceFilter()
        => CreateFilter(NotifyTypeDeviceInstance, NotifyAllDeviceInstances, null);

    internal static IntPtr CreateInterfaceFilter(Guid classGuid)
        => CreateFilter(NotifyTypeDeviceInterface, 0, classGuid.ToByteArray());

    internal static ListResult ListPresentDeviceIds()
    {
        var lengthResult = CM_Get_Device_ID_List_Size(out var length, IntPtr.Zero, GetIdListPresent);
        if (lengthResult != Success)
        {
            return ListResult.Fail(lengthResult);
        }

        var buffer = new char[Math.Max(length, 1)];
        var listResult = CM_Get_Device_ID_List(IntPtr.Zero, buffer, (uint)buffer.Length, GetIdListPresent);
        if (listResult == BufferSmall)
        {
            lengthResult = CM_Get_Device_ID_List_Size(out length, IntPtr.Zero, GetIdListPresent);
            if (lengthResult != Success)
            {
                return ListResult.Fail(lengthResult);
            }

            buffer = new char[Math.Max(length, 1)];
            listResult = CM_Get_Device_ID_List(IntPtr.Zero, buffer, (uint)buffer.Length, GetIdListPresent);
        }

        if (listResult != Success)
        {
            return ListResult.Fail(listResult);
        }

        return ListResult.Ok(SplitMultiSz(buffer));
    }

    internal static ListResult ListPresentInterfaces(Guid classGuid)
    {
        var lengthResult = CM_Get_Device_Interface_List_Size(out var length, ref classGuid, IntPtr.Zero, InterfaceListPresent);
        if (lengthResult != Success)
        {
            return ListResult.Fail(lengthResult);
        }

        var buffer = new char[Math.Max(length, 1)];
        var listResult = CM_Get_Device_Interface_List(ref classGuid, IntPtr.Zero, buffer, (uint)buffer.Length, InterfaceListPresent);
        if (listResult == BufferSmall)
        {
            lengthResult = CM_Get_Device_Interface_List_Size(out length, ref classGuid, IntPtr.Zero, InterfaceListPresent);
            if (lengthResult != Success)
            {
                return ListResult.Fail(lengthResult);
            }

            buffer = new char[Math.Max(length, 1)];
            listResult = CM_Get_Device_Interface_List(ref classGuid, IntPtr.Zero, buffer, (uint)buffer.Length, InterfaceListPresent);
        }

        if (listResult != Success)
        {
            return ListResult.Fail(listResult);
        }

        return ListResult.Ok(SplitMultiSz(buffer));
    }

    internal static PropertyRead ReadInterfaceInstanceId(string interfacePath)
        => ReadInterfaceProperty(interfacePath, InstanceIdKey);

    internal static (int ConfigRet, string Flags, uint DevInst) Locate(string instanceId)
    {
        var normal = CM_Locate_DevNode(out var devInst, instanceId, LocateNormal);
        if (normal == Success)
        {
            return (normal, "NORMAL", devInst);
        }

        var phantom = CM_Locate_DevNode(out devInst, instanceId, LocatePhantom);
        if (phantom == Success)
        {
            return (phantom, "PHANTOM", devInst);
        }

        return (normal, "NONE", 0);
    }

    internal static (int ConfigRet, uint Status, uint Problem) ReadStatus(uint devInst)
    {
        var result = CM_Get_DevNode_Status(out var status, out var problem, devInst, 0);
        return (result, status, problem);
    }

    internal static PropertyRead ReadParent(uint devInst) => ReadNodeProperty(devInst, ParentKey);
    internal static PropertyRead ReadChildren(uint devInst) => ReadNodeProperty(devInst, ChildrenKey);
    internal static PropertyRead ReadHardwareIds(uint devInst) => ReadNodeProperty(devInst, HardwareIdsKey);
    internal static PropertyRead ReadDeviceDesc(uint devInst) => ReadNodeProperty(devInst, DeviceDescKey);
    internal static PropertyRead ReadFriendlyName(uint devInst) => ReadNodeProperty(devInst, FriendlyNameKey);
    internal static PropertyRead ReadEnumerator(uint devInst) => ReadNodeProperty(devInst, EnumeratorNameKey);
    internal static PropertyRead ReadClass(uint devInst) => ReadNodeProperty(devInst, ClassKey);
    internal static PropertyRead ReadService(uint devInst) => ReadNodeProperty(devInst, ServiceKey);
    internal static PropertyRead ReadProblemCode(uint devInst) => ReadNodeProperty(devInst, ProblemCodeKey);
    internal static PropertyRead ReadProblemStatus(uint devInst) => ReadNodeProperty(devInst, ProblemStatusKey);

    internal static string? ConfigRetName(int value) => Names.TryGetValue(value, out var name) ? name : null;

    internal static string ActionName(int action) => action switch
    {
        0 => "DEVICEINTERFACEARRIVAL",
        1 => "DEVICEINTERFACEREMOVAL",
        2 => "DEVICEQUERYREMOVE",
        3 => "DEVICEQUERYREMOVEFAILED",
        4 => "DEVICEREMOVEPENDING",
        5 => "DEVICEREMOVECOMPLETE",
        6 => "DEVICECUSTOMEVENT",
        7 => "DEVICEINSTANCEENUMERATED",
        8 => "DEVICEINSTANCESTARTED",
        9 => "DEVICEINSTANCEREMOVED",
        _ => "UNKNOWN",
    };

    internal static (int FilterType, Guid? ClassGuid, string? Text, bool MissingNull) ReadNotifyPayload(IntPtr eventData, int eventDataSize)
    {
        if (eventData == IntPtr.Zero || eventDataSize < 8)
        {
            return (-1, null, null, true);
        }

        var filterType = Marshal.ReadInt32(eventData, 0);
        if (filterType == NotifyTypeDeviceInstance)
        {
            var (text, missingNull) = ReadUtf16(IntPtr.Add(eventData, 8), eventDataSize - 8);
            return (filterType, null, text, missingNull);
        }

        if (filterType == NotifyTypeDeviceInterface && eventDataSize >= 24)
        {
            var guidBytes = new byte[16];
            Marshal.Copy(IntPtr.Add(eventData, 8), guidBytes, 0, 16);
            var (text, missingNull) = ReadUtf16(IntPtr.Add(eventData, 24), eventDataSize - 24);
            return (filterType, new Guid(guidBytes), text, missingNull);
        }

        return (filterType, null, null, true);
    }

    private static IntPtr CreateFilter(int filterType, int flags, byte[]? guidOrNothing)
    {
        var memory = Marshal.AllocHGlobal(FilterBytes);
        for (var offset = 0; offset < FilterBytes; offset++)
        {
            Marshal.WriteByte(memory, offset, 0);
        }

        Marshal.WriteInt32(memory, 0, FilterBytes);
        Marshal.WriteInt32(memory, 4, flags);
        Marshal.WriteInt32(memory, 8, filterType);
        if (guidOrNothing is { Length: 16 })
        {
            Marshal.Copy(guidOrNothing, 0, IntPtr.Add(memory, 16), 16);
        }

        return memory;
    }

    private static PropertyRead ReadNodeProperty(uint devInst, DevPropKey key)
        => ReadProperty((IntPtr buffer, ref uint size, out uint type) =>
        {
            var localKey = key;
            return CM_Get_DevNode_Property(devInst, ref localKey, out type, buffer, ref size, 0);
        });

    private static PropertyRead ReadInterfaceProperty(string interfacePath, DevPropKey key)
        => ReadProperty((IntPtr buffer, ref uint size, out uint type) =>
        {
            var localKey = key;
            return CM_Get_Device_Interface_Property(interfacePath, ref localKey, out type, buffer, ref size, 0);
        });

    private static PropertyRead ReadProperty(PropertyCall call)
    {
        uint size = 0;
        var result = call(IntPtr.Zero, ref size, out var propertyType);
        if (result == Success)
        {
            return new PropertyRead(result, propertyType, []);
        }

        if (result != BufferSmall || size == 0 || size > 1_048_576)
        {
            return new PropertyRead(result, propertyType, null);
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            result = call(buffer, ref size, out propertyType);
            if (result != Success)
            {
                return new PropertyRead(result, propertyType, null);
            }

            var copy = new byte[size];
            if (size > 0)
            {
                Marshal.Copy(buffer, copy, 0, (int)size);
            }

            return new PropertyRead(result, propertyType, copy);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string[] SplitMultiSz(char[] buffer)
    {
        var text = new string(buffer);
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static (string? Text, bool MissingNull) ReadUtf16(IntPtr pointer, int byteLength)
    {
        if (pointer == IntPtr.Zero || byteLength < 2)
        {
            return (null, true);
        }

        var charCount = byteLength / 2;
        var chars = new char[charCount];
        Marshal.Copy(pointer, chars, 0, charCount);
        var terminator = Array.IndexOf(chars, '\0');
        if (terminator >= 0)
        {
            return (new string(chars, 0, terminator), false);
        }

        return (new string(chars), true);
    }

    private delegate int PropertyCall(IntPtr buffer, ref uint size, out uint propertyType);

    [DllImport("CfgMgr32.dll", EntryPoint = "CM_Register_Notification")]
    private static extern int CM_Register_Notification(
        IntPtr filter,
        IntPtr context,
        NotifyCallback callback,
        out IntPtr notifyContext);

    [DllImport("CfgMgr32.dll", EntryPoint = "CM_Unregister_Notification")]
    private static extern int CM_Unregister_Notification(IntPtr notifyContext);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    private static extern int CM_Get_Device_ID_List_Size(out uint length, IntPtr filter, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_ListW")]
    private static extern int CM_Get_Device_ID_List(IntPtr filter, char[] buffer, uint length, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static extern int CM_Get_Device_Interface_List_Size(
        out uint length,
        ref Guid interfaceClassGuid,
        IntPtr deviceId,
        uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static extern int CM_Get_Device_Interface_List(
        ref Guid interfaceClassGuid,
        IntPtr deviceId,
        char[] buffer,
        uint length,
        uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    private static extern int CM_Locate_DevNode(out uint devInst, string deviceId, int flags);

    [DllImport("CfgMgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern int CM_Get_DevNode_Property(
        uint devInst,
        ref DevPropKey propertyKey,
        out uint propertyType,
        IntPtr propertyBuffer,
        ref uint propertyBufferSize,
        uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_PropertyW")]
    private static extern int CM_Get_Device_Interface_Property(
        string interfaceName,
        ref DevPropKey propertyKey,
        out uint propertyType,
        IntPtr propertyBuffer,
        ref uint propertyBufferSize,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public DevPropKey(string formatId, uint propertyId)
        {
            FormatId = new Guid(formatId);
            PropertyId = propertyId;
        }
    }

    private static readonly Dictionary<int, string> Names = new()
    {
        [0x00] = "CR_SUCCESS",
        [0x01] = "CR_DEFAULT",
        [0x02] = "CR_OUT_OF_MEMORY",
        [0x03] = "CR_INVALID_POINTER",
        [0x04] = "CR_INVALID_FLAG",
        [0x05] = "CR_INVALID_DEVNODE",
        [0x0D] = "CR_NO_SUCH_DEVNODE",
        [0x13] = "CR_FAILURE",
        [0x1A] = "CR_BUFFER_SMALL",
        [0x1E] = "CR_INVALID_DEVICE_ID",
        [0x1F] = "CR_INVALID_DATA",
        [0x24] = "CR_DEVICE_NOT_THERE",
        [0x25] = "CR_NO_SUCH_VALUE",
        [0x26] = "CR_WRONG_TYPE",
        [0x33] = "CR_ACCESS_DENIED",
        [0x34] = "CR_CALL_NOT_IMPLEMENTED",
        [0x35] = "CR_INVALID_PROPERTY",
        [0x37] = "CR_NO_SUCH_DEVICE_INTERFACE",
        [0x3B] = "CR_INVALID_STRUCTURE_SIZE",
    };
}

internal readonly struct ListResult
{
    public int ConfigRet { get; }
    public string[] Items { get; }

    private ListResult(int configRet, string[] items)
    {
        ConfigRet = configRet;
        Items = items;
    }

    public static ListResult Ok(string[] items) => new(CfgMgr.Success, items);
    public static ListResult Fail(int configRet) => new(configRet, []);
}

internal readonly struct PropertyRead
{
    public int ConfigRet { get; }
    public uint PropertyType { get; }
    public byte[]? Bytes { get; }

    public PropertyRead(int configRet, uint propertyType, byte[]? bytes)
    {
        ConfigRet = configRet;
        PropertyType = propertyType;
        Bytes = bytes;
    }

    public bool Success => ConfigRet == CfgMgr.Success && Bytes is not null;
}

internal static class PropertyText
{
    public static string? AsString(PropertyRead read)
    {
        if (!read.Success || read.Bytes is null || read.PropertyType != CfgMgr.DevPropTypeString)
        {
            return null;
        }

        return DecodeSingle(read.Bytes);
    }

    public static string[]? AsStringList(PropertyRead read)
    {
        if (!read.Success || read.Bytes is null)
        {
            return null;
        }

        if (read.PropertyType == CfgMgr.DevPropTypeStringList)
        {
            return Encoding.Unicode.GetString(read.Bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        if (read.PropertyType == CfgMgr.DevPropTypeString)
        {
            var single = DecodeSingle(read.Bytes);
            return single is null ? [] : [single];
        }

        return null;
    }

    public static uint? AsUInt32(PropertyRead read)
    {
        if (!read.Success || read.Bytes is not { Length: >= 4 })
        {
            return null;
        }

        if (read.PropertyType is CfgMgr.DevPropTypeUInt32 or CfgMgr.DevPropTypeInt32)
        {
            return BitConverter.ToUInt32(read.Bytes, 0);
        }

        return null;
    }

    public static int? AsNtStatus(PropertyRead read)
    {
        if (!read.Success || read.Bytes is not { Length: >= 4 } || read.PropertyType != CfgMgr.DevPropTypeNtStatus)
        {
            return null;
        }

        return BitConverter.ToInt32(read.Bytes, 0);
    }

    private static string DecodeSingle(byte[] bytes)
    {
        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }
}
