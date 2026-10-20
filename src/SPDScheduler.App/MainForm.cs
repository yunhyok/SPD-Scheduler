using SPDScheduler.Core;

namespace SPDScheduler.App;

internal sealed class MainForm : Form
{
    private readonly SchedulerEngine engine;
    private readonly bool safeMode;
    private readonly Func<Action, Task> command;
    private readonly string dataDirectory;
    private readonly QueueGrid grid = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { AutoSize = true, ForeColor = Color.FromArgb(72, 85, 101), Margin = new Padding(0, 7, 0, 0) };
    private readonly Button start;
    private readonly Button pause;
    private readonly Button checkNow;
    private readonly Label checkTiming = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.DimGray, Margin = Padding.Empty };
    private string queueSignature = "";
    private bool closeForExit;

    public MainForm(SchedulerEngine engine, bool safeMode, Func<Action, Task> command, Func<Task> checkNow, string dataDirectory)
    {
        this.engine = engine;
        this.safeMode = safeMode;
        this.command = command;
        this.dataDirectory = dataDirectory;
        Text = "SPD Scheduler " + Application.ProductVersion.Split('+')[0] + (safeMode ? " · 안전 모드" : "");
        Font = new Font("맑은 고딕", 9F);
        ClientSize = new Size(744, 490);
        MinimumSize = new Size(720, 478);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(246, 248, 251);
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        KeyPreview = true;
        AllowDrop = true;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 12), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        Controls.Add(layout);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new Label { Text = "SPD Scheduler", Font = new Font(Font.FontFamily, 17F, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(28, 45, 66), Margin = Padding.Empty };
        header.Controls.Add(title);
        var execution = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        start = MakeButton("시작", async () => { await command(engine.Start); if (engine.IsArmed) await checkNow(); }, true);
        pause = MakeButton("일시 중지", async () => await command(engine.Pause));
        execution.Controls.AddRange([start, pause]);
        header.Controls.Add(execution);
        layout.Controls.Add(header, 0, 0);

        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        tools.Controls.Add(MakeButton("파일 추가", AddFilesAsync));
        tools.Controls.Add(MakeButton("제거", () => ReconcileSelectionAsync(remove: true)));
        tools.Controls.Add(MakeButton("예약", ScheduleAsync));
        tools.Controls.Add(MakeButton("예약 해제", () => SelectedCommandAsync(id => engine.SetSchedule(id, null))));
        tools.Controls.Add(MakeButton("완료 정리", async () => await command(() =>
        {
            foreach (Guid id in engine.Jobs.Where(j => j.Status == JobStatus.Completed).Select(j => j.Id).ToArray()) engine.Remove(id);
        })));
        tools.Controls.Add(MakeButton("설정", SettingsAsync));
        tools.Controls.Add(MakeButton("감시 상세", () =>
        {
            using var detail = new Form { Text = "PowerSI 감시 상세 · 마지막 외부 유휴 관측", ClientSize = new Size(760, 440),
                StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, ShowInTaskbar = false };
            detail.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Both, WordWrap = false, Text = engine.MonitoringDetails,
                AccessibleName = "PowerSI 감시 상세 (Ctrl+A, Ctrl+C로 복사)", BackColor = Color.White });
            detail.ShowDialog(this);
            return Task.CompletedTask;
        }));
        layout.Controls.Add(tools, 0, 1);
        layout.Controls.Add(grid, 0, 2);
        layout.Controls.Add(status, 0, 3);
        var checks = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 5) };
        checks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        checks.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        checks.Controls.Add(checkTiming);
        this.checkNow = MakeButton("지금 확인", checkNow);
        this.checkNow.Margin = Padding.Empty;
        checks.Controls.Add(this.checkNow);
        layout.Controls.Add(checks, 0, 4);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(new Label { Text = safeMode ? "안전 모드 · PowerSI 실행과 제어가 차단됩니다" : ".spd 파일을 끌어 놓으세요 · 창을 닫아도 트레이에서 계속 실행됩니다", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 0) });
        var help = new LinkLabel { Text = "연동 안내", AutoSize = true, Margin = new Padding(4, 4, 0, 0), LinkColor = Color.FromArgb(40, 93, 171) };
        help.LinkClicked += (_, _) => ShowIntegrationHelp();
        footer.Controls.Add(help);
        layout.Controls.Add(footer, 0, 5);

        var rowMenu = new ContextMenuStrip();
        var jobDetails = new ToolStripMenuItem("작업 상세") { Enabled = false };
        jobDetails.Click += (_, _) => { if (jobDetails.Tag is Guid id) ShowJobDetails(id); };
        rowMenu.Items.Add(jobDetails);
        rowMenu.Items.Add("전체 경로 복사 (Ctrl+C)", null, (_, _) => CopyPaths());
        string? logDirectory = null;
        var openLogs = new ToolStripMenuItem("로그 폴더 열기") { Enabled = false };
        var manualComplete = new ToolStripMenuItem("수동 완료 처리…") { Enabled = false };
        manualComplete.Click += async (_, _) => await ReconcileSelectionAsync(remove: false);
        rowMenu.Items.Add(openLogs);
        rowMenu.Opening += (_, _) =>
        {
            Guid[] selected = SelectedIds().ToArray();
            JobItem[] selectedJobs = engine.Jobs.Where(j => selected.Contains(j.Id)).ToArray();
            manualComplete.Enabled = selected.Length > 0 && selectedJobs.Length == selected.Length &&
                selectedJobs.All(j => j.Status is JobStatus.NeedsAttention or JobStatus.Failed);
            jobDetails.Enabled = selected.Length == 1;
            jobDetails.Tag = selected.Length == 1 ? selected[0] : null;
            JobItem? job = selected.Length == 1 ? engine.Jobs.FirstOrDefault(j => j.Id == selected[0]) : null;
            logDirectory = job is null ? null : LogDirectory(job);
            openLogs.Enabled = !string.IsNullOrWhiteSpace(logDirectory) && Directory.Exists(logDirectory);
            openLogs.ToolTipText = logDirectory ?? "실행 기록이 있는 작업 하나를 선택하세요.";
        };
        openLogs.Click += (_, _) =>
        {
            string? directory = logDirectory;
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
            try
            {
                using var folder = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            { ShowError($"로그 폴더를 열지 못했습니다.\n{ex.Message}"); }
        };
        rowMenu.Items.Add("예약 변경 (F2)", null, async (_, _) => await ScheduleAsync());
        rowMenu.Items.Add("다시 대기", null, async (_, _) => await SelectedCommandAsync(engine.Retry));
        rowMenu.Items.Add("건너뛰기", null, async (_, _) => await SelectedCommandAsync(engine.Skip));
        rowMenu.Items.Add(manualComplete);
        rowMenu.Items.Add(new ToolStripSeparator());
        rowMenu.Items.Add("제거 (Delete)", null, async (_, _) => await ReconcileSelectionAsync(remove: true));
        grid.ContextMenuStrip = rowMenu;
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && !grid.Rows[e.RowIndex].Selected)
            {
                grid.ClearSelection();
                grid.Rows[e.RowIndex].Selected = true;
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[0];
            }
        };
        grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex == 1 && grid.Rows[e.RowIndex].Tag is Guid id) ShowJobDetails(id);
            else if (e.ColumnIndex == 2) await ScheduleAsync();
        };
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await AddPathsAsync(paths);
        };
        grid.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        grid.DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await AddPathsAsync(paths);
        };
        grid.AllowDrop = true;
        FormClosing += (_, e) => { if (!closeForExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        KeyDown += async (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.O) { e.SuppressKeyPress = true; await AddFilesAsync(); }
            else if (e.Control && e.KeyCode == Keys.C) { e.SuppressKeyPress = true; CopyPaths(); }
            else if (e.KeyCode == Keys.Delete) { e.SuppressKeyPress = true; await ReconcileSelectionAsync(remove: true); }
            else if (e.KeyCode == Keys.F2) { e.SuppressKeyPress = true; await ScheduleAsync(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Hide(); }
        };
        RefreshQueue();
    }

    private Button MakeButton(string text, Func<Task> action, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(68, 31), Height = 31, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 6, 0), Padding = new Padding(7, 0, 7, 0), BackColor = primary ? Color.FromArgb(38, 89, 159) : Color.White, ForeColor = primary ? Color.White : Color.FromArgb(39, 52, 67), AccessibleName = text };
        button.FlatAppearance.BorderColor = primary ? button.BackColor : Color.FromArgb(207, 215, 225);
        button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { ShowError(ex.Message); } };
        return button;
    }

    public void Reveal()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        grid.Focus();
    }

    public void ShowError(string message) => MessageBox.Show(this, message, "SPD Scheduler", MessageBoxButtons.OK, MessageBoxIcon.Information);
    public void SetExiting() { Enabled = false; status.Text = "진행 중인 확인을 마무리하고 종료합니다…"; }
    public void CloseForExit() { closeForExit = true; Close(); }

    public void RefreshQueue(DateTimeOffset? nextCheckAt = null, bool checkPending = false)
    {
        if (IsDisposed) return;
        start.Enabled = !engine.IsArmed;
        pause.Enabled = engine.IsArmed;
        checkNow.Enabled = nextCheckAt.HasValue && !checkPending;
        checkNow.Text = checkPending ? "확인 중…" : "지금 확인";
        string lastCheck = engine.LastCheckedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "아직 없음";
        string nextCheck = checkPending ? "확인 중…" : nextCheckAt?.ToLocalTime().ToString("HH:mm:ss") ?? "준비 중";
        checkTiming.Text = $"마지막 확인: {lastCheck}  ·  다음 확인: {nextCheck} ({engine.Settings.PollIntervalMinutes}분 간격)";
        status.Text = (safeMode ? "[안전 모드] " : "") + engine.StatusText;
        string signature = string.Join("\n", engine.Jobs.Select(j => $"{j.Id}|{j.FilePath}|{j.Status}|{j.CompletedManually}|{j.ScheduledAt:O}|{j.LastError}"));
        if (signature == queueSignature) return;
        queueSignature = signature;
        HashSet<Guid> selected = SelectedIds().ToHashSet();
        int scroll = grid.FirstDisplayedScrollingRowIndex;
        grid.Rows.Clear();
        foreach (JobItem job in engine.Jobs)
        {
            int rowIndex = grid.Rows.Add(job.FileName, StatusName(job), job.ScheduledAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "순서대로");
            DataGridViewRow row = grid.Rows[rowIndex];
            row.Tag = job.Id;
            row.Cells[0].ToolTipText = job.FilePath;
            row.Cells[1].ToolTipText = $"{job.LastError ?? StatusName(job)}\n로그 폴더: {LogDirectory(job)}\n두 번 클릭하면 작업 상세를 복사할 수 있습니다.";
            row.Cells[2].ToolTipText = job.ScheduledAt is { } at ? $"{at.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} ({job.ScheduleTimeZone ?? TimeZoneInfo.Local.Id})" : "예약 없이 대기 순서에 따라 실행";
            row.Selected = selected.Contains(job.Id);
            row.Cells[1].Style.ForeColor = job.Status switch
            {
                JobStatus.Completed => Color.FromArgb(25, 119, 76),
                JobStatus.Failed or JobStatus.NeedsAttention => Color.FromArgb(174, 62, 45),
                JobStatus.Starting or JobStatus.Running or JobStatus.Saving => Color.FromArgb(38, 89, 159),
                _ => Color.FromArgb(87, 98, 112)
            };
        }
        if (scroll >= 0 && grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = Math.Min(scroll, grid.Rows.Count - 1);
        grid.ResetMarquee();
    }

    private IEnumerable<Guid> SelectedIds() => grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).Select(r => (Guid)r.Tag!);

    private string LogDirectory(JobItem job)
    {
        string? directory = job.Run?.DirectoryPath;
        return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) ? Path.Combine(dataDirectory, "logs") : directory;
    }

    private void ShowJobDetails(Guid id)
    {
        JobItem? job = engine.Jobs.FirstOrDefault(j => j.Id == id);
        if (job is null) return;
        RunRecord? run = job.Run;
        string text = string.Join(Environment.NewLine,
            $"파일: {job.FilePath}",
            $"작업 ID: {job.Id}",
            $"상태: {StatusName(job)} ({job.Status})",
            $"수동 완료: {(job.CompletedManually ? "예" : "아니오")}",
            $"마지막 오류: {job.LastError ?? "없음"}",
            $"실행 실패 이유: {run?.FailureReason ?? "없음"}",
            $"실행 ID: {run?.Id.ToString() ?? "없음"}",
            $"PID: {run?.Process?.Pid.ToString() ?? "없음"}",
            $"실행 생성 시각: {run?.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "없음"}",
            $"프로세스 시작 시각: {run?.Process?.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "없음"}",
            $"작업 종료 시각: {job.FinishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "없음"}",
            $"로그 폴더: {LogDirectory(job)}");
        using var detail = new Form { Text = "작업 상세 · " + job.FileName, ClientSize = new Size(760, 440),
            StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, ShowInTaskbar = false };
        detail.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both, WordWrap = false, Text = text,
            AccessibleName = "작업 상세 (Ctrl+A, Ctrl+C로 복사)", BackColor = Color.White });
        detail.ShowDialog(this);
    }

    private async Task SelectedCommandAsync(Action<Guid> action)
    {
        Guid[] ids = SelectedIds().ToArray();
        if (ids.Length == 0) return;
        await command(() => { foreach (Guid id in ids) action(id); });
    }

    private async Task ReconcileSelectionAsync(bool remove)
    {
        Guid[] ids = SelectedIds().ToArray();
        var selected = engine.Jobs.Where(j => ids.Contains(j.Id))
            .Select(j => (j.Id, RunId: j.Run?.Id, j.FileName, j.Status)).ToArray();
        if (selected.Length == 0) return;
        if (selected.Any(j => j.Status is JobStatus.Starting or JobStatus.Running or JobStatus.Saving))
        { ShowError("시작·실행·저장 중인 작업은 수동 완료하거나 제거할 수 없습니다."); return; }
        if (!remove && selected.Any(j => j.Status is not (JobStatus.NeedsAttention or JobStatus.Failed)))
        { ShowError("확인 필요 또는 실패 작업만 수동 완료할 수 있습니다."); return; }
        var manual = selected.Where(j => !remove || j.Status == JobStatus.NeedsAttention).ToArray();
        if (manual.Length > 0)
        {
            string action = remove ? "확인 필요 작업 제거" : "수동 완료 처리";
            string instruction = remove
                ? "PowerSI 상태와 결과 저장 여부를 직접 확인한 뒤 목록에서 제거하세요."
                : "시뮬레이션 완료와 결과 저장을 직접 확인한 작업만 수동 완료 처리하세요.";
            using var confirm = new Form { Text = action, ClientSize = new Size(600, 330), Font = Font,
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
            confirm.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Vertical, BackColor = Color.White,
                Text = instruction + "\r\n\r\n앱은 목록만 변경하며 PowerSI 저장·종료를 실행하지 않습니다. 실행 기록과 로그는 보존합니다.\r\n대기열은 일시 중지되며, 다시 진행하려면 [시작]을 눌러야 합니다.\r\n\r\n선택한 작업:\r\n" +
                    string.Join("\r\n", selected.Select(j => $"• {j.FileName} ({StatusName(j.Status)})")), AccessibleName = "수동 처리 확인 내용" });
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45, Padding = new Padding(6), FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(new Button { Text = remove ? "제거" : "수동 완료", DialogResult = DialogResult.OK, AutoSize = true });
            confirm.Controls.Add(buttons);
            confirm.AcceptButton = cancel;
            confirm.CancelButton = cancel;
            if (confirm.ShowDialog(this) != DialogResult.OK) return;
        }
        await command(() =>
        {
            foreach (var job in selected)
                if (!remove || job.Status == JobStatus.NeedsAttention) engine.ResolveManually(job.Id, job.RunId, remove);
                else engine.Remove(job.Id);
        });
    }

    private async Task AddFilesAsync()
    {
        using var picker = new OpenFileDialog { Title = "SPD 파일 추가", Filter = "SPD 파일 (*.spd)|*.spd", Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog(this) == DialogResult.OK) await AddPathsAsync(picker.FileNames);
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        IReadOnlyList<string> rejected = [];
        await command(() => rejected = engine.AddFiles(paths));
        if (rejected.Count > 0 && !IsDisposed) ShowError("추가하지 못한 파일입니다. 파일 존재 여부, 확장자, 중복을 확인하세요.\n\n" + string.Join("\n", rejected.Take(8)) + (rejected.Count > 8 ? $"\n외 {rejected.Count - 8}개" : ""));
    }

    private async Task ScheduleAsync()
    {
        Guid[] ids = SelectedIds().ToArray();
        if (ids.Length == 0) return;
        JobItem? first = engine.Jobs.FirstOrDefault(j => j.Id == ids[0]);
        using var dialog = new Form { Text = "실행 예약", Font = Font, ClientSize = new Size(345, 180), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var label = new Label { Text = $"선택한 {ids.Length}개 파일의 실행 가능 시각", AutoSize = true, Location = new Point(18, 17) };
        var picker = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd  HH:mm:ss", Value = first?.ScheduledAt?.LocalDateTime ?? DateTime.Now.AddMinutes(1), Location = new Point(18, 48), Width = 306 };
        var note = new Label { Text = "PC의 현재 표준 시간대 기준입니다.\n예약 시각이 지나도 PowerSI가 사용 중이면 기다립니다.", AutoSize = true, Location = new Point(18, 81), ForeColor = Color.DimGray };
        var ok = new Button { Text = "예약", DialogResult = DialogResult.OK, Location = new Point(165, 137), Size = new Size(76, 29) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Location = new Point(249, 137), Size = new Size(76, 29) };
        dialog.Controls.AddRange([label, picker, note, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            if (TimeZoneInfo.Local.IsInvalidTime(picker.Value)) { ShowError("표준 시간대 변경으로 존재하지 않는 시각입니다. 다른 시각을 선택하세요."); return; }
            DateTimeOffset at = new(picker.Value);
            await command(() => { foreach (Guid id in ids) engine.SetSchedule(id, at); });
        }
    }

    private async Task SettingsAsync()
    {
        using var settings = new SettingsForm(engine.Settings, dataDirectory);
        if (settings.ShowDialog(this) == DialogResult.OK && settings.Result is { } result) await command(() => engine.UpdateSettings(result));
    }

    private void CopyPaths()
    {
        HashSet<Guid> selected = SelectedIds().ToHashSet();
        string paths = string.Join(Environment.NewLine, engine.Jobs.Where(j => selected.Contains(j.Id)).Select(j => j.FilePath));
        if (paths.Length == 0) return;
        try { Clipboard.SetText(paths); }
        catch (System.Runtime.InteropServices.ExternalException) { ShowError("클립보드가 사용 중입니다. 다시 시도하세요."); }
    }

    private void ShowIntegrationHelp() => MessageBox.Show(this,
        "PowerSI 연동은 실제 설치 환경에서 아직 검증되지 않았습니다.\n\n" +
        "설정에서 PowerSI 실행 파일과 사용 환경에 맞는 완료 판정 규칙을 확인하세요. 규칙은 SPD 내용, 실행 시작 표시, 완료 표시를 각각 식별해야 합니다. 근거가 없거나 읽을 수 없으면 자동으로 완료 처리하지 않습니다.\n\n" +
        "일시 중지는 다음 작업의 자동 시작을 멈춥니다. 현재 PowerSI 작업은 계속 실행됩니다. 앱 종료도 PowerSI를 강제로 종료하지 않습니다.\n\n" +
        "단축키: Ctrl+O 파일 추가 · Ctrl+C 전체 경로 복사 · F2 예약 · Delete 제거 · Esc 트레이로 숨기기",
        "PowerSI 연동 안내", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static string StatusName(JobItem job) => job.Status == JobStatus.Completed && job.CompletedManually ? "완료 (수동)" : StatusName(job.Status);

    private static string StatusName(JobStatus value) => value switch
    {
        JobStatus.Queued => "대기", JobStatus.Starting => "시작 중", JobStatus.Running => "실행 중", JobStatus.Saving => "저장 중",
        JobStatus.Completed => "완료", JobStatus.Failed => "실패", JobStatus.NeedsAttention => "확인 필요", _ => value.ToString()
    };
}

internal sealed class QueueGrid : DataGridView
{
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 33 };
    private readonly Dictionary<Guid, ScrollPosition> positions = [];
    private long lastFrame;
    private sealed class ScrollPosition { public float Offset; public bool Reverse; public long WaitUntil; }

    public QueueGrid()
    {
        DoubleBuffered = true;
        BackgroundColor = Color.White;
        BorderStyle = BorderStyle.FixedSingle;
        GridColor = Color.FromArgb(233, 237, 243);
        ReadOnly = true;
        MultiSelect = true;
        SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        RowHeadersVisible = false;
        AllowUserToAddRows = false;
        AllowUserToDeleteRows = false;
        AllowUserToResizeRows = false;
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        ScrollBars = ScrollBars.Vertical;
        ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable;
        EnableHeadersVisualStyles = false;
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(238, 242, 247), ForeColor = Color.FromArgb(59, 73, 91), Padding = new Padding(7, 0, 0, 0), SelectionBackColor = Color.FromArgb(238, 242, 247) };
        DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = Color.FromArgb(34, 46, 61), SelectionBackColor = Color.FromArgb(224, 235, 252), SelectionForeColor = Color.FromArgb(26, 57, 101), Padding = new Padding(7, 0, 5, 0), WrapMode = DataGridViewTriState.False };
        ColumnHeadersHeight = 34;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        RowTemplate.Height = 34;
        Columns.Add(new DataGridViewTextBoxColumn { Name = "File", HeaderText = "파일 이름", FillWeight = 64, MinimumWidth = 170, SortMode = DataGridViewColumnSortMode.NotSortable });
        Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "상태", FillWeight = 16, MinimumWidth = 90, SortMode = DataGridViewColumnSortMode.NotSortable });
        Columns.Add(new DataGridViewTextBoxColumn { Name = "Schedule", HeaderText = "실행 예약", FillWeight = 23, MinimumWidth = 125, SortMode = DataGridViewColumnSortMode.NotSortable });
        AccessibleName = "SPD 작업 대기열";
        animation.Tick += (_, _) => AdvanceMarquee();
        VisibleChanged += (_, _) => { if (Visible) { ResetMarquee(); animation.Start(); } else animation.Stop(); };
        ColumnWidthChanged += (_, _) => ResetMarquee();
        Scroll += (_, _) => InvalidateColumn(0);
        CellPainting += PaintFileName;
    }

    public void ResetMarquee() { positions.Clear(); lastFrame = Environment.TickCount64; InvalidateColumn(0); }

    private void AdvanceMarquee()
    {
        if (!Visible || !IsHandleCreated || FindForm()?.WindowState == FormWindowState.Minimized) return;
        long now = Environment.TickCount64;
        float step = Math.Min(100, now - lastFrame) * 0.03f * DeviceDpi / 96F;
        lastFrame = now;
        int first = FirstDisplayedScrollingRowIndex;
        if (first < 0) return;
        int end = Math.Min(Rows.Count, first + DisplayedRowCount(true));
        for (int index = first; index < end; index++)
        {
            DataGridViewRow row = Rows[index];
            if (row.Tag is not Guid id) continue;
            string text = row.Cells[0].Value?.ToString() ?? "";
            int overflow = TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width - Columns[0].Width + 16;
            if (overflow <= 0) { positions.Remove(id); continue; }
            if (!positions.TryGetValue(id, out ScrollPosition? position)) positions[id] = position = new ScrollPosition { WaitUntil = now + 1100 };
            if (now < position.WaitUntil) continue;
            position.Offset += position.Reverse ? -step : step;
            if (position.Offset >= overflow) { position.Offset = overflow; position.Reverse = true; position.WaitUntil = now + 1200; }
            else if (position.Offset <= 0) { position.Offset = 0; position.Reverse = false; position.WaitUntil = now + 1200; }
            System.Diagnostics.Debug.Assert(position.Offset >= 0 && position.Offset <= overflow);
            InvalidateCell(0, row.Index);
        }
    }

    private void PaintFileName(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 0 || e.Graphics is null || e.CellStyle is null) return;
        e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
        float offset = Rows[e.RowIndex].Tag is Guid id && positions.TryGetValue(id, out ScrollPosition? position) ? position.Offset : 0;
        Rectangle clip = Rectangle.Inflate(e.CellBounds, -8, -2);
        var saved = e.Graphics.Save();
        e.Graphics.SetClip(Rectangle.Intersect(clip, e.ClipBounds));
        Color color = (e.State & DataGridViewElementStates.Selected) != 0 ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor;
        TextRenderer.DrawText(e.Graphics, e.FormattedValue?.ToString() ?? "", e.CellStyle.Font ?? Font,
            new Rectangle(clip.X - (int)offset, clip.Y, Math.Max(clip.Width + (int)offset + 1000, 10000), clip.Height), color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
        e.Graphics.Restore(saved);
        e.Handled = true;
    }

    protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
}
