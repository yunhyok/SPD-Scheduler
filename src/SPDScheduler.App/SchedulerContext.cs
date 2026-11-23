using Microsoft.Win32;
using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.App;

internal sealed class SchedulerContext : ApplicationContext
{
    private readonly SchedulerEngine engine;
    private readonly MainForm window;
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer ticker = new() { Interval = 60_000 };
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim engineGate = new(1, 1);
    private readonly RegisteredWaitHandle showRegistration;
    private readonly Dictionary<Guid, JobStatus> lastJobStatuses = [];
    private bool hadPendingWork;
    private bool statusBaselineReady;
    private bool exiting;
    private bool tickPending;
    private bool initialized;
    private DateTimeOffset? nextCheckAt;

    public SchedulerContext(QueueStore store, bool safeMode, bool show, EventWaitHandle signal)
    {
        engine = new SchedulerEngine(store, safeMode ? new SafeModeRunner() : new PowerSiRunner(),
            safeMode ? new SafeModeProbe() : new WindowsProcessProbe());
        window = new MainForm(engine, safeMode, RunCommandAsync, TickAsync, store.DataDirectory);
        _ = window.Handle;
        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("창 열기", null, (_, _) => window.Reveal());
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);
        var summaryItem = new ToolStripMenuItem("준비 중…") { Enabled = false };
        var startItem = new ToolStripMenuItem("대기열 시작", null, async (_, _) => { await RunCommandAsync(engine.Start); if (engine.IsArmed) await TickAsync(); });
        var pauseItem = new ToolStripMenuItem("일시 중지", null, async (_, _) => await RunCommandAsync(engine.Pause));
        var checkNowItem = new ToolStripMenuItem("지금 확인", null, async (_, _) => await TickAsync());
        menu.Items.Add(openItem);
        menu.Items.Add(summaryItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startItem);
        menu.Items.Add(pauseItem);
        menu.Items.Add(checkNowItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, async (_, _) => await ExitAsync());
        menu.Opening += (_, _) =>
        {
            int queued = engine.Jobs.Count(j => j.Status == JobStatus.Queued);
            int active = engine.Jobs.Count(j => j.Status is JobStatus.Starting or JobStatus.Running or JobStatus.Saving);
            int attention = engine.Jobs.Count(j => j.Status == JobStatus.NeedsAttention);
            string state = attention > 0 ? $"확인 필요 {attention}건" : active > 0 ? "실행 중" : engine.IsArmed ? "자동 진행 중" : "일시 중지";
            summaryItem.Text = $"{state} · 대기 {queued}건";
            startItem.Enabled = initialized && !exiting && !engine.IsArmed;
            pauseItem.Enabled = initialized && !exiting && engine.IsArmed;
            checkNowItem.Enabled = initialized && !exiting && !tickPending;
        };
        tray = new NotifyIcon { Icon = window.Icon, Text = "SPD Scheduler", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => window.Reveal();
        tray.BalloonTipClicked += (_, _) => window.Reveal();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        showRegistration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
        {
            if (!exiting && !window.IsDisposed)
            {
                try { window.BeginInvoke((Action)window.Reveal); }
                catch (InvalidOperationException) { }
            }
        }, null, Timeout.Infinite, false);
        ticker.Tick += async (_, _) => await TickAsync();
        window.BeginInvoke((Action)(async () =>
        {
            initialized = await RunCommandAsync(() => Task.Run(() => engine.InitializeAsync(stopping.Token), stopping.Token));
            if (!initialized) { await ExitAsync(); return; }
            if (exiting) return;
            CaptureStatusBaseline();
            ticker.Start();
            nextCheckAt = DateTimeOffset.Now.AddMilliseconds(ticker.Interval);
            window.RefreshQueue(nextCheckAt);
            if (show) window.Reveal();
        }));
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || exiting || !initialized || window.IsDisposed) return;
        try { window.BeginInvoke((Action)(async () => await TickAsync())); }
        catch (InvalidOperationException) { }
    }

    private void CaptureStatusBaseline()
    {
        lastJobStatuses.Clear();
        foreach (var job in engine.Jobs) lastJobStatuses[job.Id] = job.Status;
        hadPendingWork = engine.Jobs.Any(j => j.Status is JobStatus.Queued or JobStatus.Starting or JobStatus.Running or JobStatus.Saving);
        statusBaselineReady = true;
    }

    private void NotifyTransitions()
    {
        if (!statusBaselineReady || exiting || !initialized) return;
        JobItem? newAttention = null;
        JobItem? newFailed = null;
        bool newCompleted = false;
        foreach (var job in engine.Jobs)
        {
            if (lastJobStatuses.TryGetValue(job.Id, out var prior) && prior == job.Status) continue;
            if (job.Status == JobStatus.NeedsAttention && newAttention is null) newAttention = job;
            else if (job.Status == JobStatus.Failed && newFailed is null) newFailed = job;
            else if (job.Status == JobStatus.Completed) newCompleted = true;
        }
        bool hasPendingNow = engine.Jobs.Any(j => j.Status is JobStatus.Queued or JobStatus.Starting or JobStatus.Running or JobStatus.Saving);
        bool allFinished = hadPendingWork && !hasPendingNow && newCompleted && newAttention is null && newFailed is null;
        CaptureStatusBaseline();
        if (window.Visible) return;
        if (newAttention is not null)
            tray.ShowBalloonTip(6000, "SPD Scheduler · 확인 필요", $"{newAttention.FileName} 작업의 상태를 확인하세요.", ToolTipIcon.Warning);
        else if (newFailed is not null)
            tray.ShowBalloonTip(5000, "SPD Scheduler · 작업 실패", $"{newFailed.FileName} 작업이 실패했습니다.", ToolTipIcon.Error);
        else if (allFinished)
            tray.ShowBalloonTip(5000, "SPD Scheduler · 대기열 완료", "예정된 모든 시뮬레이션 작업이 완료되었습니다.", ToolTipIcon.Info);
    }

    private Task RunCommandAsync(Action command) => RunCommandAsync(() => { command(); return Task.CompletedTask; });

    private async Task<bool> RunCommandAsync(Func<Task> command)
    {
        if (exiting) return false;
        await engineGate.WaitAsync();
        bool succeeded = false;
        string? error = null;
        try
        {
            if (!exiting) { await command(); succeeded = true; }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            try
            {
                if (!window.IsDisposed)
                {
                    int interval = engine.Settings.PollIntervalMinutes * 60_000;
                    if (ticker.Interval != interval)
                    {
                        ticker.Interval = interval;
                        if (ticker.Enabled)
                        {
                            ticker.Stop();
                            ticker.Start();
                            nextCheckAt = DateTimeOffset.Now.AddMilliseconds(interval);
                        }
                    }
                    window.RefreshQueue(nextCheckAt, tickPending);
                    NotifyTransitions();
                    string status = engine.StatusText;
                    string text = "SPD Scheduler · " + status;
                    tray.Text = text[..Math.Min(63, text.Length)];
                }
            }
            finally { engineGate.Release(); }
        }
        if (error is not null && !exiting) window.ShowError(error);
        return succeeded;
    }

    private async Task TickAsync()
    {
        if (tickPending || exiting || !initialized) return;
        tickPending = true;
        ticker.Stop();
        nextCheckAt = null;
        window.RefreshQueue(nextCheckAt, tickPending);
        try { await RunCommandAsync(() => Task.Run(() => engine.TickAsync(stopping.Token), stopping.Token)); }
        finally
        {
            tickPending = false;
            if (!exiting)
            {
                ticker.Start();
                nextCheckAt = DateTimeOffset.Now.AddMilliseconds(ticker.Interval);
                window.RefreshQueue(nextCheckAt);
            }
            System.Diagnostics.Debug.Assert(exiting || (ticker.Enabled && nextCheckAt.HasValue));
        }
    }

    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        ticker.Stop();
        stopping.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        showRegistration.Unregister(null);
        window.SetExiting();
        await engineGate.WaitAsync();
        try { if (initialized) engine.Save(); }
        catch (Exception ex) { window.ShowError($"마지막 상태를 저장하지 못했습니다.\n{ex.Message}"); }
        finally { engineGate.Release(); }
        tray.Visible = false;
        window.CloseForExit();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            showRegistration.Unregister(null);
            ticker.Dispose();
            tray.Dispose();
            window.Dispose();
            stopping.Dispose();
            engineGate.Dispose();
        }
        base.Dispose(disposing);
    }
}
