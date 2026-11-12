using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeviceWatch.Probe;

internal sealed class LogInspection
{
    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private LogInspection(string path, IReadOnlyList<LogRecord> records, int parseErrors, string parseErrorDetail)
    {
        Path = path;
        Records = records;
        ParseErrors = parseErrors;
        ParseErrorDetail = parseErrorDetail;
    }

    public string Path { get; }

    public IReadOnlyList<LogRecord> Records { get; }

    public int ParseErrors { get; }

    public string ParseErrorDetail { get; }

    public static LogInspection Read(string path)
    {
        var records = new List<LogRecord>();
        var errors = new List<string>();
        var lineNumber = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is string line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line) is not JsonObject json)
                {
                    errors.Add($"{lineNumber}: JSON 객체가 아닙니다.");
                    continue;
                }

                records.Add(LogRecord.From(lineNumber, json));
            }
            catch (JsonException ex)
            {
                errors.Add($"{lineNumber}: {ex.Message}");
            }
        }

        return new LogInspection(path, records, errors.Count, string.Join(Environment.NewLine, errors.Take(8)));
    }

    public string Summary()
    {
        var lines = new List<string>
        {
            $"파일: {Path}",
            $"읽은 줄 {Records.Count}건, 파싱 실패 {ParseErrors}건",
        };
        foreach (var group in Records.GroupBy(record => record.RecordType).OrderBy(group => group.Key))
        {
            lines.Add($"{group.Key} {group.Count()}건");
        }

        var events = Records.Where(record => record.RecordType == "event").ToList();
        lines.Add($"변화에 나타난 장치 키 {events.Select(record => record.DeviceKey).Where(key => key is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count()}개");
        foreach (var group in events.GroupBy(record => record.Source + " / " + record.Action).OrderBy(group => group.Key))
        {
            lines.Add($"변화 {group.Key}: {group.Count()}건");
        }

        var suspicions = DisconnectSuspicions();
        var returned = suspicions.Count(item => item.CycleCount > 0);
        var removalOnly = suspicions.Count - returned;
        lines.Add($"끊김 의심 {suspicions.Count}건. 제거 뒤 다시 연결 {returned}건, 제거 기록만 있는 장치 {removalOnly}건.");
        foreach (var item in suspicions.Take(12))
        {
            var name = string.IsNullOrWhiteSpace(item.Name) ? item.DeviceKey : item.Name;
            lines.Add($"{item.Headline} | {name} | 제거 {item.RemovalCount}줄, 다시 연결 {item.ReturnCount}줄, 구간 {item.CycleCount}회");
        }

        if (suspicions.Count > 12)
        {
            lines.Add($"외 {suspicions.Count - 12}건");
        }

        if (ParseErrors > 0 && ParseErrorDetail.Length > 0)
        {
            lines.Add(ParseErrorDetail);
        }

        lines.Add("같은 시각에 가깝다는 것은 기록의 시간 간격입니다. 고장 위치는 이 요약이 정하지 않습니다.");
        return string.Join(Environment.NewLine, lines);
    }

    public IReadOnlyList<DisconnectSuspicion> DisconnectSuspicions()
    {
        var groups = Records
            .Where(record => record.RecordType == "event" && !string.IsNullOrWhiteSpace(record.DeviceKey) && record.ObservedAtUtc is not null)
            .GroupBy(record => record.DeviceKey!, StringComparer.OrdinalIgnoreCase);

        var suspicions = new List<DisconnectSuspicion>();
        foreach (var group in groups)
        {
            if (DisconnectSuspicion.TryCreate(group.ToList()) is DisconnectSuspicion suspicion)
            {
                suspicions.Add(suspicion);
            }
        }

        return suspicions
            .OrderByDescending(item => item.CycleCount)
            .ThenByDescending(item => item.RemovalCount)
            .ThenBy(item => item.FirstRemovalUtc)
            .ToList();
    }

    public IReadOnlyList<LogRecord> NearbyEvents(LogRecord selected, TimeSpan window)
    {
        if (selected.ObservedAtUtc is not DateTimeOffset origin)
        {
            return [];
        }

        return Records
            .Where(record => record.RecordType == "event" && record.LineNumber != selected.LineNumber && record.ObservedAtUtc is DateTimeOffset time && (time - origin).Duration() <= window)
            .OrderBy(record => record.ObservedAtUtc)
            .ToList();
    }

    public static string Format(JsonNode? node)
        => node?.ToJsonString(PrettyOptions) ?? "null";
}

internal sealed class LogRecord
{
    public required int LineNumber { get; init; }

    public required JsonObject Json { get; init; }

    public required string RecordType { get; init; }

    public required string Source { get; init; }

    public required string Action { get; init; }

    public DateTimeOffset? ObservedAtUtc { get; init; }

    public string? SourceTimeUtc { get; init; }

