using System.Runtime.InteropServices;

namespace DeviceWatch.Probe;

internal static class Clock
{
    public static readonly long QpcFrequency = ReadFrequency();

    public static (DateTimeOffset Utc, long Qpc) Observe()
    {
        var utc = DateTimeOffset.UtcNow;
        QueryPerformanceCounter(out var qpc);
        return (utc, qpc);
    }

    public static string FormatUtc(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffK");

    private static long ReadFrequency()
    {
        if (!QueryPerformanceFrequency(out var frequency) || frequency <= 0)
        {
            return 0;
        }

        return frequency;
    }

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceCounter(out long performanceCount);

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceFrequency(out long frequency);
}
