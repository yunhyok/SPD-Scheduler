using SPDScheduler.Core;

namespace SPDScheduler.Tests;

internal static class ManualResolutionTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return ("Manual completion preserves the run and records human confirmation without PowerSI commands", CompleteManually);
        yield return ("Manual removal preserves SPD and run logs even with a live owned identity", RemoveManually);
        yield return ("Manual reconciliation permits an unbound run but keeps automatic retry protected", UnboundRun);
        yield return ("Manual reconciliation rejects active states and stale run authorization", GuardStateAndRun);
        yield return ("Manual reconciliation pauses an armed queue and requires explicit Start", PausesQueue);
        yield return ("Manual completion does not bypass external active simulation checks", ExternalActivity);
        yield return ("A queue save failure rolls back manual completion and removal", PersistenceFailure);
        yield return ("Manual reconciliation cannot race an in-flight monitor", InFlightMonitor);
    }

    private static async Task<JobItem> Held(TestRig rig, bool bound = true, JobStatus status = JobStatus.NeedsAttention)
    {
        var run = new RunRecord { CreatedAt = rig.Clock.Now,
            Process = bound ? new ProcessIdentity(12345, rig.Clock.Now, @"C:\SyntheticOnly\PowerSI.exe") : null };
        var job = new JobItem { FilePath = rig.CreateFile("held.spd"), Status = status,
            LastError = "Synthetic observation error", Run = run };
        await rig.InitializeAsync(new QueueDocument { Jobs = [job] });
        job = rig.Engine.Jobs.Single();
        Directory.CreateDirectory(job.Run!.DirectoryPath);
        File.WriteAllText(Path.Combine(job.Run.DirectoryPath, "events.log"), "Synthetic history");
        if (job.Run.Process is { } identity) rig.Probe.Processes.Add(new ProcessSample(identity, 0, 0, 0, 0));
        return job;
    }

    private static void NoCommands(TestRig rig)
    {
        Check.Equal(0, rig.Runner.Launched.Count, "Manual reconciliation must not launch PowerSI");
        Check.Equal(0, rig.Runner.SaveRequests.Count, "Manual reconciliation must not save PowerSI");
        Check.Equal(0, rig.Runner.CleanupIdentities.Count, "Manual reconciliation must not close PowerSI");
    }

    private static async Task CompleteManually()
    {
        using var rig = new TestRig();
        var job = await Held(rig);
        var run = job.Run!;
        rig.Probe.Complete = false;
        rig.Engine.ResolveManually(job.Id, run.Id, remove: false);
        Check.Equal(JobStatus.Completed, job.Status, "Human confirmation can resolve an unreadable run");
        Check.That(job.CompletedManually && job.FinishedAt == rig.Clock.Now, "Distinguish manual completion and its timestamp");
        Check.That(ReferenceEquals(run, job.Run) && job.LastError == "Synthetic observation error", "Retain original run and failure evidence");
        var persisted = rig.Store.Load().Jobs.Single();
        Check.That(persisted.CompletedManually && persisted.Run!.Id == run.Id, "Manual completion must survive restart");
        string log = string.Join("\n", Directory.GetFiles(Path.Combine(rig.Store.DataDirectory, "logs")).Select(File.ReadAllText));
        Check.That(log.Contains("사용자 수동 정리") && log.Contains(run.Id.ToString()) && log.Contains("12345"), "Audit must identify the manual resolution and original run");
        await rig.TickAsync();
        Check.Equal(JobStatus.Completed, job.Status, "Later polling must not revive the detached run");
        NoCommands(rig);
        rig.Engine.Remove(job.Id);
        Check.Equal(0, rig.Engine.Jobs.Count, "Normal removal can clear a manually completed row");
    }

    private static async Task RemoveManually()
    {
        using var rig = new TestRig();
        var job = await Held(rig);
        Check.Throws<InvalidOperationException>(() => rig.Engine.Remove(job.Id), "Ordinary Remove must still require explicit manual acknowledgement");
        rig.Engine.ResolveManually(job.Id, job.Run!.Id, remove: true);
        Check.Equal(0, rig.Store.Load().Jobs.Count, "Persist removal of the selected row");
        Check.That(File.Exists(job.FilePath) && File.Exists(Path.Combine(job.Run.DirectoryPath, "events.log")), "Do not delete SPD or execution history");
        await rig.TickAsync();
        NoCommands(rig);
    }

    private static async Task UnboundRun()
    {
        using var rig = new TestRig();
        var job = await Held(rig, bound: false);
        Check.Throws<InvalidOperationException>(() => rig.Engine.Retry(job.Id), "Unknown launch cannot be silently replayed");
        rig.Engine.ResolveManually(job.Id, job.Run!.Id, remove: false);
        Check.That(job.CompletedManually, "Explicit human resolution must work when no PID was recorded");
        NoCommands(rig);
    }

    private static async Task GuardStateAndRun()
    {
        using var rig = new TestRig();
        var job = await Held(rig);
        Check.Throws<InvalidOperationException>(() => rig.Engine.ResolveManually(job.Id, Guid.NewGuid(), false), "A dialog for an older run cannot resolve a new run");
        foreach (var status in new[] { JobStatus.Queued, JobStatus.Starting, JobStatus.Running, JobStatus.Saving, JobStatus.Completed })
        {
            job.Status = status;
            Check.Throws<InvalidOperationException>(() => rig.Engine.ResolveManually(job.Id, job.Run!.Id, false), "Only held/failed jobs support manual completion");
            Check.Throws<InvalidOperationException>(() => rig.Engine.ResolveManually(job.Id, job.Run!.Id, true), "Only held/failed jobs support manual removal");
        }
        Check.That(!job.CompletedManually, "Rejected actions must not change completion metadata");
        NoCommands(rig);
    }

    private static async Task PausesQueue()
    {
        using var rig = new TestRig();
        var job = await Held(rig, status: JobStatus.Failed);
        var next = rig.Add("next.spd");
        rig.Engine.Start();
        rig.Engine.ResolveManually(job.Id, job.Run!.Id, false);
        Check.That(!rig.Engine.IsArmed, "Manual override must pause automatic launch");
        rig.Probe.Processes.Clear();
        await rig.TickAsync();
        NoCommands(rig);
        rig.Engine.Start();
        await rig.TickAsync();
        Check.Equal(next.Id, rig.Runner.Launched.Single(), "Explicit Start may continue with the next queued job");
    }

    private static async Task ExternalActivity()
    {
        using var rig = new TestRig();
        var job = await Held(rig);
        rig.Add("next.spd");
        rig.Engine.ResolveManually(job.Id, job.Run!.Id, false);
        rig.Probe.Processes[0] = rig.Probe.Processes[0] with { Signal = ProcessSignal.Running };
        rig.Engine.Start();
        await rig.TickAsync(3);
        NoCommands(rig);
    }

    private static async Task PersistenceFailure()
    {
        foreach (bool remove in new[] { false, true })
        {
            using var rig = new TestRig();
            var job = await Held(rig);
            string before = File.ReadAllText(rig.Store.QueuePath);
            using (var locked = new FileStream(rig.Store.QueuePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Check.Throws<IOException>(() => rig.Engine.ResolveManually(job.Id, job.Run!.Id, remove), "Failed save must report failure");
            Check.Equal(before, File.ReadAllText(rig.Store.QueuePath), "Preserve durable queue when reconciliation cannot be saved");
            Check.That(rig.Engine.Jobs.Contains(job) && job.Status == JobStatus.NeedsAttention && !job.CompletedManually && job.FinishedAt == null,
                "Roll back in-memory manual resolution when persistence fails");
            Check.That(!rig.Engine.IsArmed, "Persistence failure leaves the queue paused");
            NoCommands(rig);
        }
    }

    private static async Task InFlightMonitor()
    {
        using var rig = new TestRig();
        var job = await Held(rig);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Probe.BeforeCaptureAsync = async _ => { entered.SetResult(); await release.Task; };
        var tick = rig.TickAsync();
        await entered.Task;
        try
        {
            Check.Throws<InvalidOperationException>(() => rig.Engine.ResolveManually(job.Id, job.Run!.Id, false), "Do not detach a run while a monitor may still issue commands");
            Check.Equal(JobStatus.NeedsAttention, job.Status, "Rejected concurrent action must preserve the held row");
        }
        finally { release.SetResult(); await tick; }
    }
}
