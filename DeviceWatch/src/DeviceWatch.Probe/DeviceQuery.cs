using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeviceWatch.Probe;

internal sealed class DeviceFacts
{
    public string? InstanceId { get; init; }

    public JsonObject Queried { get; init; } = new();

    public string? ParentInstanceId { get; init; }

    public bool ParentKnown { get; init; }

    public string? Name { get; init; }

    public string? NameSource { get; init; }

    public string? Vid { get; init; }

    public string? Pid { get; init; }

    public bool VidPidKnown { get; init; }
}

internal static class DeviceQuery
{
    private static readonly Regex VidPattern = new(@"VID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PidPattern = new(@"PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static DeviceFacts Query(string? instanceId, string? interfacePath)
    {
        var queried = new JsonObject();
        string? resolvedInstanceId = instanceId;

        if (!string.IsNullOrWhiteSpace(interfacePath))
        {
            var instanceRead = CfgMgr.ReadInterfaceInstanceId(interfacePath);
            queried["interfaceInstanceId"] = Describe(instanceRead, StringValue(instanceRead));
            if (string.IsNullOrWhiteSpace(resolvedInstanceId))
            {
                resolvedInstanceId = PropertyText.AsString(instanceRead);
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedInstanceId))
        {
            return new DeviceFacts
            {
                Queried = queried,
            };
        }

        var (locateRet, locateFlags, devInst) = CfgMgr.Locate(resolvedInstanceId);
        var locate = new JsonObject
        {
            ["success"] = locateRet == CfgMgr.Success,
            ["configRet"] = locateRet,
            ["flags"] = locateFlags,
        };
        AddName(locate, locateRet);
        queried["devNode"] = locate;

        if (locateRet != CfgMgr.Success)
        {
            return new DeviceFacts
            {
                InstanceId = resolvedInstanceId,
                Queried = queried,
            };
        }

        var (statusRet, status, problem) = CfgMgr.ReadStatus(devInst);
        var statusNode = new JsonObject
        {
            ["success"] = statusRet == CfgMgr.Success,
            ["configRet"] = statusRet,
            ["api"] = "CM_Get_DevNode_Status",
        };
        AddName(statusNode, statusRet);
        if (statusRet == CfgMgr.Success)
        {
            statusNode["statusFlags"] = status;
            statusNode["statusFlagNames"] = new JsonArray(StatusFlagNames(status).Select(name => (JsonNode)JsonValue.Create(name)!).ToArray());
            statusNode["problemCode"] = problem;
        }

        queried["devNodeStatus"] = statusNode;

        var parent = CfgMgr.ReadParent(devInst);
        var hardwareIds = CfgMgr.ReadHardwareIds(devInst);
        var deviceDesc = CfgMgr.ReadDeviceDesc(devInst);
        var friendlyName = CfgMgr.ReadFriendlyName(devInst);
        var enumerator = CfgMgr.ReadEnumerator(devInst);
        var className = CfgMgr.ReadClass(devInst);
        var service = CfgMgr.ReadService(devInst);
        var problemCode = CfgMgr.ReadProblemCode(devInst);
        var problemStatus = CfgMgr.ReadProblemStatus(devInst);
        var children = CfgMgr.ReadChildren(devInst);

        queried["parentInstanceId"] = Describe(parent, StringValue(parent));
        queried["hardwareIds"] = Describe(hardwareIds, ListValue(hardwareIds));
        queried["deviceDesc"] = Describe(deviceDesc, StringValue(deviceDesc));
        queried["friendlyName"] = Describe(friendlyName, StringValue(friendlyName));
        queried["enumeratorName"] = Describe(enumerator, StringValue(enumerator));
        queried["className"] = Describe(className, StringValue(className));
        queried["service"] = Describe(service, StringValue(service));
        queried["problemCodeProperty"] = Describe(problemCode, UIntValue(problemCode));
        queried["problemStatus"] = Describe(problemStatus, NtStatusValue(problemStatus));
        queried["children"] = Describe(children, ListValue(children));

        var hardwareIdList = PropertyText.AsStringList(hardwareIds) ?? [];
        var (vid, pid, conflict, matched) = ExtractVidPid(hardwareIdList);
        queried["vid"] = VidPidNode(hardwareIds, vid, matched, conflict);
        queried["pid"] = VidPidNode(hardwareIds, pid, matched, conflict);
        if (conflict)
        {
            queried["vidPidConflict"] = true;
        }

        var friendly = PropertyText.AsString(friendlyName);
        var desc = PropertyText.AsString(deviceDesc);
        string? name = null;
        string? nameSource = null;
        if (!string.IsNullOrWhiteSpace(friendly))
        {
            name = friendly;
            nameSource = "friendlyName";
        }
        else if (!string.IsNullOrWhiteSpace(desc))
        {
            name = desc;
            nameSource = "deviceDesc";
        }

        var parentText = PropertyText.AsString(parent);
        return new DeviceFacts
        {
            InstanceId = resolvedInstanceId,
            Queried = queried,
            ParentInstanceId = parentText,
            ParentKnown = parent.Success && parentText is not null,
            Name = name,
            NameSource = nameSource,
            Vid = vid,
            Pid = pid,
            VidPidKnown = matched && !conflict && (vid is not null || pid is not null),
        };
    }

    private static JsonObject VidPidNode(PropertyRead hardwareIds, string? value, bool matched, bool conflict)
    {
        if (!hardwareIds.Success)
        {
            return Describe(hardwareIds, null);
        }

        if (!matched || conflict || value is null)
        {
            var missing = new JsonObject
            {
                ["success"] = false,
                ["derivedFrom"] = "hardwareIds",
                ["detail"] = conflict
                    ? "hardwareIds contain more than one VID/PID pair"
                    : "hardwareIds do not contain VID_ and PID_",
            };
            return missing;
        }

        return new JsonObject
        {
            ["success"] = true,
            ["value"] = value,
            ["derivedFrom"] = "hardwareIds",
        };
    }

    private static (string? Vid, string? Pid, bool Conflict, bool Matched) ExtractVidPid(IReadOnlyList<string> hardwareIds)
    {
        string? vid = null;
        string? pid = null;
        var matched = false;
        var conflict = false;
        foreach (var hardwareId in hardwareIds)
        {
            var vidMatch = VidPattern.Match(hardwareId);
            var pidMatch = PidPattern.Match(hardwareId);
            if (!vidMatch.Success && !pidMatch.Success)
            {
                continue;
            }

            matched = true;
            var nextVid = vidMatch.Success ? vidMatch.Groups[1].Value.ToUpperInvariant() : null;
            var nextPid = pidMatch.Success ? pidMatch.Groups[1].Value.ToUpperInvariant() : null;
            if (vid is not null && nextVid is not null && !vid.Equals(nextVid, StringComparison.OrdinalIgnoreCase))
            {
                conflict = true;
            }

            if (pid is not null && nextPid is not null && !pid.Equals(nextPid, StringComparison.OrdinalIgnoreCase))
            {
                conflict = true;
            }

            vid ??= nextVid;
            pid ??= nextPid;
        }

        return (vid, pid, conflict, matched);
    }

    private static JsonObject Describe(PropertyRead read, JsonNode? value)
    {
        var node = new JsonObject
        {
            ["success"] = read.Success,
            ["configRet"] = read.ConfigRet,
        };
        AddName(node, read.ConfigRet);
        if (read.Success)
        {
            node["propertyType"] = read.PropertyType;
            node["value"] = value;
        }
        else if (read.Bytes is null && read.ConfigRet == CfgMgr.BufferSmall)
        {
            node["detail"] = "property buffer size was zero or larger than 1MB";
        }

        return node;
    }

    private static void AddName(JsonObject node, int configRet)
    {
        var name = CfgMgr.ConfigRetName(configRet);
        if (name is not null)
        {
            node["configRetName"] = name;
        }
    }

    private static JsonNode? StringValue(PropertyRead read)
    {
        var text = PropertyText.AsString(read);
        return text is null ? null : JsonValue.Create(text);
    }

    private static JsonNode? ListValue(PropertyRead read)
    {
        var values = PropertyText.AsStringList(read);
        if (values is null)
        {
            return read.Success && read.Bytes is not null
                ? JsonValue.Create(Convert.ToBase64String(read.Bytes))
                : null;
        }

        return new JsonArray(values.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
    }

    private static JsonNode? UIntValue(PropertyRead read)
    {
        var value = PropertyText.AsUInt32(read);
        return value is null ? null : JsonValue.Create(value.Value);
    }

    private static JsonNode? NtStatusValue(PropertyRead read)
    {
        var value = PropertyText.AsNtStatus(read);
        if (value is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["value"] = value.Value,
            ["hex"] = "0x" + unchecked((uint)value.Value).ToString("X8"),
        };
    }

    private static IEnumerable<string> StatusFlagNames(uint status)
    {
        if ((status & 0x00000002) != 0) yield return "DN_DRIVER_LOADED";
        if ((status & 0x00000008) != 0) yield return "DN_STARTED";
        if ((status & 0x00000400) != 0) yield return "DN_HAS_PROBLEM";
        if ((status & 0x00002000) != 0) yield return "DN_DISABLEABLE";
        if ((status & 0x00004000) != 0) yield return "DN_REMOVABLE";
        if ((status & 0x00010000) != 0) yield return "DN_MF_PARENT";
        if ((status & 0x00020000) != 0) yield return "DN_MF_CHILD";
    }
}
