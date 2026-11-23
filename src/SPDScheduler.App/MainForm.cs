using SPDScheduler.Core;

namespace SPDScheduler.App;

internal sealed class MainForm : Form
{
    internal static readonly Color PanelColor = Color.FromArgb(0x2b, 0x2e, 0x34);
    internal static readonly Color PanelHoverColor = Color.FromArgb(0x36, 0x3a, 0x42);
    internal static readonly Color PanelPressedColor = Color.FromArgb(0x24, 0x27, 0x2c);
    internal static readonly Color PrimaryColor = Color.FromArgb(0x38, 0x6c, 0xb0);
    internal static readonly Color PrimaryHoverColor = Color.FromArgb(0x45, 0x7c, 0xc4);
    internal static readonly Color PrimaryPressedColor = Color.FromArgb(0x2d, 0x58, 0x91);
    internal static readonly Color BackgroundDark = Color.FromArgb(0x20, 0x22, 0x26);
    internal static readonly Color TextColor = Color.FromArgb(0xec, 0xee, 0xf1);
    internal static readonly Color BorderColor = Color.FromArgb(0x53, 0x59, 0x64);
    internal static readonly Color FocusColor = Color.FromArgb(0x8f, 0xc1, 0xff);
    internal static readonly Color SuccessColor = Color.FromArgb(143, 218, 170);
    internal static readonly Color DangerColor = Color.FromArgb(255, 172, 158);
    internal static readonly Color MutedTextColor = Color.FromArgb(0xb9, 0xc0, 0xca);
    private static readonly Color EditorColor = Color.FromArgb(0x15, 0x18, 0x1d);
    private readonly SchedulerEngine engine;
    private readonly bool safeMode;
    private readonly Func<Action, Task> command;
    private readonly Func<Task> checkNowAction;
    private readonly string dataDirectory;
    private readonly QueueGrid grid = new() { Dock = DockStyle.Fill };
    private readonly Label modeBadge = new() { AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(10, 6, 0, 0) };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
    private readonly Button start;
    private readonly Button pause;
    private readonly Button removeButton;
    private readonly Button scheduleButton;
    private readonly Button clearScheduleButton;
    private readonly Button clearCompletedButton;
    private readonly Button checkNow;
    private readonly Label checkTiming = new() { Dock = DockStyle.Fill, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = MutedTextColor, Margin = new Padding(0, 0, 8, 0) };
    private readonly ToolTip toolTip = new() { AutoPopDelay = 12_000, InitialDelay = 400, ReshowDelay = 200 };
    private string queueSignature = "";
    private bool checkPending;
    private bool closeForExit;

