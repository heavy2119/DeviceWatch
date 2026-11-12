using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeviceWatch.Probe;

internal sealed class ProbeLog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly BlockingCollection<RawEvent> _queue = new();
    private readonly StreamWriter _writer;
    private readonly Thread _worker;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, LastKnown> _byInstance = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _instanceByInterface = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _eventCounts = new(StringComparer.Ordinal);
    private bool _closed;
    private int _disposed;
    private long _sequence;

    public ProbeLog(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        OutputPath = stream.Name;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "DeviceWatch.Probe.Worker",
        };
        _worker.Start();
    }

    public string OutputPath { get; }

    public int SnapshotCount { get; private set; }

    public int EventCount { get; private set; }

    public int StatusCount { get; private set; }

    public IReadOnlyDictionary<string, int> EventCounts => _eventCounts;

    public void Enqueue(RawEvent item)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            item.ProbeSequence = Interlocked.Increment(ref _sequence);
            _queue.Add(item);
        }
    }

    public void CompleteAndDrain()
    {
        lock (_gate)
        {
            _closed = true;
        }

        _queue.CompleteAdding();
        _worker.Join();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (!_queue.IsAddingCompleted)
        {
            CompleteAndDrain();
        }

        _writer.Dispose();
        _queue.Dispose();
    }

    private void WorkerLoop()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                WriteRecord(item);
            }
            catch (Exception ex)
            {
                WriteRawLine(new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["recordType"] = "collectorStatus",
                    ["source"] = "Probe",
                    ["action"] = "workerError",
                    ["observedAtUtc"] = Clock.FormatUtc(DateTimeOffset.UtcNow),
                    ["detail"] = ex.GetType().Name + ": " + ex.Message,
                });
            }
        }
    }

    private void WriteRecord(RawEvent item)
    {
        LastKnown? previous = null;
        string? previousLookup = null;
        var cacheKey = item.InstanceId;
        if (string.IsNullOrWhiteSpace(cacheKey) &&
            !string.IsNullOrWhiteSpace(item.InterfacePath) &&
            _instanceByInterface.TryGetValue(item.InterfacePath, out var mapped))
        {
            cacheKey = mapped;
            previousLookup = "priorInterfacePath";
        }
        else if (!string.IsNullOrWhiteSpace(cacheKey))
        {
            previousLookup = "instanceId";
        }

        if (!string.IsNullOrWhiteSpace(cacheKey) && _byInstance.TryGetValue(cacheKey, out var existing))
        {
            previous = existing.Copy();
        }

        DeviceFacts? facts = null;
        if (item.QueryDevice)
        {
            facts = DeviceQuery.Query(item.InstanceId, item.InterfacePath);
        }

        if (facts?.InstanceId is not null)
        {
            var updated = existingOrNew(cacheKey, facts.InstanceId);
            if (facts.ParentKnown)
            {
                updated.ParentInstanceId = facts.ParentInstanceId;
            }

            if (facts.Name is not null)
            {
                updated.Name = facts.Name;
                updated.NameSource = facts.NameSource;
            }

            if (facts.VidPidKnown)
            {
                updated.Vid = facts.Vid;
                updated.Pid = facts.Pid;
            }

            updated.AsOfUtc = item.ObservedAtUtc;
            _byInstance[facts.InstanceId] = updated;
            if (!string.IsNullOrWhiteSpace(item.InterfacePath))
            {
                _instanceByInterface[item.InterfacePath] = facts.InstanceId;
            }
        }

        var record = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["probeSequence"] = item.ProbeSequence,
            ["recordType"] = item.RecordType,
            ["source"] = item.Source,
            ["observedAtUtc"] = Clock.FormatUtc(item.ObservedAtUtc),
            ["observedQpc"] = item.ObservedQpc,
            ["sourceTimeUtc"] = item.SourceTimeUtc is null ? null : Clock.FormatUtc(item.SourceTimeUtc.Value),
            ["action"] = item.Action,
            ["raw"] = ToNode(item.Raw) ?? new JsonObject(),
            ["queried"] = facts?.Queried,
            ["lastKnown"] = previous is null ? null : previous.ToJson(previousLookup),
        };

        if (facts?.InstanceId is not null && !string.Equals(facts.InstanceId, item.InstanceId, StringComparison.OrdinalIgnoreCase))
        {
            record["queriedInstanceId"] = facts.InstanceId;
        }

        WriteRawLine(record);
        Count(item);
        if (item.RecordType is "event" or "collectorStatus")
        {
            var device = facts?.InstanceId ?? item.InstanceId ?? item.InterfacePath ?? "";
            try
            {
                Notice?.Invoke(new ProbeNotice(
                    item.RecordType,
                    item.Source,
                    item.Action ?? "",
                    item.ObservedAtUtc,
                    device,
                    item.RecordType == "collectorStatus" ? Summarize(item.Raw) : ""));
            }
            catch (Exception ex)
            {
                WriteRawLine(new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["recordType"] = "collectorStatus",
                    ["source"] = "Probe",
                    ["action"] = "noticeError",
                    ["detail"] = ex.GetType().Name,
                });
            }
        }
    }

    public Action<ProbeNotice>? Notice { get; set; }

    private static string Summarize(Dictionary<string, object?> raw)
    {
        var parts = new List<string>();
        Add(parts, raw, "registrationName");
        Add(parts, raw, "channel");
        Add(parts, raw, "configRetName");
        Add(parts, raw, "win32Error", "win32");
        Add(parts, raw, "count");
        Add(parts, raw, "detail");
        return string.Join("  ", parts);
    }

    private static void Add(List<string> parts, Dictionary<string, object?> raw, string key, string? label = null)
    {
        if (!raw.TryGetValue(key, out var value) || value is null || value is "")
        {
            return;
        }

        parts.Add($"{label ?? key}={value}");
    }

    private LastKnown existingOrNew(string? cacheKey, string instanceId)
    {
        if (!string.IsNullOrWhiteSpace(cacheKey) && _byInstance.TryGetValue(cacheKey, out var existing))
        {
            return existing.Copy();
        }

        if (_byInstance.TryGetValue(instanceId, out existing))
        {
            return existing.Copy();
        }

        return new LastKnown();
    }

    private void Count(RawEvent item)
    {
        switch (item.RecordType)
        {
            case "snapshot":
                SnapshotCount++;
                break;
            case "event":
                EventCount++;
                var key = item.Source + "/" + (item.Action ?? "");
                _eventCounts[key] = _eventCounts.GetValueOrDefault(key) + 1;
                break;
            default:
                StatusCount++;
                break;
        }
    }

    private void WriteRawLine(JsonObject record)
        => _writer.WriteLine(record.ToJsonString(JsonOptions));

    private static JsonNode? ToNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode node => node,
            string text => JsonValue.Create(text),
            bool flag => JsonValue.Create(flag),
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            uint number => JsonValue.Create(number),
            ulong number => JsonValue.Create(number),
            byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
            string[] items => new JsonArray(items.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray()),
            Dictionary<string, object?> map => Map(map),
            _ => JsonValue.Create(value.ToString()),
        };
    }

    private static JsonObject Map(Dictionary<string, object?> map)
    {
        var node = new JsonObject();
        foreach (var (key, value) in map)
        {
            node[key] = ToNode(value);
        }

        return node;
    }

    private sealed class LastKnown
    {
        public string? ParentInstanceId { get; set; }
        public string? Name { get; set; }
        public string? NameSource { get; set; }
        public string? Vid { get; set; }
        public string? Pid { get; set; }
        public DateTimeOffset AsOfUtc { get; set; }

        public LastKnown Copy() => new()
        {
            ParentInstanceId = ParentInstanceId,
            Name = Name,
            NameSource = NameSource,
            Vid = Vid,
            Pid = Pid,
            AsOfUtc = AsOfUtc,
        };

        public JsonObject ToJson(string? lookup)
        {
            return new JsonObject
            {
                ["lookup"] = lookup,
                ["parentInstanceId"] = ParentInstanceId,
                ["name"] = Name,
                ["nameSource"] = NameSource,
                ["vid"] = Vid,
                ["pid"] = Pid,
                ["asOfUtc"] = AsOfUtc == default ? null : Clock.FormatUtc(AsOfUtc),
            };
        }
    }
}
