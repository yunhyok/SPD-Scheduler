using System.Text.RegularExpressions;
using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.App;

internal sealed class SettingsForm : Form
{
    private readonly TextBox executable = new() { Dock = DockStyle.Fill, AccessibleName = "PowerSI 실행 파일 경로" };
    private readonly NumericUpDown idleSeconds;
    private readonly NumericUpDown cpu;
    private readonly NumericUpDown memory;
    private readonly NumericUpDown io;
    private readonly NumericUpDown timeout;
    private readonly NumericUpDown polling;
    private readonly CheckBox allowBbs;
    private readonly DataGridView rules;
    public AppSettings? Result { get; private set; }

    public SettingsForm(AppSettings current, string dataDirectory)
    {
        Text = "SPD Scheduler 설정";
        Font = new Font("맑은 고딕", 9F);
        ClientSize = new Size(716, 602);
        MinimumSize = new Size(690, 602);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Color.FromArgb(246, 248, 251);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 8 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 123));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        Controls.Add(root);
        root.Controls.Add(new Label { Text = "PowerSI 실행 파일", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 77));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        executable.Text = string.IsNullOrWhiteSpace(current.PowerSiPath)
            ? PowerSiDiscovery.FindInstalled().FirstOrDefault() ?? ""
            : current.PowerSiPath;
        pathRow.Controls.Add(executable);
        var browse = new Button { Text = "찾아보기", Dock = DockStyle.Fill, Margin = new Padding(6, 0, 0, 7) };
        browse.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "PowerSI 실행 파일 (*.exe)|*.exe", Title = "PowerSI 실행 파일 선택", CheckFileExists = true };
            if (picker.ShowDialog(this) == DialogResult.OK) executable.Text = picker.FileName;
        };
        pathRow.Controls.Add(browse);
        var discover = new Button { Text = "자동 찾기", Dock = DockStyle.Fill, Margin = new Padding(6, 0, 0, 7) };
        discover.Click += (_, _) =>
        {
            try
            {
                var found = PowerSiDiscovery.FindInstalled();
                if (found.Count == 0) MessageBox.Show(this, "설치 경로에서 PowerSI를 찾지 못했습니다. 찾아보기로 지정하세요.", Text);
                else executable.Text = found[0];
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
        };
        pathRow.Controls.Add(discover);
        root.Controls.Add(pathRow, 0, 1);

        var thresholds = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3, Margin = new Padding(0, 8, 0, 6) };
        thresholds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        thresholds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 19));
        thresholds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        thresholds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 19));
        idleSeconds = AddNumber(thresholds, "유휴 유지 시간 (초)", current.IdleSeconds, 10, 86400, 0, 0);
        cpu = AddNumber(thresholds, "CPU 한도 (1코어 %)", (decimal)current.IdleCpuPercent, 0.01M, 100, 2, 0, 2);
        memory = AddNumber(thresholds, "메모리 변화 한도 (MiB)", current.IdleMemoryMiB, 0, 1048576, 0, 1);
        io = AddNumber(thresholds, "읽기/쓰기 한도 (KiB/s)", current.IdleIoKiBPerSecond, 0, 1048576, 2, 1);
        timeout = AddNumber(thresholds, "시작 제한 시간 (분)", current.StartTimeoutMinutes, 1, 1440, 0, 2);
        polling = AddNumber(thresholds, "확인 간격 (분)", current.PollIntervalMinutes, 1, 60, 2, 2);
        root.Controls.Add(thresholds, 0, 2);
        var bbsOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        allowBbs = new CheckBox { Text = "외부 BbsCon 후처리 중 다음 시뮬레이션 허용", Checked = current.AllowBbsPostProcessing,
            AutoSize = true, AccessibleName = "외부 BbsCon 후처리 동시 진행 허용" };
        bbsOptions.Controls.Add(allowBbs);
        bbsOptions.Controls.Add(new Label { Text = "BbsCon은 그대로 유지합니다. 자체 실행 작업은 저장·정리가 끝나야 다음 작업을 시작합니다.", AutoSize = true, ForeColor = Color.DimGray });
        root.Controls.Add(bbsOptions, 0, 3);
        root.Controls.Add(new Label
        {
            Text = "고급 · 작업별 판정 규칙 (정규식)\nSPD 패턴은 파일의 각 줄에서 평가합니다. 모든 패턴은 기본적으로 대소문자를 구분합니다.\n시작·완료 패턴을 실제 로그에 맞추세요. 대소문자를 무시하려면 (?i)를 붙이세요.",
            Dock = DockStyle.Fill, ForeColor = Color.FromArgb(65, 79, 98), Margin = new Padding(0, 5, 0, 0)
        }, 0, 4);
        rules = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersWidth = 24,
            AllowUserToResizeRows = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
            AccessibleName = "작업별 PowerSI 완료 판정 규칙", ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText
        };
        rules.Columns.Add(new DataGridViewTextBoxColumn { Name = "Workflow", HeaderText = "작업 이름", FillWeight = 22 });
        rules.Columns.Add(new DataGridViewTextBoxColumn { Name = "SPD", HeaderText = "SPD 내용 패턴", FillWeight = 26 });
        rules.Columns.Add(new DataGridViewTextBoxColumn { Name = "Started", HeaderText = "시작 패턴 (필수)", FillWeight = 26 });
        rules.Columns.Add(new DataGridViewTextBoxColumn { Name = "Completed", HeaderText = "완료 표시 패턴", FillWeight = 26 });
        foreach (CompletionRule rule in current.CompletionRules) rules.Rows.Add(rule.Workflow, rule.SpdPattern, rule.StartedPattern, rule.CompletedPattern);
        root.Controls.Add(rules, 0, 5);
        var location = new TextBox { Text = "저장 폴더: " + dataDirectory, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = BackColor, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0), AccessibleName = "설정 및 대기열 저장 폴더" };
        root.Controls.Add(location, 0, 6);
        var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Size = new Size(80, 30) };
        var save = new Button { Text = "저장", Size = new Size(80, 30) };
        var deleteRule = new Button { Text = "선택 규칙 삭제", AutoSize = true, Height = 30 };
        deleteRule.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in rules.SelectedCells.Cast<DataGridViewCell>().Select(c => c.OwningRow).OfType<DataGridViewRow>().Distinct().Where(r => !r.IsNewRow).ToArray()) rules.Rows.Remove(row);
        };
        save.Click += (_, _) => SaveSettings();
        actions.Controls.AddRange([cancel, save, deleteRule]);
        root.Controls.Add(actions, 0, 7);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static NumericUpDown AddNumber(TableLayoutPanel panel, string label, decimal value, decimal minimum, decimal maximum, int column, int row, int decimals = 0)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(column == 0 ? 0 : 14, 2, 3, 2) }, column, row);
        var input = new NumericUpDown { Minimum = minimum, Maximum = maximum, DecimalPlaces = decimals, Value = Math.Clamp(value, minimum, maximum), Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), AccessibleName = label };
        panel.Controls.Add(input, column + 1, row);
        return input;
    }

    private void SaveSettings()
    {
        try
        {
            rules.EndEdit();
            string path = executable.Text.Trim();
            if (path.Length != 0 && (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("실제로 존재하는 PowerSI .exe 파일의 전체 경로를 지정하세요.");
            var completedRules = new List<CompletionRule>();
            foreach (DataGridViewRow row in rules.Rows)
            {
                if (row.IsNewRow) continue;
                string[] values = row.Cells.Cast<DataGridViewCell>().Select(c => c.Value?.ToString()?.Trim() ?? "").ToArray();
                if (values.All(string.IsNullOrEmpty)) continue;
                if (values.Any(string.IsNullOrEmpty)) throw new ArgumentException($"규칙 {row.Index + 1}행의 작업 이름, SPD 패턴, 시작 패턴, 완료 패턴을 모두 입력하세요.");
                foreach (string pattern in values.Skip(1)) _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                completedRules.Add(new CompletionRule { Workflow = values[0], SpdPattern = values[1], StartedPattern = values[2], CompletedPattern = values[3] });
            }
            Result = new AppSettings
            {
                AllowBbsPostProcessing = allowBbs.Checked,
                PowerSiPath = path, PollIntervalMinutes = (int)polling.Value,
                IdleSeconds = (int)idleSeconds.Value, IdleCpuPercent = (double)cpu.Value,
                IdleMemoryMiB = (int)memory.Value, IdleIoKiBPerSecond = (int)io.Value,
                StartTimeoutMinutes = (int)timeout.Value, CompletionRules = completedRules
            };
            SettingsValidator.Validate(Result);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "설정 확인", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
}