    public MainForm(SchedulerEngine engine, bool safeMode, Func<Action, Task> command, Func<Task> checkNow, string dataDirectory)
    {
        this.engine = engine;
        this.safeMode = safeMode;
        this.command = command;
        checkNowAction = checkNow;
        this.dataDirectory = dataDirectory;
        SuspendLayout();
        Text = "SPD Scheduler " + Application.ProductVersion.Split('+')[0] + (safeMode ? " · 안전 모드" : "");
        Font = new Font("맑은 고딕", 9F);
        ClientSize = new Size(760, 500);
        MinimumSize = new Size(650, 440);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = BackgroundDark;
        ForeColor = TextColor;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        KeyPreview = true;
        AllowDrop = true;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 12), ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var headerLeft = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var title = new Label { Text = "SPD Scheduler", Font = new Font(Font.FontFamily, 17F, FontStyle.Bold), AutoSize = true, Margin = Padding.Empty };
        modeBadge.Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold);
        headerLeft.Controls.AddRange([title, modeBadge]);
        header.Controls.Add(headerLeft, 0, 0);
        var execution = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        start = MakeButton("시작", async () => { await command(engine.Start); if (engine.IsArmed) await checkNow(); }, true);
        pause = MakeButton("일시 중지", async () => await command(engine.Pause));
        pause.Margin = new Padding(0, 0, 0, 6);
        execution.Controls.AddRange([start, pause]);
        header.Controls.Add(execution, 1, 0);
        layout.Controls.Add(header, 0, 0);

        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 4) };
        tools.Controls.Add(MakeButton("파일 추가", AddFilesAsync));
        removeButton = MakeButton("제거", () => ReconcileSelectionAsync(remove: true));
        tools.Controls.Add(removeButton);
        scheduleButton = MakeButton("예약", ScheduleAsync);
        tools.Controls.Add(scheduleButton);
        clearScheduleButton = MakeButton("예약 해제", () => SelectedCommandAsync(id => engine.SetSchedule(id, null)));
        tools.Controls.Add(clearScheduleButton);
        clearCompletedButton = MakeButton("완료 정리", async () => await command(() =>
        {
            foreach (Guid id in engine.Jobs.Where(j => j.Status == JobStatus.Completed).Select(j => j.Id).ToArray()) engine.Remove(id);
        }));
        tools.Controls.Add(clearCompletedButton);
        tools.Controls.Add(MakeButton("설정", SettingsAsync));
        tools.Controls.Add(MakeButton("감시 상세", () =>
        {
            ShowTextDialog("PowerSI/PowerDC 감시 상세", () => engine.MonitoringDetails, "PowerSI/PowerDC 감시 상세 (Ctrl+A, Ctrl+C로 복사)", allowRefresh: true);
            return Task.CompletedTask;
        }));
        layout.Controls.Add(tools, 0, 1);
        layout.Controls.Add(grid, 0, 2);
        layout.Controls.Add(status, 0, 3);

        var checks = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 0, 0, 5) };
        checks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        checks.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        checks.Controls.Add(checkTiming, 0, 0);
        this.checkNow = MakeButton("지금 확인", checkNow);
        this.checkNow.Margin = Padding.Empty;
        checks.Controls.Add(this.checkNow, 1, 0);
        layout.Controls.Add(checks, 0, 4);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(new Label { Text = safeMode ? "안전 모드 · PowerSI/PowerDC 실행과 제어 차단 · 기본 64 스레드" : ".spd 기본: PowerSI · 64 스레드 · 대기 작업 우클릭으로 프로그램·스레드·순서 변경\n창을 닫아도 트레이에서 계속 실행됩니다", AutoSize = true, ForeColor = MutedTextColor, Margin = new Padding(0, 4, 0, 0) });
        var help = new LinkLabel { Text = "연동 안내", AutoSize = true, Margin = new Padding(4, 4, 0, 0), LinkColor = FocusColor, ActiveLinkColor = TextColor };
        help.LinkClicked += (_, _) => ShowIntegrationHelp();
        footer.Controls.Add(help);
        layout.Controls.Add(footer, 0, 5);

        var rowMenu = new ContextMenuStrip();
        var jobDetails = new ToolStripMenuItem("작업 상세 (Enter)") { Enabled = false };
        jobDetails.Click += (_, _) => { if (jobDetails.Tag is Guid id) ShowJobDetails(id); };
        rowMenu.Items.Add(jobDetails);
        var executionTool = new ToolStripMenuItem("실행 프로그램") { Enabled = false };
        foreach (SimulationTool tool in Enum.GetValues<SimulationTool>())
        {
            var item = new ToolStripMenuItem(tool.ToString()) { Tag = tool };
            item.Click += async (_, _) =>
            {
                if (checkPending) { ShowError("상태 확인 중입니다. 확인이 끝난 뒤 실행 프로그램을 변경하세요."); return; }
                await SelectedCommandAsync(id => engine.SetTool(id, tool));
            };
            executionTool.DropDownItems.Add(item);
        }
        rowMenu.Items.Add(executionTool);
        var resources = new ToolStripMenuItem("리소스 사용 (스레드)") { Enabled = false };
        foreach (int threadCount in new[] { 16, 24, 32, 48, 64 })
        {
            var item = new ToolStripMenuItem(threadCount == 64 ? "64 스레드 (기본·최대)" : $"{threadCount} 스레드") { Tag = threadCount };
            item.Click += async (_, _) =>
            {
                if (checkPending) { ShowError("상태 확인 중입니다. 확인이 끝난 뒤 스레드 수를 변경하세요."); return; }
                await SelectedCommandAsync(id => engine.SetThreadCount(id, threadCount));
            };
            resources.DropDownItems.Add(item);
        }
        rowMenu.Items.Add(resources);
        var moveUp = new ToolStripMenuItem("순서 위로 (Alt+↑)") { Enabled = false };
        moveUp.Click += async (_, _) => await MoveSelectedAsync(-1);
        var moveDown = new ToolStripMenuItem("순서 아래로 (Alt+↓)") { Enabled = false };
        moveDown.Click += async (_, _) => await MoveSelectedAsync(1);
        rowMenu.Items.Add(moveUp);
        rowMenu.Items.Add(moveDown);
        rowMenu.Items.Add(new ToolStripSeparator());
        var copyPathsItem = new ToolStripMenuItem("전체 경로 복사 (Ctrl+C)") { Enabled = false };
        copyPathsItem.Click += (_, _) => CopyPaths();
        rowMenu.Items.Add(copyPathsItem);
        string? logDirectory = null;
        var openLogs = new ToolStripMenuItem("로그 폴더 열기") { Enabled = false };
        var scheduleItem = new ToolStripMenuItem("예약 변경 (F2)") { Enabled = false };
        scheduleItem.Click += async (_, _) => await ScheduleAsync();
        var clearScheduleItem = new ToolStripMenuItem("예약 해제") { Enabled = false };
        clearScheduleItem.Click += async (_, _) => await SelectedCommandAsync(id => engine.SetSchedule(id, null));
        var retryItem = new ToolStripMenuItem("다시 대기") { Enabled = false };
        retryItem.Click += async (_, _) => await SelectedCommandAsync(engine.Retry);
        var skipItem = new ToolStripMenuItem("건너뛰기") { Enabled = false };
        skipItem.Click += async (_, _) => await SelectedCommandAsync(engine.Skip);
        var manualComplete = new ToolStripMenuItem("수동 완료 처리…") { Enabled = false };
        manualComplete.Click += async (_, _) => await ReconcileSelectionAsync(remove: false);
        var removeItem = new ToolStripMenuItem("제거 (Delete)") { Enabled = false };
        removeItem.Click += async (_, _) => await ReconcileSelectionAsync(remove: true);
        rowMenu.Items.Add(openLogs);
        rowMenu.Items.Add(new ToolStripSeparator());
        rowMenu.Items.Add(scheduleItem);
        rowMenu.Items.Add(clearScheduleItem);
        rowMenu.Items.Add(retryItem);
        rowMenu.Items.Add(skipItem);
        rowMenu.Items.Add(manualComplete);
        rowMenu.Items.Add(new ToolStripSeparator());
        rowMenu.Items.Add(removeItem);
        rowMenu.Opening += (_, _) =>
        {
            Guid[] selected = SelectedIds().ToArray();
            JobItem[] selectedJobs = engine.Jobs.Where(j => selected.Contains(j.Id)).ToArray();
            bool hasSelection = selected.Length > 0 && selectedJobs.Length == selected.Length;
            bool allQueued = hasSelection && selectedJobs.All(j => j.Status == JobStatus.Queued);
            manualComplete.Enabled = hasSelection && selectedJobs.All(j => j.Status is JobStatus.Running or JobStatus.NeedsAttention or JobStatus.Failed);
            executionTool.Enabled = allQueued && !checkPending;
            foreach (ToolStripMenuItem item in executionTool.DropDownItems)
                item.Checked = hasSelection && selectedJobs.All(j => j.Tool == (SimulationTool)item.Tag!);
            resources.Enabled = allQueued && !checkPending;
            foreach (ToolStripMenuItem item in resources.DropDownItems)
                item.Checked = hasSelection && selectedJobs.All(j => j.ThreadCount == (int)item.Tag!);
            moveUp.Enabled = selected.Length == 1 && allQueued && !checkPending && CanMoveQueued(selected[0], -1);
            moveDown.Enabled = selected.Length == 1 && allQueued && !checkPending && CanMoveQueued(selected[0], 1);
            copyPathsItem.Enabled = hasSelection;
            scheduleItem.Enabled = allQueued;
            clearScheduleItem.Enabled = allQueued && selectedJobs.Any(j => j.ScheduledAt.HasValue);
            retryItem.Enabled = hasSelection && selectedJobs.All(j => j.Status is JobStatus.Failed or JobStatus.NeedsAttention);
            skipItem.Enabled = hasSelection && selectedJobs.All(j => j.Status is JobStatus.Failed or JobStatus.NeedsAttention);
            removeItem.Enabled = hasSelection && selectedJobs.All(j => j.Status is not (JobStatus.Starting or JobStatus.Saving));
            jobDetails.Enabled = selected.Length == 1;
            jobDetails.Tag = selected.Length == 1 ? selected[0] : null;
            JobItem? job = selected.Length == 1 ? engine.Jobs.FirstOrDefault(j => j.Id == selected[0]) : null;
            logDirectory = job is null ? null : LogDirectory(job);
            openLogs.Enabled = !string.IsNullOrWhiteSpace(logDirectory) && Directory.Exists(logDirectory);
            openLogs.ToolTipText = logDirectory ?? "실행 기록이 있는 작업 하나를 선택하세요.";
        };
        openLogs.Click += (_, _) => OpenDirectory(logDirectory, "로그 폴더");
        grid.ContextMenuStrip = rowMenu;
        grid.SelectionChanged += (_, _) => UpdateSelectionActionStates();
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
            if (e.ColumnIndex is 0 or 1 && grid.Rows[e.RowIndex].Tag is Guid id) ShowJobDetails(id);
            else if (e.ColumnIndex == 2) await ScheduleAsync();
            else if (e.ColumnIndex is 3 or 4) rowMenu.Show(grid, grid.PointToClient(Cursor.Position));
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
            else if (e.Control && e.KeyCode == Keys.A) { e.SuppressKeyPress = true; grid.SelectAll(); }
            else if (e.Alt && e.KeyCode == Keys.Up) { e.SuppressKeyPress = true; await MoveSelectedAsync(-1); }
            else if (e.Alt && e.KeyCode == Keys.Down) { e.SuppressKeyPress = true; await MoveSelectedAsync(1); }
            else if (e.KeyCode == Keys.Enter && SelectedIds().Take(2).ToArray() is [var selectedId]) { e.SuppressKeyPress = true; ShowJobDetails(selectedId); }
            else if (e.KeyCode == Keys.Delete) { e.SuppressKeyPress = true; await ReconcileSelectionAsync(remove: true); }
            else if (e.KeyCode == Keys.F2) { e.SuppressKeyPress = true; await ScheduleAsync(); }
            else if (e.KeyCode == Keys.F5 && this.checkNow.Enabled) { e.SuppressKeyPress = true; await checkNowAction(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Hide(); }
        };
        ResumeLayout();
        RefreshQueue();
    }

    private Button MakeButton(string text, Func<Task> action, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(68, 31), Height = 31, FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 6, 6), Padding = new Padding(7, 0, 7, 0),
            BackColor = primary ? PrimaryColor : PanelColor, ForeColor = primary ? Color.White : TextColor,
            Cursor = Cursors.Hand, AccessibleName = text
        };
        Color border = primary ? PrimaryColor : BorderColor;
        button.FlatAppearance.BorderColor = border;
        button.FlatAppearance.MouseOverBackColor = primary ? PrimaryHoverColor : PanelHoverColor;
        button.FlatAppearance.MouseDownBackColor = primary ? PrimaryPressedColor : PanelPressedColor;
        button.GotFocus += (_, _) => button.FlatAppearance.BorderColor = FocusColor;
        button.LostFocus += (_, _) => button.FlatAppearance.BorderColor = border;
        button.Paint += (_, e) =>
        {
            if (button.Enabled) return;
            using var background = new SolidBrush(PanelColor);
            e.Graphics.FillRectangle(background, Rectangle.Inflate(button.ClientRectangle, -1, -1));
            TextRenderer.DrawText(e.Graphics, button.Text, button.Font, button.ClientRectangle, MutedTextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        };
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
        this.checkPending = checkPending;
        start.Enabled = !engine.IsArmed;
        pause.Enabled = engine.IsArmed;
        checkNow.Enabled = nextCheckAt.HasValue && !checkPending;
        checkNow.Text = checkPending ? "확인 중…" : "지금 확인";

        int queuedCount = engine.Jobs.Count(j => j.Status == JobStatus.Queued);
        int activeCount = engine.Jobs.Count(j => j.Status is JobStatus.Starting or JobStatus.Running or JobStatus.Saving);
        int attentionCount = engine.Jobs.Count(j => j.Status == JobStatus.NeedsAttention);
        int completedCount = engine.Jobs.Count(j => j.Status == JobStatus.Completed);
        int failedCount = engine.Jobs.Count(j => j.Status == JobStatus.Failed);
        UpdateModeBadge(activeCount, attentionCount, failedCount);

        var summaryParts = new List<string>();
        if (activeCount > 0) summaryParts.Add($"실행 {activeCount}");
        if (queuedCount > 0) summaryParts.Add($"대기 {queuedCount}");
        if (completedCount > 0) summaryParts.Add($"완료 {completedCount}");
        if (failedCount > 0) summaryParts.Add($"실패 {failedCount}");
        if (attentionCount > 0) summaryParts.Add($"확인 필요 {attentionCount}");
        string summaryPrefix = summaryParts.Count > 0 ? string.Join(" · ", summaryParts) + "  |  " : "";

        string lastCheck = engine.LastCheckedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "아직 없음";
        string nextCheck = checkPending ? "확인 중…" : nextCheckAt?.ToLocalTime().ToString("HH:mm:ss") ?? "준비 중";
        string timingText = $"{summaryPrefix}마지막 확인: {lastCheck} · 다음 확인: {nextCheck} ({engine.Settings.PollIntervalMinutes}분)";
        checkTiming.Text = timingText;
        toolTip.SetToolTip(checkTiming, timingText);

        string statusText = (safeMode ? "[안전 모드] " : "") + engine.StatusText;
        status.Text = statusText;
        status.ForeColor = attentionCount > 0 || statusText.Contains("실패") || statusText.Contains("확인 필요") ? DangerColor
            : activeCount > 0 || engine.IsArmed ? FocusColor
            : completedCount > 0 && queuedCount == 0 && failedCount == 0 ? SuccessColor
            : TextColor;
        toolTip.SetToolTip(status, statusText);
        clearCompletedButton.Enabled = completedCount > 0;

        string signature = string.Join("\n", engine.Jobs.Select(j => $"{j.Id}|{j.Order}|{j.FilePath}|{j.Tool}|{j.ThreadCount}|{j.Status}|{j.CompletedManually}|{j.ScheduledAt:O}|{j.Run?.Id}|{j.LastError}"));
        if (signature == queueSignature)
        {
            UpdateSelectionActionStates();
            return;
        }
        queueSignature = signature;
        HashSet<Guid> selected = SelectedIds().ToHashSet();
        int scroll = grid.FirstDisplayedScrollingRowIndex;
        grid.Rows.Clear();
        foreach (JobItem job in engine.Jobs)
        {
            int rowIndex = grid.Rows.Add(job.FileName, StatusName(job), job.ScheduledAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "순서대로", job.Tool, job.ThreadCount);
            DataGridViewRow row = grid.Rows[rowIndex];
            row.Tag = job.Id;
            row.Cells[0].ToolTipText = job.FilePath;
            row.Cells[1].ToolTipText = $"{job.LastError ?? StatusName(job)}\n로그 폴더: {LogDirectory(job)}\n두 번 클릭하거나 Enter를 누르면 작업 상세를 볼 수 있습니다.";
            row.Cells[2].ToolTipText = job.ScheduledAt is { } at ? $"{at.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} ({job.ScheduleTimeZone ?? TimeZoneInfo.Local.Id})" : "예약 없이 대기 순서에 따라 실행";
            row.Cells[3].ToolTipText = "기본 PowerSI · 대기 작업만 우클릭 → 실행 프로그램에서 변경할 수 있습니다.";
            row.Cells[4].ToolTipText = $"설정: {job.ThreadCount} 스레드 · 다음 시뮬레이션 시작 전에 적용\n대기 작업만 우클릭 → 리소스 사용 (스레드)에서 변경할 수 있습니다.\n실제 CPU 점유율을 고정하는 설정은 아닙니다.";
            row.Selected = selected.Contains(job.Id);
            row.Cells[1].Style.ForeColor = job.Status switch
            {
                JobStatus.Completed => SuccessColor,
                JobStatus.Failed or JobStatus.NeedsAttention => DangerColor,
                JobStatus.Starting or JobStatus.Running or JobStatus.Saving => FocusColor,
                _ => MutedTextColor
            };
        }
        if (scroll >= 0 && grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = Math.Min(scroll, grid.Rows.Count - 1);
        grid.ResetMarquee();
        UpdateSelectionActionStates();
    }

    private void UpdateModeBadge(int activeCount, int attentionCount, int failedCount)
    {
        if (safeMode)
        {
            modeBadge.Text = "안전 모드";
            modeBadge.BackColor = Color.FromArgb(0x45, 0x3b, 0x26);
            modeBadge.ForeColor = Color.FromArgb(0xf6, 0xd1, 0x7a);
        }
        else if (attentionCount > 0)
        {
            modeBadge.Text = "확인 필요";
            modeBadge.BackColor = Color.FromArgb(0x52, 0x2c, 0x2a);
            modeBadge.ForeColor = DangerColor;
        }
        else if (activeCount > 0)
        {
            modeBadge.Text = engine.IsArmed ? "실행 중 · 자동" : "실행 중 · 일시 중지";
            modeBadge.BackColor = Color.FromArgb(0x23, 0x3b, 0x57);
            modeBadge.ForeColor = FocusColor;
        }
        else if (engine.IsArmed)
        {
            modeBadge.Text = "자동 진행 중";
            modeBadge.BackColor = Color.FromArgb(0x23, 0x42, 0x34);
            modeBadge.ForeColor = SuccessColor;
        }
        else if (failedCount > 0 && engine.Jobs.All(j => j.Status is JobStatus.Completed or JobStatus.Failed))
        {
            modeBadge.Text = "실패 있음";
            modeBadge.BackColor = Color.FromArgb(0x52, 0x2c, 0x2a);
            modeBadge.ForeColor = DangerColor;
        }
        else
        {
            modeBadge.Text = "일시 중지";
            modeBadge.BackColor = PanelColor;
            modeBadge.ForeColor = MutedTextColor;
        }
    }

    private void UpdateSelectionActionStates()
    {
        Guid[] selected = SelectedIds().ToArray();
        JobItem[] selectedJobs = engine.Jobs.Where(j => selected.Contains(j.Id)).ToArray();
        bool hasSelection = selected.Length > 0 && selectedJobs.Length == selected.Length;
        bool allQueued = hasSelection && selectedJobs.All(j => j.Status == JobStatus.Queued);
        removeButton.Enabled = hasSelection && selectedJobs.All(j => j.Status is not (JobStatus.Starting or JobStatus.Saving));
        scheduleButton.Enabled = allQueued;
        clearScheduleButton.Enabled = allQueued && selectedJobs.Any(j => j.ScheduledAt.HasValue);
    }

    private bool CanMoveQueued(Guid id, int direction)
    {
        int index = -1;
        for (int i = 0; i < engine.Jobs.Count; i++)
        {
            if (engine.Jobs[i].Id == id) { index = i; break; }
        }
        if (index < 0 || engine.Jobs[index].Status != JobStatus.Queued) return false;
        int step = direction < 0 ? -1 : 1;
        for (int i = index + step; i >= 0 && i < engine.Jobs.Count; i += step)
            if (engine.Jobs[i].Status == JobStatus.Queued) return true;
        return false;
    }

    private async Task MoveSelectedAsync(int direction)
    {
        Guid[] ids = SelectedIds().ToArray();
        if (ids.Length != 1) return;
        if (checkPending) { ShowError("상태 확인 중입니다. 확인이 끝난 뒤 순서를 변경하세요."); return; }
        if (!CanMoveQueued(ids[0], direction)) return;
        await command(() => engine.MoveQueued(ids[0], direction));
        if (IsDisposed) return;
        grid.ClearSelection();
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Tag is Guid rowId && rowId == ids[0])
            {
                row.Selected = true;
                grid.CurrentCell = row.Cells[0];
                break;
            }
        }
    }

    private IEnumerable<Guid> SelectedIds() => grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).Select(r => (Guid)r.Tag!);

    private string LogDirectory(JobItem job)
    {
        string? directory = job.Run?.DirectoryPath;
        return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) ? Path.Combine(dataDirectory, "logs") : directory;
    }

    private void OpenDirectory(string? directory, string label)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        try
        {
            using var folder = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        { ShowError($"{label}를 열지 못했습니다.\n{ex.Message}"); }
    }

    private void ShowTextDialog(string title, Func<string> getText, string accessibleName, bool allowRefresh = false, string? openFolderPath = null)
    {
        using var detail = new Form
        {
            Text = title, ClientSize = new Size(760, 460), MinimumSize = new Size(520, 320),
            StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, ShowInTaskbar = false,
            AutoScaleDimensions = new SizeF(96F, 96F), AutoScaleMode = AutoScaleMode.Dpi,
            BackColor = BackgroundDark, ForeColor = TextColor
        };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false,
            Text = getText(), AccessibleName = accessibleName, BackColor = EditorColor, ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 10)
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty
        };
        var close = MakeDialogButton("닫기", primary: false);
        close.DialogResult = DialogResult.Cancel;
        close.Margin = Padding.Empty;
        var copy = MakeDialogButton("클립보드 복사", primary: true);
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(box.Text);
                copy.Text = "복사됨";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                ShowError("클립보드가 사용 중입니다. 다시 시도하세요.");
            }
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(copy);
        if (allowRefresh)
        {
            var refresh = MakeDialogButton("새로 고침", primary: false);
            refresh.Click += (_, _) =>
            {
                box.Text = getText();
                copy.Text = "클립보드 복사";
            };
            buttons.Controls.Add(refresh);
        }
        if (!string.IsNullOrWhiteSpace(openFolderPath) && Directory.Exists(openFolderPath))
        {
            var openFolder = MakeDialogButton("로그 폴더 열기", primary: false);
            openFolder.Click += (_, _) => OpenDirectory(openFolderPath, "로그 폴더");
            buttons.Controls.Add(openFolder);
        }
        root.Controls.Add(box, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        detail.Controls.Add(root);
        detail.CancelButton = close;
        detail.ShowDialog(this);
    }

    private Button MakeDialogButton(string text, bool primary)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(76, 30), Height = 30, FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 6, 0), Padding = new Padding(8, 0, 8, 0),
            BackColor = primary ? PrimaryColor : PanelColor, ForeColor = primary ? Color.White : TextColor,
            Cursor = Cursors.Hand
        };
        Color border = primary ? PrimaryColor : BorderColor;
        button.FlatAppearance.BorderColor = border;
        button.FlatAppearance.MouseOverBackColor = primary ? PrimaryHoverColor : PanelHoverColor;
        button.FlatAppearance.MouseDownBackColor = primary ? PrimaryPressedColor : PanelPressedColor;
        button.GotFocus += (_, _) => button.FlatAppearance.BorderColor = FocusColor;
        button.LostFocus += (_, _) => button.FlatAppearance.BorderColor = border;
        return button;
    }

    private void ShowJobDetails(Guid id)
    {
        JobItem? job = engine.Jobs.FirstOrDefault(j => j.Id == id);
        if (job is null) return;
        RunRecord? run = job.Run;
        string logDir = LogDirectory(job);
        string text = string.Join(Environment.NewLine,
            $"파일: {job.FilePath}",
            $"작업 ID: {job.Id}",
            $"실행 프로그램: {job.Tool}",
            $"리소스 설정: {job.ThreadCount} 스레드 (다음 시뮬레이션 시작 전 적용)",
            $"상태: {StatusName(job)} ({job.Status})",
            $"수동 완료: {(job.CompletedManually ? "예" : "아니오")}",
            $"마지막 오류: {job.LastError ?? "없음"}",
            $"실행 실패 이유: {run?.FailureReason ?? "없음"}",
            $"실행 ID: {run?.Id.ToString() ?? "없음"}",
            $"PID: {run?.Process?.Pid.ToString() ?? "없음"}",
            $"실행 생성 시각: {run?.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "없음"}",
            $"프로세스 시작 시각: {run?.Process?.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "없음"}",
            $"작업 종료 시각: {job.FinishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "없음"}",
            $"로그 폴더: {logDir}");
        ShowTextDialog("작업 상세 · " + job.FileName, () => text, "작업 상세 (Ctrl+A, Ctrl+C로 복사)", openFolderPath: logDir);
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
        if (selected.Any(j => j.Status is JobStatus.Starting or JobStatus.Saving))
        { ShowError("시작·저장 중인 작업은 수동 완료하거나 제거할 수 없습니다."); return; }
        if (!remove && selected.Any(j => j.Status is not (JobStatus.Running or JobStatus.NeedsAttention or JobStatus.Failed)))
        { ShowError("실행 중, 확인 필요 또는 실패 작업만 수동 완료할 수 있습니다."); return; }
        var manual = selected.Where(j => !remove || j.Status is JobStatus.Running or JobStatus.NeedsAttention).ToArray();
        if (manual.Length > 0)
        {
            string action = remove ? "작업 수동 제거" : "수동 완료 처리";
            string instruction = remove
                ? "PowerSI/PowerDC 상태와 결과 저장 여부를 직접 확인한 뒤 목록에서 제거하세요."
                : "시뮬레이션 완료와 결과 저장을 직접 확인한 작업만 수동 완료 처리하세요.";
            using var confirm = new Form
            {
                Text = action, ClientSize = new Size(600, 340), MinimumSize = new Size(460, 260), Font = Font,
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                AutoScaleDimensions = new SizeF(96F, 96F), AutoScaleMode = AutoScaleMode.Dpi,
                BackColor = BackgroundDark, ForeColor = TextColor
            };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 2 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new TextBox
            {
                Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
                BackColor = EditorColor, ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 10),
                Text = instruction + "\r\n\r\n앱은 목록만 변경하며 PowerSI/PowerDC 저장·종료를 실행하지 않습니다. 실행 기록과 로그는 보존합니다.\r\n대기열은 일시 중지되며, 다시 진행하려면 [시작]을 눌러야 합니다.\r\n\r\n선택한 작업:\r\n" +
                    string.Join("\r\n", selected.Select(j => $"• {j.FileName} ({StatusName(j.Status)})")),
                AccessibleName = "수동 처리 확인 내용"
            }, 0, 0);
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty
            };
            var cancel = MakeDialogButton("취소", primary: false);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Margin = Padding.Empty;
            var proceed = MakeDialogButton(remove ? "제거" : "수동 완료", primary: true);
            proceed.DialogResult = DialogResult.OK;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(proceed);
            root.Controls.Add(buttons, 0, 1);
            confirm.Controls.Add(root);
            confirm.AcceptButton = cancel;
            confirm.CancelButton = cancel;
            if (confirm.ShowDialog(this) != DialogResult.OK) return;
        }
        await command(() =>
        {
            foreach (var job in selected)
                if (!remove || job.Status is JobStatus.Running or JobStatus.NeedsAttention) engine.ResolveManually(job.Id, job.RunId, remove);
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
        JobItem[] selectedJobs = engine.Jobs.Where(j => ids.Contains(j.Id)).ToArray();
        if (selectedJobs.Length != ids.Length || selectedJobs.Any(j => j.Status != JobStatus.Queued))
        {
            ShowError("대기 작업만 예약을 변경할 수 있습니다.");
            return;
        }
        JobItem first = selectedJobs[0];
        using var dialog = new Form
        {
            Text = "실행 예약", Font = Font, ClientSize = new Size(400, 225), MinimumSize = new Size(370, 215),
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            AutoScaleDimensions = new SizeF(96F, 96F), AutoScaleMode = AutoScaleMode.Dpi,
            BackColor = BackgroundDark, ForeColor = TextColor
        };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var label = new Label { Text = $"선택한 {ids.Length}개 파일의 실행 가능 시각", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        var picker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd  HH:mm:ss",
            Value = first.ScheduledAt?.LocalDateTime ?? DateTime.Now.AddMinutes(10),
            Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8)
        };
        var presets = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        void AddPreset(string caption, Func<DateTime> compute)
        {
            var button = MakeDialogButton(caption, primary: false);
            button.MinimumSize = new Size(64, 26);
            button.Height = 26;
            button.Padding = new Padding(6, 0, 6, 0);
            button.Margin = new Padding(0, 0, 6, 4);
            button.Click += (_, _) => picker.Value = compute();
            presets.Controls.Add(button);
        }
        AddPreset("+10분", () => DateTime.Now.AddMinutes(10));
        AddPreset("+30분", () => DateTime.Now.AddMinutes(30));
        AddPreset("+1시간", () => DateTime.Now.AddHours(1));
        AddPreset("내일 09:00", () => DateTime.Today.AddDays(1).AddHours(9));

        var note = new Label
        {
            Text = "PC의 현재 표준 시간대 기준입니다.\n예약 이후에도 시뮬레이션 사용 중이면 기다립니다.",
            AutoSize = true, ForeColor = MutedTextColor, Margin = new Padding(0, 0, 0, 12)
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty
        };
        var cancel = MakeDialogButton("취소", primary: false);
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Margin = Padding.Empty;
        var ok = MakeDialogButton("예약", primary: true);
        ok.DialogResult = DialogResult.OK;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        root.Controls.Add(label, 0, 0);
        root.Controls.Add(picker, 0, 1);
        root.Controls.Add(presets, 0, 2);
        root.Controls.Add(note, 0, 3);
        root.Controls.Add(buttons, 0, 4);
        dialog.Controls.Add(root);
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
        "PowerDC 2025.1 단일 보드 IR Drop은 영문 파일명 기준 실행·저장·종료를 확인했으며, 기타 PowerSI/PowerDC 모드는 사용 환경에 맞는 검증이 필요합니다.\n\n" +
        "설정에서 사용할 PowerSI/PowerDC 실행 파일과 작업별 완료 판정 규칙을 확인하세요. 근거가 없거나 물리 디스크 활성 시간이 80% 이상이면 자동 완료·다음 작업 시작을 보류합니다.\n\n" +
        "일시 중지는 다음 작업의 자동 시작을 멈춥니다. 현재 PowerSI/PowerDC 작업은 계속 실행됩니다. 앱 종료도 PowerSI/PowerDC를 강제로 종료하지 않습니다.\n\n" +
        "새 SPD는 PowerSI · 64 스레드로 추가됩니다. 대기 작업을 우클릭해 [실행 프로그램], [리소스 사용 (스레드)], [순서 위로/아래로]를 변경할 수 있습니다. PowerDC 결과 XML은 작업별 로그 폴더에 저장합니다.\n\n" +
        "단축키: Ctrl+O 파일 추가 · Ctrl+C 경로 복사 · Ctrl+A 전체 선택 · Enter 작업 상세 · F2 예약 · F5 지금 확인 · Alt+↑/↓ 순서 이동 · Delete 제거 · Esc 트레이로 숨기기",
        "PowerSI/PowerDC 연동 안내", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static string StatusName(JobItem job) => job.Status == JobStatus.Completed && job.CompletedManually ? "완료 (수동)" : StatusName(job.Status);

    private static string StatusName(JobStatus value) => value switch
    {
        JobStatus.Queued => "대기", JobStatus.Starting => "시작 중", JobStatus.Running => "실행 중", JobStatus.Saving => "저장 중",
        JobStatus.Completed => "완료", JobStatus.Failed => "실패", JobStatus.NeedsAttention => "확인 필요", _ => value.ToString()
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) toolTip.Dispose();
        base.Dispose(disposing);
    }
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
        BackgroundColor = Color.FromArgb(0x1d, 0x20, 0x25);
        BorderStyle = BorderStyle.FixedSingle;
        GridColor = MainForm.BorderColor;
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
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = MainForm.PanelColor, ForeColor = MainForm.TextColor, Padding = new Padding(7, 0, 0, 0), SelectionBackColor = MainForm.PanelColor, SelectionForeColor = MainForm.TextColor };
        DefaultCellStyle = new DataGridViewCellStyle { BackColor = BackgroundColor, ForeColor = MainForm.TextColor, SelectionBackColor = Color.FromArgb(0x29, 0x4e, 0x7c), SelectionForeColor = Color.White, Padding = new Padding(7, 0, 5, 0), WrapMode = DataGridViewTriState.False };
        AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(0x22, 0x25, 0x2b), ForeColor = MainForm.TextColor, SelectionBackColor = Color.FromArgb(0x29, 0x4e, 0x7c), SelectionForeColor = Color.White, Padding = new Padding(7, 0, 5, 0), WrapMode = DataGridViewTriState.False };
        ColumnHeadersHeight = 34;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        RowTemplate.Height = 34;
        Columns.Add(new DataGridViewTextBoxColumn { Name = "File", HeaderText = "파일 이름", FillWeight = 64, MinimumWidth = 170, SortMode = DataGridViewColumnSortMode.NotSortable });
        Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "상태", FillWeight = 16, MinimumWidth = 90, SortMode = DataGridViewColumnSortMode.NotSortable });
        Columns.Add(new DataGridViewTextBoxColumn { Name = "Schedule", HeaderText = "실행 예약", FillWeight = 23, MinimumWidth = 125, SortMode = DataGridViewColumnSortMode.NotSortable });
        Columns.Add(new DataGridViewTextBoxColumn { Name = "Tool", HeaderText = "실행 프로그램", FillWeight = 18, MinimumWidth = 100, SortMode = DataGridViewColumnSortMode.NotSortable });
        Columns[3].DisplayIndex = 1;
        Columns.Add(new DataGridViewTextBoxColumn { Name = "Threads", HeaderText = "스레드", FillWeight = 12, MinimumWidth = 70, SortMode = DataGridViewColumnSortMode.NotSortable });
        Columns[4].DisplayIndex = 2;
        AccessibleName = "SPD 작업 대기열";
        animation.Tick += (_, _) => AdvanceMarquee();
        VisibleChanged += (_, _) => { if (Visible) ResetMarquee(); else animation.Stop(); };
        ColumnWidthChanged += (_, _) => ResetMarquee();
        Resize += (_, _) => ResetMarquee();
        Scroll += (_, _) => { InvalidateColumn(0); EnsureAnimationRunning(); };
        CellPainting += PaintFileName;
    }

    public void ResetMarquee()
    {
        positions.Clear();
        lastFrame = Environment.TickCount64;
        InvalidateColumn(0);
        EnsureAnimationRunning();
    }

    private void EnsureAnimationRunning()
    {
        if (!Visible || Rows.Count == 0) { animation.Stop(); return; }
        if (!animation.Enabled) { lastFrame = Environment.TickCount64; animation.Start(); }
    }

    private void AdvanceMarquee()
    {
        if (!Visible || !IsHandleCreated || FindForm()?.WindowState == FormWindowState.Minimized) return;
        long now = Environment.TickCount64;
        float step = Math.Min(100, now - lastFrame) * 0.03f * DeviceDpi / 96F;
        lastFrame = now;
        int first = FirstDisplayedScrollingRowIndex;
        if (first < 0) { animation.Stop(); return; }
        int end = Math.Min(Rows.Count, first + DisplayedRowCount(true));
        bool anyOverflow = false;
        for (int index = first; index < end; index++)
        {
            DataGridViewRow row = Rows[index];
            if (row.Tag is not Guid id) continue;
            string text = row.Cells[0].Value?.ToString() ?? "";
            int overflow = TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width - Columns[0].Width + 16;
            if (overflow <= 0) { positions.Remove(id); continue; }
            anyOverflow = true;
            if (!positions.TryGetValue(id, out ScrollPosition? position)) positions[id] = position = new ScrollPosition { WaitUntil = now + 1100 };
            if (now < position.WaitUntil) continue;
            position.Offset += position.Reverse ? -step : step;
            if (position.Offset >= overflow) { position.Offset = overflow; position.Reverse = true; position.WaitUntil = now + 1200; }
            else if (position.Offset <= 0) { position.Offset = 0; position.Reverse = false; position.WaitUntil = now + 1200; }
            System.Diagnostics.Debug.Assert(position.Offset >= 0 && position.Offset <= overflow);
            InvalidateCell(0, row.Index);
        }
        if (!anyOverflow) animation.Stop();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Rows.Count > 0) return;
        Rectangle area = new(12, ColumnHeadersHeight + 12, Math.Max(0, ClientSize.Width - 24), Math.Max(0, ClientSize.Height - ColumnHeadersHeight - 24));
        if (area.Width <= 0 || area.Height <= 0) return;
        TextRenderer.DrawText(e.Graphics,
            "여기에 .spd 파일을 끌어다 놓거나 [파일 추가] (Ctrl+O)로 작업을 등록하세요.\n새 파일은 PowerSI · 64 스레드로 추가되며, 대기 행을 우클릭해 변경할 수 있습니다.",
            Font, area, MainForm.MutedTextColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
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
