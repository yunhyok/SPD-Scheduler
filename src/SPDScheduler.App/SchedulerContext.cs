using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.App;

internal sealed class SchedulerContext : ApplicationContext
{
    private readonly SchedulerEngine engine;
    private readonly MainForm window;
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer ticker = new() { Interval = 2000 };
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim engineGate = new(1, 1);
    private readonly RegisteredWaitHandle showRegistration;
    private bool exiting;
    private bool tickPending;
    private bool initialized;

    public SchedulerContext(QueueStore store, bool safeMode, bool show, EventWaitHandle signal)
    {
        engine = new SchedulerEngine(store, safeMode ? new SafeModeRunner() : new PowerSiRunner(),
            safeMode ? new SafeModeProbe() : new WindowsProcessProbe());
        window = new MainForm(engine, safeMode, RunCommandAsync, store.DataDirectory);
        _ = window.Handle;
        var menu = new ContextMenuStrip();
        menu.Items.Add("창 열기", null, (_, _) => window.Reveal());
        menu.Items.Add("대기열 시작", null, async (_, _) => await RunCommandAsync(engine.Start));
        menu.Items.Add("일시 중지", null, async (_, _) => await RunCommandAsync(engine.Pause));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, async (_, _) => await ExitAsync());
        tray = new NotifyIcon { Icon = window.Icon, Text = "SPD Scheduler", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => window.Reveal();
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
            ticker.Start();
            if (show) window.Reveal();
        }));
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
                    window.RefreshQueue();
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
        if (tickPending || exiting) return;
        tickPending = true;
        try { await RunCommandAsync(() => Task.Run(() => engine.TickAsync(stopping.Token), stopping.Token)); }
        finally { tickPending = false; }
    }

    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        ticker.Stop();
        stopping.Cancel();
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
