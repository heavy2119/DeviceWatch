using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Windows.Forms;

[assembly: SupportedOSPlatform("windows")]

namespace DeviceWatch.Probe;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            MessageBox.Show("DeviceWatch.Probe는 64비트 Windows에서만 실행합니다.", "DeviceWatch Probe");
            return 2;
        }

        ApplicationConfiguration.Initialize();
        if (args.Length >= 2 && args[0] == "--inspect")
        {
            return Inspect(args[1]);
        }

        if (!TryParse(args, out var output, out var durationSeconds, out var enableEtw, out var exitCode))
        {
            return exitCode;
        }

        var form = new MainForm(output, enableEtw, durationSeconds);
        Application.Run(form);
        return form.ExitCode;
    }

    private static bool TryParse(string[] args, out string output, out int? durationSeconds, out bool enableEtw, out int exitCode)
    {
        output = Path.Combine(ProbePaths.EnsureExecutableLogDirectory(), $"probe-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        durationSeconds = null;
        enableEtw = false;
        exitCode = 2;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg is "--help" or "-h" or "/?")
            {
                ShowHelp();
                exitCode = 0;
                return false;
            }

            if (arg == "--etw")
            {
                enableEtw = true;
                continue;
            }

            if (arg == "--output" && index + 1 < args.Length)
            {
                output = Path.GetFullPath(args[++index]);
                continue;
            }

            if (arg == "--duration" && index + 1 < args.Length && int.TryParse(args[++index], out var seconds) && seconds > 0)
            {
                durationSeconds = seconds;
                continue;
            }

            MessageBox.Show("알 수 없는 인수입니다: " + arg + "\r\n\r\n" + HelpText, "DeviceWatch Probe");
            return false;
        }

        exitCode = 0;
        return true;
    }

    private static void ShowHelp()
        => MessageBox.Show(HelpText, "DeviceWatch Probe");

    private const string HelpText = """
        DeviceWatch.Probe

        사용법:
          DeviceWatch.Probe [--output 경로] [--duration 초] [--etw]

        --etw 는 Microsoft-Windows-USB-USBHUB3 의 Default 키워드만 켭니다.
        버스 데이터와 StateMachine 키워드는 켜지 않습니다.
        관리자 권한이 없으면 그 실패만 기록하고 나머지 수집은 계속합니다.

        기록 파일은 실행 파일 옆의 logs 폴더에 만들어집니다.
        --output 으로 다른 파일을 지정하면 그 파일이 있는 폴더를 사용합니다.

        테스트 A-H는 기록 시작을 누른 뒤 하드웨어를 직접 조작합니다.
        그 기록이 생기기 전에는 Phase 0 통과로 보지 않습니다.

        저장된 로그 요약:
          DeviceWatch.Probe --inspect 로그파일.jsonl
        """;

    private static int Inspect(string path)
    {
        AttachConsole(-1);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var inspection = LogInspection.Read(path);
            Console.WriteLine(inspection.Summary());
            return inspection.ParseErrors == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