    public string? DeviceKey { get; init; }

    public string? QueriedName { get; init; }

    public string? QueriedVidPid { get; init; }

    public string? QueriedParent { get; init; }

    public string? LastKnownName { get; init; }

    public string? LastKnownVidPid { get; init; }

    public string? LastKnownParent { get; init; }

    public static LogRecord From(int lineNumber, JsonObject json)
    {
        var raw = json["raw"] as JsonObject;
        var queried = json["queried"] as JsonObject;
        var lastKnown = json["lastKnown"] as JsonObject;
        var device = FirstText(json["queriedInstanceId"], raw?["instanceId"], queried?["interfaceInstanceId"]?["value"], raw?["interfacePath"]);
        var vid = Text(queried?["vid"]?["value"]);
        var pid = Text(queried?["pid"]?["value"]);
        string? vidPid = vid is null && pid is null ? null : $"{vid ?? "-"}/{pid ?? "-"}";
        var knownVid = Text(lastKnown?["vid"]);
        var knownPid = Text(lastKnown?["pid"]);
        string? knownVidPid = knownVid is null && knownPid is null ? null : $"{knownVid ?? "-"}/{knownPid ?? "-"}";
        DateTimeOffset? observed = null;
        if (DateTimeOffset.TryParse(Text(json["observedAtUtc"]), out var parsed))
        {
            observed = parsed.ToUniversalTime();
        }

        return new LogRecord
        {
            LineNumber = lineNumber,
            Json = json,
            RecordType = Text(json["recordType"]) ?? "",
            Source = Text(json["source"]) ?? "",
            Action = Text(json["action"]) ?? "",
            ObservedAtUtc = observed,
            SourceTimeUtc = Text(json["sourceTimeUtc"]),
            DeviceKey = device,
            QueriedName = FirstText(queried?["friendlyName"]?["value"], queried?["deviceDesc"]?["value"]),
            QueriedVidPid = vidPid,
            QueriedParent = Text(queried?["parentInstanceId"]?["value"]),
            LastKnownName = Text(lastKnown?["name"]),
            LastKnownVidPid = knownVidPid,
            LastKnownParent = Text(lastKnown?["parentInstanceId"]),
        };
    }

