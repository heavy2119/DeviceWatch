namespace DeviceWatch.Probe;

internal sealed class AnalysisForm : ProbeForm
{
    private readonly string _logDirectory;
    private readonly TextBox _path = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly TextBox _filter = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _snapshots = new() { Text = "스냅샷 포함", AutoSize = true, BackColor = ProbeTheme.Canvas, ForeColor = ProbeTheme.Ink, Padding = new Padding(12, 8, 0, 0) };
    private readonly ThemeButton _showAll = new() { Text = "전체 기록", Dock = DockStyle.Right, Size = new Size(104, 34) };
    private readonly ThemeButton _reload = new() { Text = "다시 읽기", Dock = DockStyle.Right, Enabled = false, Size = new Size(104, 34) };
    private readonly Label _suspicionTitle = new()
    {
        Dock = DockStyle.Top,
        Height = 26,
        Padding = new Padding(8, 4, 8, 0),
        Font = ProbeTheme.Section,
        ForeColor = ProbeTheme.Ink,
        BackColor = ProbeTheme.Surface,
        Text = "끊김 의심",
    };
    private readonly Label _suspicionNote = new()
    {
        Dock = DockStyle.Top,
        Height = 48,
        Padding = new Padding(0, 0, 8, 0),
        Font = ProbeTheme.Caption,
        ForeColor = ProbeTheme.Muted,
        BackColor = ProbeTheme.Surface,
        Text = "같은 장치 키에서 제거가 기록되고 그 뒤 다시 연결된 장치를 올립니다. 제거 기록만 있는 장치도 함께 표시합니다. 장치 키가 같은 줄만 한 장치로 모으고, 고장 위치는 정하지 않습니다. 항목을 누르면 그 장치의 기록만 봅니다.",
    };
    private readonly ListView _suspicions = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
    };
    private readonly TextBox _summary = new()
    {
        Dock = DockStyle.Top,
        Multiline = true,
        ReadOnly = true,
        Height = 96,
        ScrollBars = ScrollBars.Vertical,
    };
    private readonly ListView _records = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
    };
    private readonly ListView _nearby = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        Height = 140,
    };
    private readonly TextBox _detail = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = ProbeTheme.Mono,
    };
    private readonly SplitContainer _bodySplit;
    private readonly SplitContainer _rightSplit;
    private readonly HashSet<int> _evidenceLines = new();
    private LogInspection? _inspection;
    private IReadOnlyList<DisconnectSuspicion> _suspicionData = [];
    private DisconnectSuspicion? _activeSuspicion;
    private string? _deviceFocus;
    private int _suppress;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            _bodySplit.SplitterDistance = Math.Max(_bodySplit.Panel1MinSize, _bodySplit.Width * 3 / 5);
            _rightSplit.SplitterDistance = Math.Max(_rightSplit.Panel1MinSize, _rightSplit.Height / 2);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public AnalysisForm(string logDirectory)
    {
        _logDirectory = string.IsNullOrWhiteSpace(logDirectory)
            ? ProbePaths.EnsureExecutableLogDirectory()
            : logDirectory;
        Text = "DeviceWatch 로그 분석";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1040, 720);
        ClientSize = new Size(1280, 860);

        var open = new ThemeButton { Text = "로그 열기", Dock = DockStyle.Right, Tone = ButtonTone.Primary, Size = new Size(112, 34) };
        open.Click += (_, _) => ChooseFile();
        _reload.Click += (_, _) => ReloadFile();
        var fileRow = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = ProbeTheme.Canvas, Padding = new Padding(0, 4, 0, 8) };
        var pathHost = new Panel { Dock = DockStyle.Fill, BackColor = ProbeTheme.Soft, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 0, 8, 0) };
        pathHost.Controls.Add(_path);
        fileRow.Controls.Add(pathHost);
        fileRow.Controls.Add(_reload);
        fileRow.Controls.Add(open);

        _snapshots.Dock = DockStyle.Right;
        var filterRow = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = ProbeTheme.Canvas, Padding = new Padding(0, 6, 0, 6) };
        var filterBorder = new Panel { Dock = DockStyle.Fill, BackColor = ProbeTheme.Line, Padding = new Padding(1) };
        var filterHost = new Panel { Dock = DockStyle.Fill, BackColor = ProbeTheme.Surface, Padding = new Padding(10, 7, 10, 7) };
        filterHost.Controls.Add(_filter);
        filterBorder.Controls.Add(filterHost);
        filterRow.Controls.Add(filterBorder);
        filterRow.Controls.Add(_snapshots);
        filterRow.Controls.Add(_showAll);
        _filter.PlaceholderText = "출처, 동작, 장치 검색";
        _filter.TextChanged += (_, _) => FillRecords();
        _snapshots.CheckedChanged += (_, _) => FillRecords();
        _showAll.Click += (_, _) => ShowAllRecords();

        AddSuspicionColumns();
        AddColumns(_records);
        AddColumns(_nearby);
        _suspicions.SelectedIndexChanged += (_, _) => OnSuspicionSelected();
        _records.SelectedIndexChanged += (_, _) => ShowSelected();
        _nearby.SelectedIndexChanged += (_, _) =>
        {
            if (_nearby.SelectedItems.Count == 1 && _nearby.SelectedItems[0].Tag is LogRecord record)
            {
                ShowDetail(record);
            }
        };

        var suspicionPanel = new Panel { Dock = DockStyle.Top, Height = 252, BackColor = ProbeTheme.Line, Padding = new Padding(1) };
        var suspicionBody = new Panel { Dock = DockStyle.Fill, BackColor = ProbeTheme.Surface, Padding = new Padding(14, 8, 14, 10) };
        suspicionBody.Controls.Add(_suspicions);
        suspicionBody.Controls.Add(_suspicionNote);
        suspicionBody.Controls.Add(_suspicionTitle);
        suspicionPanel.Controls.Add(suspicionBody);

        _rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        _rightSplit.Panel1.Controls.Add(_detail);
        _rightSplit.Panel2.Controls.Add(_nearby);
        _bodySplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        _bodySplit.Panel1.Controls.Add(_records);
        _bodySplit.Panel2.Controls.Add(_rightSplit);

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            Padding = new Padding(2, 8, 2, 0),
            Font = ProbeTheme.Caption,
            ForeColor = ProbeTheme.Muted,
            BackColor = ProbeTheme.Canvas,
            Text = "아래쪽 목록은 선택한 변화의 관찰 시각 앞뒤 2초에 있는 다른 변화 줄입니다. 시간 간격을 보여 주며, 고장 위치는 정하지 않습니다.",
        };

        var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = ProbeTheme.Canvas };
        header.Controls.Add(new Label
        {
            Text = "로그 분석",
            Font = ProbeTheme.Title,
            ForeColor = ProbeTheme.Ink,
            BackColor = ProbeTheme.Canvas,
            AutoSize = true,
            Location = new Point(0, 0),
        });
        header.Controls.Add(new Label
        {
            Text = "제거 뒤 다시 연결된 장치를 근거와 함께 보여 줍니다",
            Font = ProbeTheme.Caption,
            ForeColor = ProbeTheme.Muted,
            BackColor = ProbeTheme.Canvas,
            AutoSize = true,
            Location = new Point(2, 38),
        });

        var page = ProbeTheme.Page();
        page.Controls.Add(_bodySplit);
        page.Controls.Add(note);
        page.Controls.Add(filterRow);
        page.Controls.Add(_summary);
        page.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12, BackColor = ProbeTheme.Canvas });
        page.Controls.Add(suspicionPanel);
        page.Controls.Add(fileRow);
        page.Controls.Add(header);
        Controls.Add(page);

        ProbeTheme.StyleField(_path, muted: true);
        ProbeTheme.StyleField(_filter);
        _filter.BackColor = ProbeTheme.Surface;
        _filter.Font = ProbeTheme.Ui;
        ProbeTheme.StyleField(_summary);
        _summary.BackColor = ProbeTheme.Soft;
        _summary.ForeColor = ProbeTheme.Ink;
        _summary.Font = ProbeTheme.Caption;
        ProbeTheme.StyleField(_detail);
        _detail.BackColor = Color.FromArgb(248, 250, 252);
        ProbeTheme.StyleList(_suspicions);
        ProbeTheme.StyleList(_records);
        ProbeTheme.StyleList(_nearby);
        _bodySplit.BackColor = ProbeTheme.Canvas;
        _bodySplit.BorderStyle = BorderStyle.None;
        _bodySplit.SplitterWidth = 12;
        _rightSplit.BackColor = ProbeTheme.Canvas;
        _rightSplit.BorderStyle = BorderStyle.None;
        _rightSplit.SplitterWidth = 12;
        _bodySplit.Panel1.BackColor = ProbeTheme.Line;
        _bodySplit.Panel1.Padding = new Padding(1);
        _bodySplit.Panel2.BackColor = ProbeTheme.Canvas;
        _rightSplit.Panel1.BackColor = ProbeTheme.Surface;
        _rightSplit.Panel1.Padding = new Padding(12, 10, 12, 10);
        _rightSplit.Panel2.BackColor = ProbeTheme.Line;
        _rightSplit.Panel2.Padding = new Padding(1);
        _summary.Text = "JSONL 로그를 열면 제거 뒤 다시 연결이 기록된 장치를 끊김 의심으로 올립니다.";
    }

    public void LoadFile(string path)
    {
        _activeSuspicion = null;
        _deviceFocus = null;
        try
        {
            _inspection = LogInspection.Read(path);
            _path.Text = path;
            _summary.Text = _inspection.Summary();
            _reload.Enabled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _inspection = null;
            _path.Text = path;
            _summary.Text = "로그 파일을 읽지 못했습니다." + Environment.NewLine + ex.Message;
            var owner = IsHandleCreated ? this : null;
            MessageBox.Show(owner, "로그 파일을 읽지 못했습니다." + Environment.NewLine + ex.Message, "DeviceWatch 로그 분석");
        }

        FillSuspicions();
        FillRecords();
    }

    private void ReloadFile()
    {
        if (!string.IsNullOrWhiteSpace(_path.Text) && File.Exists(_path.Text))
        {
            LoadFile(_path.Text);
        }
    }

    private void ChooseFile()
    {
        var directory = _logDirectory;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            directory = ProbePaths.EnsureExecutableLogDirectory();
        }
        if (!directory.EndsWith(Path.DirectorySeparatorChar))
        {
            directory += Path.DirectorySeparatorChar;
        }

        using var dialog = new OpenFileDialog
        {
            Filter = "JSONL 로그 (*.jsonl)|*.jsonl|모든 파일 (*.*)|*.*",
            Title = "DeviceWatch 로그 열기",
            InitialDirectory = directory,
            RestoreDirectory = false,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            LoadFile(dialog.FileName);
        }
    }

    private void ShowAllRecords()
    {
        _activeSuspicion = null;
        _deviceFocus = null;
        _suppress++;
        _suspicions.SelectedIndices.Clear();
        _suppress--;
        FillRecords();
    }

    private void OnSuspicionSelected()
    {
        if (_suppress > 0)
        {
            return;
        }

        if (_suspicions.SelectedItems.Count == 1 && _suspicions.SelectedItems[0].Tag is DisconnectSuspicion suspicion)
        {
            _activeSuspicion = suspicion;
            _deviceFocus = suspicion.DeviceKey;
        }
        else
        {
            _activeSuspicion = null;
            _deviceFocus = null;
        }

        FillRecords();
    }

    private void FillSuspicions()
    {
        _suppress++;
        _suspicions.BeginUpdate();
        _suspicions.Items.Clear();
        _evidenceLines.Clear();
        _suspicionData = _inspection?.DisconnectSuspicions() ?? [];
        foreach (var suspicion in _suspicionData)
        {
            foreach (var record in suspicion.Evidence)
            {
                _evidenceLines.Add(record.LineNumber);
            }

            var item = new ListViewItem(suspicion.Headline);
            item.SubItems.Add(suspicion.DeviceKey);
            item.SubItems.Add(suspicion.Name ?? "");
            item.SubItems.Add(suspicion.VidPid ?? "");
            item.SubItems.Add(suspicion.RemovalCount.ToString());
            item.SubItems.Add(suspicion.ReturnCount.ToString());
            item.SubItems.Add(suspicion.CycleCount.ToString());
            item.SubItems.Add(suspicion.FirstRemovalUtc.ToLocalTime().ToString("HH:mm:ss.fff"));
            item.SubItems.Add(suspicion.LastReturnUtc?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "");
            item.Tag = suspicion;
            item.BackColor = suspicion.CycleCount > 0
                ? Color.FromArgb(255, 236, 214)
                : Color.FromArgb(255, 226, 226);
            _suspicions.Items.Add(item);
        }

        _suspicions.EndUpdate();
        _suppress--;
        _suspicionTitle.Text = _suspicionData.Count == 0
            ? "끊김 의심 없음"
            : $"끊김 의심 {_suspicionData.Count}건";
    }

    private void FillRecords()
    {
        _records.BeginUpdate();
        _records.Items.Clear();
        _nearby.Items.Clear();
        _detail.Clear();
        if (_inspection is null)
        {
            _records.EndUpdate();
            return;
        }

        var filter = _filter.Text.Trim();
        ListViewItem? firstEvidence = null;
        foreach (var record in _inspection.Records)
        {
            if (!_snapshots.Checked && record.RecordType == "snapshot")
            {
                continue;
            }

            if (_deviceFocus is not null && !string.Equals(record.DeviceKey, _deviceFocus, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Matches(record, filter))
            {
                continue;
            }

            var item = CreateItem(record);
            if (_evidenceLines.Contains(record.LineNumber))
            {
                item.BackColor = DisconnectSignals.Classify(record.Action) == DisconnectSignal.Removal
                    ? Color.FromArgb(255, 214, 214)
                    : Color.FromArgb(214, 236, 214);
                firstEvidence ??= item;
            }
            else if (record.DeviceKey is not null && _suspicionData.Any(itemSuspicion => string.Equals(itemSuspicion.DeviceKey, record.DeviceKey, StringComparison.OrdinalIgnoreCase)))
            {
                item.BackColor = Color.FromArgb(255, 248, 236);
            }

            _records.Items.Add(item);
        }

        _records.EndUpdate();
        var selection = firstEvidence ?? (_records.Items.Count > 0 ? _records.Items[0] : null);
        if (selection is not null)
        {
            selection.Selected = true;
            selection.EnsureVisible();
        }
        else if (_activeSuspicion is not null)
        {
            _detail.Text = _activeSuspicion.Describe();
        }
    }

    private void ShowSelected()
    {
        if (_records.SelectedItems.Count != 1 || _records.SelectedItems[0].Tag is not LogRecord record || _inspection is null)
        {
            return;
        }

        ShowDetail(record);
        _nearby.BeginUpdate();
        _nearby.Items.Clear();
        foreach (var near in _inspection.NearbyEvents(record, TimeSpan.FromSeconds(2)))
        {
            var item = CreateItem(near);
            if (string.Equals(near.DeviceKey, record.DeviceKey, StringComparison.OrdinalIgnoreCase) && record.DeviceKey is not null)
            {
                item.BackColor = Color.FromArgb(232, 240, 254);
            }

            _nearby.Items.Add(item);
        }

        _nearby.EndUpdate();
    }

    private void ShowDetail(LogRecord record)
    {
        var suspicion = _activeSuspicion;
        if (suspicion is null && record.DeviceKey is not null)
        {
            suspicion = _suspicionData.FirstOrDefault(item => string.Equals(item.DeviceKey, record.DeviceKey, StringComparison.OrdinalIgnoreCase));
        }

        var header = suspicion is null ? "" : suspicion.Describe() + Environment.NewLine + Environment.NewLine;
        var sameDevice = record.DeviceKey is null
            ? "장치 키가 없는 줄입니다."
            : "장치 키: " + record.DeviceKey;
        _detail.Text = header + string.Join(Environment.NewLine, [
            $"줄 {record.LineNumber}  {record.RecordType}  {record.Source}  {record.Action}",
            "observedAtUtc: " + (record.ObservedAtUtc?.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK") ?? "null"),
            "sourceTimeUtc: " + (record.SourceTimeUtc ?? "null"),
            sameDevice,
            "",
            "[raw] 이 줄의 원본",
            LogInspection.Format(record.Json["raw"]),
            "",
            "[queried] 이 줄 이후 조회",
            LogInspection.Format(record.Json["queried"]),
            "",
            "[lastKnown] 이전 조회에서 남아 있던 값",
            LogInspection.Format(record.Json["lastKnown"]),
        ]);
    }

    private static bool Matches(LogRecord record, string filter)
    {
        if (filter.Length == 0)
        {
            return true;
        }

        return Contains(record.RecordType, filter)
            || Contains(record.Source, filter)
            || Contains(record.Action, filter)
            || Contains(record.DeviceKey, filter)
            || Contains(record.QueriedName, filter)
            || Contains(record.LastKnownName, filter)
            || Contains(record.QueriedParent, filter)
            || Contains(record.LastKnownParent, filter)
            || Contains(record.QueriedVidPid, filter)
            || Contains(record.LastKnownVidPid, filter);
    }

    private static bool Contains(string? value, string filter)
        => value?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;

    private void AddSuspicionColumns()
    {
        _suspicions.Columns.Add("근거", 150);
        _suspicions.Columns.Add("장치", 320);
        _suspicions.Columns.Add("이름", 180);
        _suspicions.Columns.Add("VID/PID", 110);
        _suspicions.Columns.Add("제거", 60);
        _suspicions.Columns.Add("다시 연결", 80);
        _suspicions.Columns.Add("구간", 50);
        _suspicions.Columns.Add("첫 제거", 110);
        _suspicions.Columns.Add("마지막 연결", 110);
    }

    private static void AddColumns(ListView view)
    {
        view.Columns.Add("시각", 110);
        view.Columns.Add("종류", 110);
        view.Columns.Add("출처", 150);
        view.Columns.Add("동작", 180);
        view.Columns.Add("장치", 280);
        view.Columns.Add("조회 VID/PID", 110);
        view.Columns.Add("조회 부모", 220);
        view.Columns.Add("이전 부모", 220);
    }

    private static ListViewItem CreateItem(LogRecord record)
    {
        var time = record.ObservedAtUtc?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "";
        var item = new ListViewItem(time);
        item.SubItems.Add(record.RecordType);
        item.SubItems.Add(record.Source);
        item.SubItems.Add(record.Action);
        item.SubItems.Add(record.DeviceKey ?? "");
        item.SubItems.Add(record.QueriedVidPid ?? "");
        item.SubItems.Add(record.QueriedParent ?? "");
        item.SubItems.Add(record.LastKnownParent ?? "");
        item.Tag = record;
        return item;
    }
}
