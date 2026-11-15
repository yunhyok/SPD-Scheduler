using System.Drawing;
using System.Windows.Forms;
using SPDScheduler.App;
using SPDScheduler.Core;

namespace SPDScheduler.Tests;

internal static class AppLayoutTests
{
    public static IEnumerable<(string, Func<Task>)> All()
    {
        yield return ("Long monitoring errors keep controls visible at simulated layout scales", MainWindowLayoutAsync);
        yield return ("Thread context menu persists per-job choices and protects active jobs", () => OnSta(CheckThreadMenu));
    }

    private static Task MainWindowLayoutAsync() => OnSta(CheckMainWindowLayout);

    private static Task OnSta(Action check)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new Thread(() =>
        {
            try { check(); result.SetResult(); }
            catch (Exception exception) { result.SetException(exception); }
        }) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        return result.Task;
    }

    private static void CheckThreadMenu()
    {
        using var rig = new TestRig();
        rig.InitializeAsync().GetAwaiter().GetResult();
        var first = rig.Add("large.spd");
        var second = rig.Add("small.spd");
        using var form = new MainForm(rig.Engine, false, action => { action(); return Task.CompletedTask; },
            () => Task.CompletedTask, rig.DirectoryPath);
        var grid = Descendants(form).OfType<DataGridView>().Single();
        var menu = grid.ContextMenuStrip!;
        var resources = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "리소스 사용 (스레드)");
        var choices = resources.DropDownItems.Cast<ToolStripMenuItem>().ToArray();
        void OpenMenu() => typeof(ToolStripDropDown).GetMethod("OnOpening",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(menu, [new System.ComponentModel.CancelEventArgs()]);
        Check.That(choices.Select(item => (int)item.Tag!).SequenceEqual([16, 24, 32, 48, 64]), "Offer all five requested thread counts");
        grid.ClearSelection();
        grid.Rows[0].Selected = true;
        OpenMenu();
        Check.That(resources.Enabled && choices.Single(item => item.Checked).Tag is 64, "New job defaults to 64 threads");
        choices.Single(item => item.Tag is 16).PerformClick();
        form.RefreshQueue();
        Check.Equal(16, first.ThreadCount, "Menu applies choice to the selected job");
        Check.Equal(64, second.ThreadCount, "Other job keeps its choice");
        Check.Equal(16, (int)grid.Rows[0].Cells["Threads"].Value!, "Column refreshes after the choice changes");
        Check.Equal(16, rig.Store.Load().Jobs.Single(job => job.Id == first.Id).ThreadCount, "Menu choice is persisted");
        grid.Rows[1].Selected = true;
        OpenMenu();
        Check.That(resources.Enabled && choices.All(item => !item.Checked), "Mixed choices have no misleading checkmark");
        choices.Single(item => item.Tag is 32).PerformClick();
        Check.That(rig.Engine.Jobs.All(job => job.ThreadCount == 32), "Multiple queued jobs can change together");
        OpenMenu();
        Check.That(choices.Single(item => item.Checked).Tag is 32, "Common choice is checked");
        form.RefreshQueue(checkPending: true);
        OpenMenu();
        Check.That(!resources.Enabled, "Do not queue resource edits behind an in-flight launch check");
        form.RefreshQueue();
        OpenMenu();
        Check.That(resources.Enabled, "Allow edits again after the check finishes");
        foreach (JobStatus status in Enum.GetValues<JobStatus>().Where(status => status != JobStatus.Queued))
        {
            first.Status = status;
            OpenMenu();
            Check.That(!resources.Enabled, "Mixed selection with a nonqueued job is protected: " + status);
        }
        grid.ClearSelection();
        OpenMenu();
        Check.That(!resources.Enabled && choices.All(item => !item.Checked), "Empty selection cannot change resources");
    }

    private static void CheckMainWindowLayout()
    {
        using var rig = new TestRig();
        rig.InitializeAsync().GetAwaiter().GetResult();
        for (int index = 0; index < 20; index++)
            rig.Engine.Document.Jobs.Add(new JobItem { FilePath = Path.Combine(rig.DirectoryPath, $"Layout fixture {index + 1}.spd") });
        string error = "프로세스에 접근할 수 없습니다. " + new string('X', 2_048);
        rig.Probe.BeforeCaptureAsync = _ => Task.FromException(new UnauthorizedAccessException(error));
        rig.TickAsync().GetAwaiter().GetResult();

        foreach (float scale in new[] { 1F, 1.5F, 2F })
        {
            using var form = new MainForm(rig.Engine, false, _ => Task.CompletedTask, () => Task.CompletedTask, rig.DirectoryPath);
            form.Scale(new SizeF(scale, scale));
            form.RefreshQueue(rig.Clock.Now.AddMinutes(1));
            var controls = Descendants(form).ToArray();
            var status = controls.OfType<Label>().Single(label => label.Text.Contains(error));
            var grid = controls.OfType<DataGridView>().Single();
            _ = form.Handle;
            foreach (Control control in controls) _ = control.Handle;
            var buttons = controls.OfType<Button>().ToArray();
            var add = buttons.Single(button => button.Text == "파일 추가");
            var monitoring = buttons.Single(button => button.Text == "감시 상세");
            var toolbar = (FlowLayoutPanel)add.Parent!;
            Check.Equal(10, buttons.Length, "Main window should retain all action buttons");
            Check.Equal(5, grid.Columns.Count, "Main window should retain all queue columns including per-job threads");
            Check.Equal("연동 안내", controls.OfType<LinkLabel>().Single().Text, "Main window should retain its help link");
            Check.That(status.AutoEllipsis && !status.AutoSize, "Long status must fit in its own cell");
            Size initial = form.Size;
            Size wide = new(initial.Width + (int)(200 * scale), initial.Height);
            foreach (Size size in new[] { initial, form.MinimumSize, wide, form.MinimumSize })
            {
                form.Size = size;
                form.PerformLayout();
                Rectangle client = form.ClientRectangle;
                foreach (Control control in buttons.Cast<Control>().Concat(controls.OfType<Label>()).Append(grid))
                {
                    Rectangle bounds = control.Bounds;
                    for (Control parent = control.Parent!; parent != form; parent = parent.Parent!)
                        bounds.Offset(parent.Location);
                    Check.That(bounds.Width > 0 && bounds.Height > 0 && client.Contains(bounds) &&
                        control.Parent!.ClientRectangle.Contains(control.Bounds),
                        $"{scale:P0} scale at {size}: {control.GetType().Name} ({control.Text[..Math.Min(control.Text.Length, 32)]}) bounds {bounds} outside {client}, parent bounds {control.Parent!.Bounds}, child bounds {control.Bounds}");
                }
                int columnsWidth = grid.Columns.Cast<DataGridViewColumn>().Sum(column => column.Width);
                Check.That(columnsWidth <= grid.ClientSize.Width,
                    $"{scale:P0} scale at {size}: grid columns need {columnsWidth}px, grid is {grid.ClientSize.Width}px wide");
                Check.That(grid.Height >= grid.ColumnHeadersHeight + 3 * grid.RowTemplate.Height,
                    $"{scale:P0} scale at {size}: queue is {grid.Height}px tall with {grid.ColumnHeadersHeight}px headers and {grid.RowTemplate.Height}px rows");
                // Windows can limit the requested form width to the desktop's maximum window size.
                int availableWidth = toolbar.ClientSize.Width - toolbar.Padding.Horizontal;
                int requiredWidth = toolbar.Controls.Cast<Control>().Sum(button => button.Width + button.Margin.Horizontal);
                Check.Equal(requiredWidth <= availableWidth, add.Top == monitoring.Top,
                    $"{scale:P0} scale at {form.Size}: toolbar needs {requiredWidth}px for one row and has {availableWidth}px");
                if (size == form.MinimumSize)
                    Check.That(monitoring.Top > add.Top, "Toolbar should wrap at the minimum width");
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(control => Descendants(control).Prepend(control));
}
