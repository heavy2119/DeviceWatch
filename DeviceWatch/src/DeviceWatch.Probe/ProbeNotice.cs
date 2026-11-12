namespace DeviceWatch.Probe;

internal sealed record ProbeNotice(
    string RecordType,
    string Source,
    string Action,
    DateTimeOffset ObservedAtUtc,
    string Device,
    string Detail);
