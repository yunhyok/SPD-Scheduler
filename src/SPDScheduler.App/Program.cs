using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (WindowsTextWorker.TryRun(args) is int result) return result;
        ApplicationConfiguration.Initialize();
        bool safeMode = args.Contains("--safe-mode", StringComparer.OrdinalIgnoreCase);
        bool show = args.Contains("--show", StringComparer.OrdinalIgnoreCase);
        string? directory = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (!safeMode || ++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                {
                    MessageBox.Show("--data-dir는 --safe-mode와 함께 경로를 지정해야 합니다.", "SPD Scheduler");
                    return 2;
                }
                try { directory = Path.GetFullPath(args[i]); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    MessageBox.Show($"저장 경로가 올바르지 않습니다.\n{ex.Message}", "SPD Scheduler");
                    return 2;
                }
            }
            else if (!args[i].Equals("--safe-mode", StringComparison.OrdinalIgnoreCase)
                && !args[i].Equals("--show", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show($"알 수 없는 옵션: {args[i]}", "SPD Scheduler");
                return 2;
            }
        }
        if (safeMode)
            directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SPDScheduler", "SafeMode");

        string suffix = safeMode ? "_SafeMode" : "";
        using var mutex = new Mutex(true, @"Local\SPDScheduler_SingleInstance" + suffix, out bool firstInstance);
        if (!firstInstance)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using var existing = EventWaitHandle.OpenExisting(@"Local\SPDScheduler_ShowWindow" + suffix);
                    existing.Set();
                    return 0;
                }
                catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(100); }
            }
            MessageBox.Show("이미 실행 중입니다. 작업 표시줄의 숨겨진 아이콘에서 SPD Scheduler를 여세요.", "SPD Scheduler");
            return 0;
        }
        try
        {
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SPDScheduler_ShowWindow" + suffix);
            using var context = new SchedulerContext(new QueueStore(directory), safeMode, show, signal);
            Application.Run(context);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"시작하지 못했습니다.\n{ex.Message}", "SPD Scheduler", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally { mutex.ReleaseMutex(); }
    }
}

internal sealed class SafeModeRunner : IPowerSiRunner
{
    public Task<ProcessIdentity> LaunchAsync(JobItem job, AppSettings settings, CancellationToken cancellationToken) =>
        Task.FromException<ProcessIdentity>(new InvalidOperationException("안전 모드에서는 PowerSI를 실행하지 않습니다."));
    public Task<RunObservation> InspectAsync(JobItem job, AppSettings settings, ProcessSnapshot snapshot, CancellationToken cancellationToken) =>
        Task.FromResult(new RunObservation(RunStage.Unknown, "안전 모드: 실행 상태를 확인하지 않습니다.", false));
    public Task RequestSaveAndExitAsync(JobItem job, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<CleanupResult> CloseFailedRunAsync(JobItem job, ProcessSnapshot snapshot, CancellationToken cancellationToken) =>
        Task.FromResult(new CleanupResult(CleanupState.Unsafe, "안전 모드에서는 프로세스를 제어하지 않습니다."));
}

internal sealed class SafeModeProbe : IProcessProbe
{
    public Task<ProcessSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProcessSnapshot(DateTimeOffset.Now, [], false, "안전 모드: PowerSI 실행 차단"));
}
