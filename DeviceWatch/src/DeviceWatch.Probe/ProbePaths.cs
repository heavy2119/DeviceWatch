namespace DeviceWatch.Probe;

internal static class ProbePaths
{
    public static string ExecutableLogDirectory()
        => Path.Combine(AppContext.BaseDirectory, "logs");

    public static string EnsureExecutableLogDirectory()
    {
        var directory = ExecutableLogDirectory();
        Directory.CreateDirectory(directory);
        return directory;
    }
}
