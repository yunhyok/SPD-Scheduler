using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.Tests;

internal static class ReliabilityAndUxTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return ("Queued jobs can be reordered safely and preserve FIFO execution order", ReorderQueuedJobs);
        yield return ("Queue reorder blocks during launch preflight and rolls back on save failure", ReorderGuardsAndRollback);
        yield return ("Retry rejects duplicating an active or queued SPD and tool", RetryDuplicateGuard);
        yield return ("Queue mutations roll back in-memory state when persistence fails", MutationRollbacks);
        yield return ("SPD rule matching succeeds while another process holds a shared read-write handle", SharedReadWriteSpdRule);
        yield return ("InspectAsync handles a missing run directory without crashing", MissingRunDirectorySafety);
        yield return ("Generated Tcl protects control.txt reads so transient locks cannot stop polling", TclControlReadGuard);
    }

    private static async Task ReorderQueuedJobs()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var first = rig.Add("first.spd");
        var completed = rig.Add("completed.spd");
        completed.Status = JobStatus.Completed;
        var second = rig.Add("second.spd");
        var third = rig.Add("third.spd");

        Check.That(!rig.Engine.MoveQueued(first.Id, -1), "First queued job cannot move higher");
        Check.That(!rig.Engine.MoveQueued(third.Id, 1), "Last queued job cannot move lower");
        Check.That(rig.Engine.MoveQueued(third.Id, -1), "Move third above second");
        Check.That(rig.Engine.MoveQueued(third.Id, -1), "Move third above first, skipping completed row");
        Check.Equal(third.Id, rig.Engine.Jobs[0].Id, "Moved job reaches top of queue");
        Check.Equal(first.Id, rig.Engine.Jobs[2].Id, "Previous first job shifts past non-queued row");
        Check.Equal(second.Id, rig.Engine.Jobs[3].Id, "Second job stays after first");

        var reloaded = rig.Store.Load().Jobs.Where(j => j.Status == JobStatus.Queued).Select(j => j.Id).ToArray();
        Check.That(reloaded.SequenceEqual([third.Id, first.Id, second.Id]), "Reordered queue persists across reload");

        rig.Engine.Start();
        await rig.TickAsync();
        Check.Equal(third.Id, rig.Runner.Launched.Single(), "Execution follows the updated FIFO order");

        Check.Throws<InvalidOperationException>(() => rig.Engine.MoveQueued(completed.Id, -1), "Non-queued jobs cannot be reordered");
    }

    private static async Task ReorderGuardsAndRollback()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var first = rig.Add("first.spd");
        var second = rig.Add("second.spd");
        long firstOrder = first.Order;
        long secondOrder = second.Order;

        using (File.Open(rig.Store.QueuePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Check.Throws<IOException>(() => rig.Engine.MoveQueued(second.Id, -1), "Report failed reorder persistence");
        Check.That(rig.Engine.Jobs.Select(j => j.Id).SequenceEqual([first.Id, second.Id]), "Rollback list order on save failure");
        Check.Equal(firstOrder, first.Order, "Rollback first order on save failure");
        Check.Equal(secondOrder, second.Order, "Rollback second order on save failure");

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Probe.BeforeCaptureAsync = capture =>
        {
            if (capture != 2) return Task.CompletedTask;
            reached.SetResult();
            return release.Task;
        };
        rig.Engine.Start();
        var tick = rig.Engine.TickAsync();
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Throws<InvalidOperationException>(() => rig.Engine.MoveQueued(second.Id, -1), "Reject reorder while launch preflight owns the queue");
        }
        finally { release.TrySetResult(); await tick; }
    }

    private static async Task RetryDuplicateGuard()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var failed = rig.Add("dup.spd");
        failed.Status = JobStatus.Failed;
        failed.LastError = "Synthetic failure";
        rig.Engine.Save();
        Check.Equal(0, rig.Engine.AddFiles([failed.FilePath]).Count, "Re-adding a failed SPD should succeed");
        var queued = rig.Engine.Jobs.Single(j => j.Status == JobStatus.Queued);
        Check.Throws<InvalidOperationException>(() => rig.Engine.Retry(failed.Id), "Retry must reject duplicating an already queued SPD and tool");
        Check.Equal(JobStatus.Failed, failed.Status, "Rejected retry keeps failed job unchanged");
        rig.Engine.SetTool(queued.Id, SimulationTool.PowerDC);
        rig.Engine.Retry(failed.Id);
        Check.Equal(JobStatus.Queued, failed.Status, "Retry succeeds when the other queued entry targets a different tool");
    }

    private static async Task MutationRollbacks()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var queued = rig.Add("queued.spd");
        var failed = rig.Add("failed.spd");
        failed.Status = JobStatus.Failed;
        failed.LastError = "Previous error";
        failed.FinishedAt = rig.Clock.Now;
        rig.Engine.Save();

        using (File.Open(rig.Store.QueuePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Check.Throws<IOException>(() => rig.Engine.SetSchedule(queued.Id, rig.Clock.Now.AddHours(1)), "Schedule save failure throws");
            Check.That(queued.ScheduledAt is null && queued.ScheduleTimeZone is null, "Schedule rolls back");

            Check.Throws<IOException>(() => rig.Engine.Remove(queued.Id), "Remove save failure throws");
            Check.That(rig.Engine.Jobs.Contains(queued), "Remove rolls back");

            Check.Throws<IOException>(() => rig.Engine.Retry(failed.Id), "Retry save failure throws");
            Check.That(failed.Status == JobStatus.Failed && failed.LastError == "Previous error" && failed.FinishedAt == rig.Clock.Now, "Retry rolls back");

            Check.Throws<IOException>(() => rig.Engine.Skip(failed.Id), "Skip save failure throws");
            Check.Equal("Previous error", failed.LastError, "Skip rolls back");

            var updated = new AppSettings { IdleSeconds = 120 };
            Check.Throws<IOException>(() => rig.Engine.UpdateSettings(updated), "UpdateSettings save failure throws");
            Check.Equal(60, rig.Engine.Settings.IdleSeconds, "UpdateSettings rolls back");
        }
    }

    private static async Task SharedReadWriteSpdRule()
    {
        using var rig = new TestRig();
        string spd = rig.CreateFile("locked.spd");
        File.WriteAllText(spd, "Header\nWorkflow=ExtractionMode\nFooter\n");
        var rule = new CompletionRule { Tool = SimulationTool.PowerSI, Workflow = "Extraction", SpdPattern = "Workflow=ExtractionMode", StartedPattern = "START", CompletedPattern = "DONE" };
        using var simulatedPowerSiHandle = new FileStream(spd, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var findRule = typeof(PowerSiRunner).GetMethod("FindRuleAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var matched = await (Task<CompletionRule?>)findRule.Invoke(null, [spd, (IEnumerable<CompletionRule>)[rule], CancellationToken.None])!;
        Check.That(matched is not null && matched.Workflow == "Extraction", "Rule lookup must succeed even while PowerSI/PowerDC holds a read-write handle");
    }

    private static async Task MissingRunDirectorySafety()
    {
        using var rig = new TestRig();
        string missingDir = Path.Combine(rig.DirectoryPath, "missing-run-dir");
        var job = new JobItem
        {
            FilePath = rig.CreateFile("missing-dir.spd"),
            Status = JobStatus.Running,
            Run = new RunRecord
            {
                DirectoryPath = missingDir,
                CreatedAt = rig.Clock.Now,
                Process = new ProcessIdentity(int.MaxValue, rig.Clock.Now, @"C:\SyntheticOnly\PowerSI.exe")
            }
        };
        var runner = (PowerSiRunner)Activator.CreateInstance(typeof(PowerSiRunner),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [(Func<ProcessIdentity, (bool, bool, string?)>)(_ => (true, true, null)),
                (Func<ProcessIdentity, CancellationToken, Task<string?>>)((_, _) => Task.FromResult<string?>(null))], null)!;
        var observation = await runner.InspectAsync(job, new AppSettings(), new ProcessSnapshot(rig.Clock.Now, []), CancellationToken.None);
        Check.Equal(RunStage.Starting, observation.Stage, "Missing run directory must not crash inspection with DirectoryNotFoundException");
    }

    private static Task TclControlReadGuard()
    {
        string script = PowerSiRunner.CreateScript(Guid.NewGuid(), "synthetic.spd", "synthetic-run");
        int poll = script.IndexOf("proc scheduler_poll", StringComparison.Ordinal);
        int readCatch = script.IndexOf("if {[catch {", poll, StringComparison.Ordinal);
        int closeGuard = script.IndexOf("if {$f ne \"\"} {catch {close $f}}", poll, StringComparison.Ordinal);
        int reschedule = script.IndexOf("after 500 scheduler_poll", poll, StringComparison.Ordinal);
        Check.That(poll >= 0 && readCatch > poll && closeGuard > readCatch && reschedule > closeGuard,
            "scheduler_poll must catch transient control.txt open/read locks and continue polling");
        return Task.CompletedTask;
    }
}
