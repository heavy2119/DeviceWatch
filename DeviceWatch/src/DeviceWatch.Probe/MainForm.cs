namespace DeviceWatch.Probe;

internal sealed class MainForm : ProbeForm
{
    private enum RunState
    {
        Idle,
        Starting,
        Recording,
        Stopping,
    }

    private readonly string _logDirectory;
    private readonly int? _durationSeconds;
    private readonly object _stopGate = new();
    private readonly ThemeButton _start = new() { Text = "기록 시작", Tone = ButtonTone.Primary, Size = new Size(124, 36) };
    private readonly ThemeButton _stop = new() { Text = "기록 정지", Enabled = false, Size = new Size(112, 36) };
    private readonly ThemeButton _analyze = new() { Text = "로그 분석", Size = new Size(112, 36) };
    private readonly CheckBox _etw = new() { Text = "USBHUB3 ETW", AutoSize = true, BackColor = ProbeTheme.Canvas, ForeColor = ProbeTheme.Ink, Margin = new Padding(10, 8, 0, 0) };
    private readonly Label _etwHint = new()
    {
        AutoSize = true,
        MaximumSize = new Size(520, 0),
        Margin = new Padding(8, 8, 0, 0),
        ForeColor = ProbeTheme.Muted,
        BackColor = ProbeTheme.Canvas,
        Font = ProbeTheme.Caption,
        Text = "USB 3 허브 드라이버의 포트 상태 기록입니다. 관리자 권한이 필요하며, 끊긴 원인을 단정하지 않습니다.",
    };
    private readonly ToolTip _tips = new();
    private readonly StatusPill _pill = new() { Text = "대기" };
    private readonly TextBox _path = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Label _etwAlert = new()
    {
        Dock = DockStyle.Top,
        Height = 34,
        Visible = false,
        Padding = new Padding(10, 6, 10, 6),
        ForeColor = ProbeTheme.Danger,
        BackColor = ProbeTheme.DangerSoft,
        TextAlign = ContentAlignment.MiddleLeft,
    };
    private readonly Label _liveSuspicionTitle = new();
    private readonly ListView _liveSuspicions = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        HideSelection = false,
    };
    private readonly System.Windows.Forms.Timer _suspicionTimer = new() { Interval = 400 };
    private int _suspicionTicket;
    private readonly Label _summary = new()
    {
        Dock = DockStyle.Top,
        AutoSize = false,
        Height = 32,
        Padding = new Padding(2, 8, 2, 4),
        ForeColor = ProbeTheme.Ink,
        BackColor = ProbeTheme.Canvas,
    };
    private readonly TextBox _status = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = false,
    };
    private readonly ListView _events = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
    };
    private ManualResetEventSlim _collectorsReady = new(true);
    private ProbeLog? _log;
    private DeviceChangeWindow? _deviceWindow;
    private CmCollector? _cm;
    private KernelPnpCollector? _kernelPnp;
    private UsbHub3EtwCollector? _etwCollector;
    private RunState _state = RunState.Idle;
    private int _closeAfterStop;
    private int _closeStage;
    private int _exitCode;

    public MainForm(string output, bool enableEtw, int? durationSeconds)
    {
        _logDirectory = Path.GetDirectoryName(output) ?? ProbePaths.ExecutableLogDirectory();
        _durationSeconds = durationSeconds;
        _etw.Checked = enableEtw;

        Text = "DeviceWatch";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        ClientSize = new Size(1180, 760);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = ProbeTheme.Canvas,
            Padding = new Padding(0, 4, 0, 8),
            WrapContents = true,
        };
        _start.Margin = new Padding(0, 0, 6, 0);
        _stop.Margin = new Padding(0, 0, 6, 0);
        _analyze.Margin = new Padding(0, 0, 4, 0);
        buttons.Controls.Add(_start);
        buttons.Controls.Add(_stop);
        buttons.Controls.Add(_analyze);
        buttons.Controls.Add(_etw);
        _etwHint.AutoSize = false;
        _etwHint.Dock = DockStyle.Top;
        _etwHint.Height = 32;
        _etwHint.MaximumSize = new Size(0, 0);
        _etwHint.Margin = new Padding(0);
        _etwHint.Padding = new Padding(2, 2, 2, 8);
        _tips.SetToolTip(_etw, "Microsoft-Windows-USB-USBHUB3(usbhub3.sys)의 Default 기록만 켭니다. 허브 포트 상태 변화를 추가로 남기며, USB로 오간 데이터와 드라이버 내부 상태 기록은 켜지 않습니다. 관리자 권한이 없으면 이 항목만 실패하고 장치 연결·분리 수집은 계속됩니다.");
        _tips.SetToolTip(_etwHint, "체크하면 USB 3 허브 드라이버의 포트 상태 변화를 로그에 추가합니다. 장치가 끊긴 이유를 이 기록만으로 정하지 않습니다.");
        _suspicionTimer.Tick += (_, _) =>
        {
            _suspicionTimer.Stop();
            RefreshLiveSuspicions();
        };
        _start.Click += (_, _) => StartRecording();
        _stop.Click += (_, _) => StopRecording(closeWhenDone: false);
        _analyze.Click += (_, _) => OpenAnalysis();

        _events.Columns.Add("시각", 120);
        _events.Columns.Add("출처", 170);
        _events.Columns.Add("동작", 230);
        _events.Columns.Add("장치", 560);
        ProbeTheme.StyleList(_events);
        ProbeTheme.StyleField(_status);
        ProbeTheme.StyleField(_path);
        _path.Font = ProbeTheme.Mono;
        _path.BackColor = ProbeTheme.Surface;
        _summary.Text = "기록 시작을 누르면 새 파일에 수집을 시작합니다.";
        _path.Text = "아직 기록 파일이 없습니다.";

        var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = ProbeTheme.Canvas };
        var title = new Label
        {
            Text = "DeviceWatch",
            Font = ProbeTheme.Title,
            ForeColor = ProbeTheme.Ink,
            BackColor = ProbeTheme.Canvas,
            AutoSize = true,
            Location = new Point(0, 0),
        };
        var subtitle = new Label
        {
            Text = "USB, HID, PnP 연결을 기록합니다",
            Font = ProbeTheme.Caption,
            ForeColor = ProbeTheme.Muted,
            BackColor = ProbeTheme.Canvas,
            AutoSize = true,
            Location = new Point(2, 40),
        };
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(_pill);
        header.Resize += (_, _) => _pill.Location = new Point(Math.Max(0, header.Width - _pill.Width), 8);

        var pathHost = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = ProbeTheme.Line, Padding = new Padding(1) };
        var pathBody = new Panel { Dock = DockStyle.Fill, BackColor = ProbeTheme.Surface, Padding = new Padding(12, 9, 12, 6) };
        pathBody.Controls.Add(_path);
        pathHost.Controls.Add(pathBody);

        var statusCard = ProbeTheme.Card(120);
        var statusBody = ProbeTheme.CardBody();
        statusBody.Controls.Add(_status);
        statusBody.Controls.Add(ProbeTheme.Heading("수집 상태"));
        statusCard.Controls.Add(statusBody);

        _liveSuspicions.Columns.Add("근거", 150);
        _liveSuspicions.Columns.Add("이름", 180);
        _liveSuspicions.Columns.Add("장치", 360);
        _liveSuspicions.Columns.Add("제거", 60);
        _liveSuspicions.Columns.Add("다시 연결", 80);
        _liveSuspicions.Columns.Add("구간", 50);
        _liveSuspicions.Columns.Add("첫 제거", 110);
        ProbeTheme.StyleList(_liveSuspicions);
        var suspicionCard = ProbeTheme.Card(168);
        var suspicionBody = ProbeTheme.CardBody();
        _liveSuspicionTitle.Text = "끊김 의심";
        _liveSuspicionTitle.Dock = DockStyle.Top;
        _liveSuspicionTitle.Height = 28;
        _liveSuspicionTitle.Font = ProbeTheme.Section;
        _liveSuspicionTitle.ForeColor = ProbeTheme.Ink;
        _liveSuspicionTitle.BackColor = ProbeTheme.Surface;
        _liveSuspicionTitle.TextAlign = ContentAlignment.MiddleLeft;
        suspicionBody.Controls.Add(_liveSuspicions);
        suspicionBody.Controls.Add(_liveSuspicionTitle);
        suspicionCard.Controls.Add(suspicionBody);

        var eventCard = ProbeTheme.Card();
        var eventBody = ProbeTheme.CardBody();
        eventBody.Controls.Add(_events);
        eventBody.Controls.Add(ProbeTheme.Heading("실시간 변화"));
        eventCard.Controls.Add(eventBody);

        var page = ProbeTheme.Page();
        page.Controls.Add(eventCard);
        page.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12, BackColor = ProbeTheme.Canvas });
        page.Controls.Add(suspicionCard);
        page.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12, BackColor = ProbeTheme.Canvas });
        page.Controls.Add(statusCard);
        page.Controls.Add(_summary);
        page.Controls.Add(pathHost);
        page.Controls.Add(_etwAlert);
        page.Controls.Add(_etwHint);
        page.Controls.Add(buttons);
        page.Controls.Add(header);
        Controls.Add(page);
        UpdateButtons();
    }

    public int ExitCode => _exitCode;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_durationSeconds is not int seconds)
        {
            return;
        }

        StartRecording();
        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, seconds) * 1000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            Close();
        };
        timer.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closeStage == 2 || _state == RunState.Idle)
        {
            _closeStage = 2;
            return;
        }

        e.Cancel = true;
        StopRecording(closeWhenDone: true);
    }

    private void StartRecording()
    {
        if (_state != RunState.Idle)
        {
            return;
        }

        _state = RunState.Starting;
        UpdateButtons();
        _events.Items.Clear();
        _liveSuspicions.Items.Clear();
        _liveSuspicionTitle.Text = "끊김 의심";
        _etwAlert.Visible = false;
        _status.Clear();
        _summary.Text = "수집기를 등록하고 있습니다.";
        var output = Path.Combine(_logDirectory, $"probe-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        _path.Text = output;
        _collectorsReady.Dispose();
        _collectorsReady = new ManualResetEventSlim(false);
        var enableEtw = _etw.Checked;
        Task.Run(() => StartCollectors(output, enableEtw));
    }

    private void StopRecording(bool closeWhenDone)
    {
        if (closeWhenDone)
        {
            Interlocked.Exchange(ref _closeAfterStop, 1);
        }

        if (_state is RunState.Idle)
        {
            return;
        }

        if (_state is RunState.Stopping)
        {
            return;
        }

        _state = RunState.Stopping;
        UpdateButtons();
        _summary.Text = "기록을 저장하고 있습니다.";
        Task.Run(StopCollectors);
    }

    private void StartCollectors(string output, bool enableEtw)
    {
        try
        {
            var log = new ProbeLog(output);
            log.Notice = OnNotice;
            _log = log;
            log.Enqueue(RawEvent.Status("Probe", "started", new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["pid"] = Environment.ProcessId,
                ["qpcFrequency"] = Clock.QpcFrequency,
                ["elevated"] = IsElevated(),
                ["etwRequested"] = enableEtw,
                ["output"] = log.OutputPath,
                ["duplicatePolicy"] = "keep every source record",
                ["sourceTimeUtc"] = "filled only when the original event carries a timestamp",
            }));

            var deviceWindow = new DeviceChangeWindow(log);
            var cm = new CmCollector(log);
            var kernelPnp = new KernelPnpCollector(log);
            var etw = new UsbHub3EtwCollector(log);
            _deviceWindow = deviceWindow;
            _cm = cm;
            _kernelPnp = kernelPnp;
            _etwCollector = etw;
            deviceWindow.Start();
            cm.Start();
            cm.Snapshot();
            kernelPnp.Start();
            if (enableEtw)
            {
                etw.Start();
            }
            else
            {
                log.Enqueue(RawEvent.Status("UsbHub3Etw", "notRequested", new Dictionary<string, object?>
                {
                    ["detail"] = "USBHUB3 trace starts only when the ETW box is checked",
                }));
            }

            var ready = deviceWindow.Ready && cm.RegistrationsSucceeded;
            _exitCode = ready ? 0 : 1;
            Post(() =>
            {
                if (_state == RunState.Stopping)
                {
                    return;
                }

                _state = RunState.Recording;
                UpdateButtons();
                _summary.Text = ready
                    ? "기록 중입니다. 장치를 분리하거나 다시 연결하면 아래 목록에 표시됩니다."
                    : "필수 수집기 등록에 실패했습니다. 위 상태를 확인한 뒤 기록 정지를 누르세요.";
            });
        }
        catch (Exception ex)
        {
            _exitCode = 1;
            Post(() =>
            {
                _state = RunState.Idle;
                UpdateButtons();
                _summary.Text = "수집을 시작하지 못했습니다.";
                _status.AppendText(ex.GetType().Name + ": " + ex.Message + Environment.NewLine);
            });
        }
        finally
        {
            _collectorsReady.Set();
        }
    }

    private void StopCollectors()
    {
        _collectorsReady.Wait(TimeSpan.FromSeconds(30));
        lock (_stopGate)
        {
            if (_log is not null && _deviceWindow is not null && _cm is not null &&
                (!_deviceWindow.Ready || !_cm.RegistrationsSucceeded))
            {
                _log.Enqueue(RawEvent.Status("Probe", "requiredCollectorFailed", new Dictionary<string, object?>
                {
                    ["windowReady"] = _deviceWindow.Ready,
                    ["cmRegistrationsSucceeded"] = _cm.RegistrationsSucceeded,
                }));
            }

            var path = _log?.OutputPath;
            var events = _log?.EventCount ?? 0;
            var snapshots = _log?.SnapshotCount ?? 0;
            _etwCollector?.Dispose();
            _kernelPnp?.Dispose();
            _cm?.Dispose();
            _deviceWindow?.Dispose();
            _log?.Dispose();
            _etwCollector = null;
            _kernelPnp = null;
            _cm = null;
            _deviceWindow = null;
            _log = null;
            var close = Interlocked.Exchange(ref _closeAfterStop, 0) == 1;
            Post(() =>
            {
                _state = RunState.Idle;
                UpdateButtons();
                _summary.Text = $"기록을 멈췄습니다. 스냅샷 {snapshots}건, 변화 {events}건.";
                if (close)
                {
                    _closeStage = 2;
                    Close();
                }
            });
        }
    }

    private void OpenAnalysis()
    {
        var form = new AnalysisForm(_logDirectory);
        var current = _log?.OutputPath;
        if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
        {
            form.LoadFile(current);
        }

        form.Show(this);
    }

    private void OnNotice(ProbeNotice notice)
    {
        Post(() => ShowNotice(notice));
    }

    private void ShowNotice(ProbeNotice notice)
    {
        if (notice.RecordType == "collectorStatus")
        {
            _status.AppendText($"{notice.ObservedAtUtc.ToLocalTime():HH:mm:ss}  {notice.Source}  {notice.Action}  {notice.Detail}{Environment.NewLine}");
            _status.SelectionStart = _status.TextLength;
            _status.ScrollToCaret();
            if (notice.Source == "UsbHub3Etw" && notice.Action is "startFailed" or "enableFailed" or "layoutRejected")
            {
                _etwAlert.Text = notice.Detail.Contains("win32=5", StringComparison.Ordinal)
                    ? "USBHUB3 ETW를 열지 못했습니다. 관리자 권한이 없어 이 기록만 빠지고, 장치 연결 수집은 계속됩니다."
                    : "USBHUB3 ETW를 열지 못했습니다. " + notice.Detail;
                _etwAlert.Visible = true;
            }

            return;
        }

        if (notice.Action == "DBT_DEVNODES_CHANGED")
        {
            return;
        }

        if (DisconnectSignals.Classify(notice.Action) != DisconnectSignal.None)
        {
            _suspicionTimer.Stop();
            _suspicionTimer.Start();
        }

        var item = new ListViewItem(notice.ObservedAtUtc.ToLocalTime().ToString("HH:mm:ss.fff"));
        item.SubItems.Add(notice.Source);
        item.SubItems.Add(notice.Action);
        item.SubItems.Add(notice.Device);
        _events.Items.Add(item);
        if (_events.Items.Count > 2000)
        {
            _events.Items.RemoveAt(0);
        }

        item.EnsureVisible();
        _summary.Text = $"기록 중. 표시 중인 변화 {_events.Items.Count}건, 저장된 변화 {_log?.EventCount ?? 0}건.";
    }

    private void RefreshLiveSuspicions()
    {
        var path = _path.Text;
        if (!File.Exists(path))
        {
            return;
        }

        var ticket = Interlocked.Increment(ref _suspicionTicket);
        Task.Run(() =>
        {
            IReadOnlyList<DisconnectSuspicion> items;
            try
            {
                items = LogInspection.Read(path).DisconnectSuspicions();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            Post(() =>
            {
                if (ticket != _suspicionTicket)
                {
                    return;
                }

                ShowLiveSuspicions(items);
            });
        });
    }

    private void ShowLiveSuspicions(IReadOnlyList<DisconnectSuspicion> items)
    {
        _liveSuspicions.BeginUpdate();
        _liveSuspicions.Items.Clear();
        foreach (var suspicion in items)
        {
            var item = new ListViewItem(suspicion.Headline);
            item.SubItems.Add(suspicion.Name ?? "");
            item.SubItems.Add(suspicion.DeviceKey);
            item.SubItems.Add(suspicion.RemovalCount.ToString());
            item.SubItems.Add(suspicion.ReturnCount.ToString());
            item.SubItems.Add(suspicion.CycleCount.ToString());
            item.SubItems.Add(suspicion.FirstRemovalUtc.ToLocalTime().ToString("HH:mm:ss.fff"));
            item.BackColor = suspicion.CycleCount > 0
                ? Color.FromArgb(255, 236, 214)
                : Color.FromArgb(255, 226, 226);
            _liveSuspicions.Items.Add(item);
        }

        _liveSuspicions.EndUpdate();
        _liveSuspicionTitle.Text = items.Count == 0
            ? "끊김 의심"
            : $"끊김 의심 {items.Count}건";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _suspicionTimer.Stop();
        _suspicionTimer.Dispose();
        base.OnFormClosed(e);
    }

    private void UpdateButtons()
    {
        var recording = _state != RunState.Idle;
        _stop.Enabled = _state is RunState.Recording or RunState.Starting;
        _etw.Enabled = _state == RunState.Idle;
        _start.Enabled = true;
        if (recording)
        {
            _start.Text = "기록중";
            _start.Tone = ButtonTone.Danger;
            _start.Invalidate();
            _pill.Text = _state == RunState.Stopping ? "저장 중" : "기록중";
            _pill.BackColor = ProbeTheme.DangerSoft;
            _pill.ForeColor = ProbeTheme.Danger;
            _tips.SetToolTip(_start, "기록 중입니다. 멈추려면 기록 정지를 누르세요.");
            return;
        }

        _start.Text = "기록 시작";
        _start.Tone = ButtonTone.Primary;
        _start.Invalidate();
        _pill.Text = "대기";
        _pill.BackColor = Color.FromArgb(226, 232, 240);
        _pill.ForeColor = Color.FromArgb(71, 85, 105);
        _tips.SetToolTip(_start, "새 로그 파일을 만들고 수집을 시작합니다.");
    }

    private void Post(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
