namespace DeviceWatch.Probe;

internal sealed class RawEvent
{
    public long ProbeSequence { get; set; }

    public required string RecordType { get; init; }

    public required string Source { get; init; }

    public DateTimeOffset ObservedAtUtc { get; init; }

    public long ObservedQpc { get; init; }

    public DateTimeOffset? SourceTimeUtc { get; init; }

    public string? Action { get; init; }

    public string? InstanceId { get; init; }

    public string? InterfacePath { get; init; }

    public bool QueryDevice { get; init; }

    public Dictionary<string, object?> Raw { get; init; } = new(StringComparer.Ordinal);

    public static RawEvent Status(string source, string action, Dictionary<string, object?> raw)
    {
        var (utc, qpc) = Clock.Observe();
        return new RawEvent
        {
            RecordType = "collectorStatus",
            Source = source,
            Action = action,
            ObservedAtUtc = utc,
            ObservedQpc = qpc,
            QueryDevice = false,
            Raw = raw,
        };
    }
}
