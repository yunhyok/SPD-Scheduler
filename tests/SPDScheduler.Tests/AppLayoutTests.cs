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
    }

    private static Task MainWindowLayoutAsync()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new Thread(() =>
        {
            try { CheckMainWindowLayout(); result.SetResult(); }
            catch (Exception exception) { result.SetException(exception); }
        }) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        return result.Task;
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
            Check.Equal(10, buttons.Length, "Main window should retain all action buttons");
            Check.Equal(4, grid.Columns.Count, "Main window should retain all queue columns");
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
                if (size == wide) Check.Equal(add.Top, monitoring.Top, "Toolbar should restore one row at a wide size");
                if (size == form.MinimumSize)
                    Check.That(monitoring.Top > add.Top, "Toolbar should wrap at the minimum width");
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(control => Descendants(control).Prepend(control));
}