    private static string? FirstText(params JsonNode?[] nodes)
    {
        foreach (var node in nodes)
        {
            var text = Text(node);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? Text(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return null;
    }
}

internal enum DisconnectSignal
{
    None,
    Removal,
    Return,
}

internal static class DisconnectSignals
{
    public static DisconnectSignal Classify(string action)
    {
        if (action.Equals("DEVICEINSTANCEREMOVED", StringComparison.OrdinalIgnoreCase)
            || action.Equals("DEVICEINTERFACEREMOVAL", StringComparison.OrdinalIgnoreCase)
            || action.Equals("DBT_DEVICEREMOVECOMPLETE", StringComparison.OrdinalIgnoreCase))
        {
            return DisconnectSignal.Removal;
        }

        if (action.Equals("DEVICEINSTANCEENUMERATED", StringComparison.OrdinalIgnoreCase)
            || action.Equals("DEVICEINSTANCESTARTED", StringComparison.OrdinalIgnoreCase)
            || action.Equals("DEVICEINTERFACEARRIVAL", StringComparison.OrdinalIgnoreCase)
            || action.Equals("DBT_DEVICEARRIVAL", StringComparison.OrdinalIgnoreCase))
        {
            return DisconnectSignal.Return;
        }

        return DisconnectSignal.None;
    }
}

internal sealed class DisconnectSuspicion
{
    private DisconnectSuspicion(
        string deviceKey,
        int removalCount,
        int returnCount,
        int cycleCount,
        bool endsDisconnected,
        DateTimeOffset firstRemovalUtc,
        DateTimeOffset? lastReturnUtc,
        string? name,
        bool nameFromQuery,
        string? vidPid,
        bool vidPidFromQuery,
        string? parent,
        bool parentFromQuery,
        IReadOnlyList<LogRecord> evidence)
    {
        DeviceKey = deviceKey;
        RemovalCount = removalCount;
        ReturnCount = returnCount;
        CycleCount = cycleCount;
        EndsDisconnected = endsDisconnected;
        FirstRemovalUtc = firstRemovalUtc;
        LastReturnUtc = lastReturnUtc;
        Name = name;
        NameFromQuery = nameFromQuery;
        VidPid = vidPid;
        VidPidFromQuery = vidPidFromQuery;
        Parent = parent;
        ParentFromQuery = parentFromQuery;
        Evidence = evidence;
    }

    public string DeviceKey { get; }

    public int RemovalCount { get; }

    public int ReturnCount { get; }

    public int CycleCount { get; }

    public bool EndsDisconnected { get; }

    public DateTimeOffset FirstRemovalUtc { get; }

    public DateTimeOffset? LastReturnUtc { get; }

    public string? Name { get; }

    public bool NameFromQuery { get; }

    public string? VidPid { get; }

    public bool VidPidFromQuery { get; }

    public string? Parent { get; }

    public bool ParentFromQuery { get; }

    public IReadOnlyList<LogRecord> Evidence { get; }

    public string Headline => CycleCount > 0 ? "제거 뒤 다시 연결" : "제거 기록만 있음";

    public static DisconnectSuspicion? TryCreate(IReadOnlyList<LogRecord> records)
    {
        var ordered = records
            .Where(record => record.ObservedAtUtc is not null)
            .OrderBy(record => record.ObservedAtUtc)
            .ThenBy(record => record.LineNumber)
            .ToList();
        var evidence = new List<LogRecord>();
        var removalCount = 0;
        var returnCount = 0;
        var cycleCount = 0;
        var open = false;
        var acceptFollowingReturns = false;
        DateTimeOffset? firstRemoval = null;
        DateTimeOffset? lastReturn = null;
        foreach (var record in ordered)
        {
            switch (DisconnectSignals.Classify(record.Action))
            {
                case DisconnectSignal.Removal:
                    evidence.Add(record);
                    removalCount++;
                    acceptFollowingReturns = false;
                    if (!open)
                    {
                        open = true;
                        firstRemoval ??= record.ObservedAtUtc;
                    }

                    break;
                case DisconnectSignal.Return when open || acceptFollowingReturns:
                    evidence.Add(record);
                    returnCount++;
                    if (open)
                    {
                        cycleCount++;
                        open = false;
                        acceptFollowingReturns = true;
                        lastReturn = record.ObservedAtUtc;
                    }

                    break;
            }
        }

        if (removalCount == 0 || firstRemoval is not DateTimeOffset removalAt || evidence.Count == 0)
        {
            return null;
        }

        var (name, nameFromQuery) = Latest(ordered, record => record.QueriedName, record => record.LastKnownName);
        var (vidPid, vidPidFromQuery) = Latest(ordered, record => record.QueriedVidPid, record => record.LastKnownVidPid);
        var (parent, parentFromQuery) = Latest(ordered, record => record.QueriedParent, record => record.LastKnownParent);
        return new DisconnectSuspicion(
            evidence[0].DeviceKey ?? ordered[0].DeviceKey ?? "",
            removalCount,
            returnCount,
            cycleCount,
            endsDisconnected: open,
            removalAt,
            lastReturn,
            name,
            nameFromQuery,
            vidPid,
            vidPidFromQuery,
            parent,
            parentFromQuery,
            evidence);
    }

    public string Describe()
    {
        var lines = new List<string>();
        if (CycleCount > 0)
        {
            lines.Add($"제거 기록 {RemovalCount}줄 뒤에 다시 연결 기록 {ReturnCount}줄이 있습니다. 제거 후 연결 구간 {CycleCount}회.");
            if (EndsDisconnected)
            {
                lines.Add("마지막 근거 기록은 제거입니다.");
            }
        }
        else
        {
            lines.Add($"제거 기록 {RemovalCount}줄이 있고, 그 뒤 연결 기록은 이 파일에 없습니다.");
        }

        if (!string.IsNullOrWhiteSpace(Name))
        {
            lines.Add(NameFromQuery ? "이름(조회): " + Name : "이름(이전 조회): " + Name);
        }

        if (!string.IsNullOrWhiteSpace(VidPid))
        {
            lines.Add(VidPidFromQuery ? "VID/PID(조회): " + VidPid : "VID/PID(이전 조회): " + VidPid);
        }

        if (!string.IsNullOrWhiteSpace(Parent))
        {
            lines.Add(ParentFromQuery ? "부모(조회): " + Parent : "부모(이전 조회): " + Parent);
        }

        lines.Add("근거로 쓴 동작은 인스턴스 제거, 인터페이스 제거, 장치 제거 완료, 그리고 그 뒤의 열거·시작·인터페이스 도착·장치 도착입니다.");
        lines.Add("근거 기록:");
        foreach (var record in Evidence)
        {
            var time = record.ObservedAtUtc?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "";
            lines.Add($"  {time}  {record.Source}  {record.Action}  줄 {record.LineNumber}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static (string? Value, bool FromQuery) Latest(
        IReadOnlyList<LogRecord> records,
        Func<LogRecord, string?> queried,
        Func<LogRecord, string?> lastKnown)
    {
        for (var index = records.Count - 1; index >= 0; index--)
        {
            var value = queried(records[index]);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return (value, true);
            }
        }

        for (var index = records.Count - 1; index >= 0; index--)
        {
            var value = lastKnown(records[index]);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return (value, false);
            }
        }

        return (null, false);
    }
}
