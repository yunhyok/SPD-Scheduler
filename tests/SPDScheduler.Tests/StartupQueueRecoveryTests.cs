using SPDScheduler.Core;

namespace SPDScheduler.Tests;

internal static class StartupQueueRecoveryTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return ("An eighteen-minute startup hold recovers and advances after idle confirmation", SlowStartupRecovers);
        yield return ("Pause before a startup timeout remains paused after recovery", () => ExplicitPauseRemainsPaused(true));
        yield return ("Pause during a startup hold remains paused after recovery", () => ExplicitPauseRemainsPaused(false));
        yield return ("Restarted startup hold reconciles without arming queued work", RestartRemainsPaused);
        yield return ("Other attention failures cannot be rearmed by startup recovery", OtherFailureRemainsPaused);
        yield return ("Manual resolution of a startup hold leaves queued work paused", ManualResolutionRemainsPaused);
        yield return ("Owned monitoring uses current root and child evidence instead of old external details", CurrentOwnedMonitoring);
    }

    private static async Task SlowStartupRecovers()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var first = rig.Add("slow-start.spd");
        var next = rig.Add("after-slow-start.spd");
        rig.Runner.Observation = new(RunStage.Starting, "Synthetic startup still pending", true);
        rig.Engine.Start();
        await rig.TickAsync();
        var runId = first.Run!.Id;
        rig.Clock.Advance(TimeSpan.FromMinutes(10));
        await rig.TickAsync();
        Check.Equal(JobStatus.NeedsAttention, first.Status, "Timeout remains visible as a hold");
        Check.That(rig.Engine.IsArmed, "A recoverable startup timeout retains prior Start intent");
        Check.That(first.LastError!.Contains("계속 재확인"), "The hold must explain continued observation");
        Check.Throws<InvalidOperationException>(() => rig.Engine.Start(), "An unresolved hold cannot be bypassed with Start");
        rig.Clock.Advance(TimeSpan.FromMinutes(8));
        await rig.TickAsync(3);
        Check.Equal(1, rig.Runner.Launched.Count, "A held run blocks the next launch despite retained Start intent");
        Check.Equal(0, rig.Runner.CleanupIdentities.Count, "Startup delay does not authorize cleanup");
        Check.Equal(0, rig.Runner.SaveRequests.Count, "Startup delay does not authorize save");

        rig.Runner.Observation = new(RunStage.Running, "Synthetic running after eighteen minutes", true);
        await rig.TickAsync();
        Check.Equal(JobStatus.Running, first.Status, "Fresh running evidence clears the hold");
        Check.That(first.LastError is null && first.Run!.Id == runId, "Recovery clears only the diagnostic and keeps the original run");
        rig.Runner.Observation = new(RunStage.ReadyToSave, "Synthetic current-run completion", true);
        await rig.TickAsync();
        Check.Equal(JobStatus.Saving, first.Status, "Recovered run proceeds to save");
        Check.Equal(first.Id, rig.Runner.SaveRequests.Single(), "Save targets the recovered job");
        rig.Probe.Processes.Clear();
        var external = new ProcessIdentity(70_001, rig.Clock.Now, @"C:\SyntheticOnly\PowerSI.exe");
        rig.Probe.Processes.Add(new ProcessSample(external, 0, 0, 1024, 0));
        rig.Runner.Observation = new(RunStage.Succeeded, "Synthetic saved and closed", false);
        await rig.TickAsync(2);
        Check.Equal(JobStatus.Completed, first.Status, "Recovered run completes normally");
        Check.Equal(1, rig.Runner.Launched.Count, "The next launch still requires external idle confirmation");
        rig.Clock.Advance(TimeSpan.FromSeconds(rig.Engine.Settings.IdleSeconds));
        await rig.TickAsync();
        Check.Equal(next.Id, rig.Runner.Launched.Last(), "The next job starts after the normal idle gate");
    }

    private static async Task ExplicitPauseRemainsPaused(bool beforeTimeout)
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var first = rig.Add("paused-start.spd");
        rig.Add("after-paused-start.spd");
        rig.Runner.Observation = new(RunStage.Starting, "Synthetic pending startup", true);
        rig.Engine.Start();
        await rig.TickAsync();
        if (beforeTimeout) rig.Engine.Pause();
        rig.Clock.Advance(TimeSpan.FromMinutes(18));
        await rig.TickAsync();
        if (!beforeTimeout) rig.Engine.Pause();
        await rig.TickAsync();
        Check.That(!rig.Engine.IsArmed, "A timeout must preserve explicit Pause");
        await CompleteRecoveredRun(rig, first);
        Check.That(!rig.Engine.IsArmed, "Successful recovery cannot rearm an explicitly paused queue");
        Check.Equal(1, rig.Runner.Launched.Count, "Explicit Pause still blocks the next job");
    }

    private static async Task RestartRemainsPaused()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var first = rig.Add("restart-start.spd");
        var next = rig.Add("after-restart-start.spd");
        rig.Engine.Start();
        await rig.TickAsync();
        rig.Runner.Observation = new(RunStage.Starting, "Synthetic pending startup", true);
        rig.Clock.Advance(TimeSpan.FromMinutes(18));
        await rig.TickAsync();
        first.LastError = "Persisted v0.1.7 startup attention reason";
        rig.Engine.Save();
        var restarted = new SchedulerEngine(rig.Store, rig.Runner, rig.Probe, rig.Clock);
        await restarted.InitializeAsync();
        await restarted.TickAsync();
        Check.That(!restarted.IsArmed, "Repeated startup timeout after restart must remain paused");
        rig.Runner.Observation = new(RunStage.Running, "Synthetic running after restart", true);
        await restarted.TickAsync();
        Check.Equal(JobStatus.Running, restarted.Jobs.Single(job => job.Id == first.Id).Status, "Running can reconcile a persisted hold");
        Check.That(!restarted.IsArmed, "Reconciliation does not restore Start intent across a restart");
        rig.Runner.Observation = new(RunStage.ReadyToSave, "Synthetic completion after restart", true);
        await restarted.TickAsync();
        rig.Probe.Processes.Clear();
        rig.Runner.Observation = new(RunStage.Succeeded, "Synthetic closed after restart", false);
        await restarted.TickAsync();
        await restarted.TickAsync();
        Check.Equal(1, rig.Runner.Launched.Count, "Restart recovery must not replay or advance queued work");
        restarted.Start();
        await restarted.TickAsync();
        Check.Equal(next.Id, rig.Runner.Launched.Last(), "Explicit Start after recovery resumes queued work");
    }

    private static async Task OtherFailureRemainsPaused()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var first = rig.Add("other-hold.spd");
        rig.Add("after-other-hold.spd");
        rig.Engine.Start();
        await rig.TickAsync();
        rig.Runner.Observation = new(RunStage.Unknown, "Synthetic ambiguous failure", true);
        await rig.TickAsync();
        Check.That(!rig.Engine.IsArmed, "Other attention reasons still pause the queue");
        rig.Clock.Advance(TimeSpan.FromMinutes(18));
        rig.Runner.Observation = new(RunStage.Starting, "Synthetic pending after another failure", true);
        await rig.TickAsync();
        Check.That(!rig.Engine.IsArmed, "A later recoverable timeout cannot arm a failure-paused queue");
        await CompleteRecoveredRun(rig, first);
        Check.That(!rig.Engine.IsArmed, "Later recovery leaves other failure pauses intact");
        Check.Equal(1, rig.Runner.Launched.Count, "Other failure recovery does not advance queued work");
    }

    private static async Task ManualResolutionRemainsPaused()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var first = rig.Add("manual-start.spd");
        rig.Add("after-manual-start.spd");
        rig.Engine.Start();
        await rig.TickAsync();
        rig.Runner.Observation = new(RunStage.Starting, "Synthetic pending startup", true);
        rig.Clock.Advance(TimeSpan.FromMinutes(18));
        await rig.TickAsync();
        rig.Engine.ResolveManually(first.Id, first.Run!.Id, remove: false);
        rig.Probe.Processes.Clear();
        await rig.TickAsync();
        Check.That(!rig.Engine.IsArmed, "Manual resolution pauses even a recoverable timeout hold");
        Check.Equal(1, rig.Runner.Launched.Count, "Manual resolution requires explicit Start for the next job");
    }

    private static async Task CurrentOwnedMonitoring()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var first = rig.Add("monitor-owned.spd");
        var oldExternal = new ProcessIdentity(70_002, rig.Clock.Now, @"C:\SyntheticOnly\PowerSI.exe");
        rig.Probe.Processes.Add(new ProcessSample(oldExternal, 0, 0, 1024, 0,
            Signal: ProcessSignal.Running, Evidence: "STALE_EXTERNAL_EVIDENCE"));
        rig.Engine.Start();
        await rig.TickAsync();
        Check.That(rig.Engine.MonitoringDetails.Contains("STALE_EXTERNAL_EVIDENCE"), "Fixture seeds external monitoring details");
        rig.Probe.Processes.Clear();
        await rig.TickAsync();
        var run = first.Run!;
        var root = run.Process!;
        var child = new ProcessIdentity(70_003, rig.Clock.Now, @"C:\SyntheticOnly\Solver.exe");
        run.Children.Add(child);
        rig.Probe.Processes.Clear();
        rig.Probe.Processes.Add(new ProcessSample(root, 0, 0, 1024, 0,
            Signal: ProcessSignal.Running, Evidence: "CURRENT_ROOT_EVIDENCE"));
        rig.Probe.Processes.Add(new ProcessSample(child, root.Pid, 0, 1024, 0,
            Signal: ProcessSignal.LicenseWaiting, Evidence: "CURRENT_CHILD_EVIDENCE"));
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        rig.Runner.Observation = new(RunStage.Running, "CURRENT_RUN_OBSERVATION", true);
        await rig.TickAsync();
        var details = rig.Engine.MonitoringDetails;
        Check.That(details.Contains(rig.Clock.Now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")), "Owned details show the fresh snapshot time");
        Check.That(details.Contains(run.Id.ToString()) && details.Contains($"PID {root.Pid}") && details.Contains("Running"), "Owned details show current identity and job status");
        Check.That(details.Contains($"PID {child.Pid}") && details.Contains("LicenseWaiting") && details.Contains("CURRENT_CHILD_EVIDENCE"), "Owned children show current status and evidence");
        Check.That(details.Contains("CURRENT_RUN_OBSERVATION") && details.Contains("CURRENT_ROOT_EVIDENCE"), "Owned details include current inspection and root evidence");
        Check.That(!details.Contains("STALE_EXTERNAL_EVIDENCE"), "Old external evidence cannot masquerade as current owned monitoring");

        rig.Probe.Complete = false;
        rig.Probe.Processes.Clear();
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await rig.TickAsync();
        details = rig.Engine.MonitoringDetails;
        Check.That(details.Contains("관측 불완전") && details.Contains("종료 여부 확인 불가"), "Incomplete observations explicitly preserve uncertainty");
        Check.That(details.Contains(rig.Clock.Now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")), "Incomplete details still show the new snapshot time");
        Check.That(!details.Contains("CURRENT_RUN_OBSERVATION") && !details.Contains("CURRENT_ROOT_EVIDENCE") && !details.Contains("STALE_EXTERNAL_EVIDENCE"),
            "An incomplete fresh snapshot cannot reuse old process evidence or inspection detail");
    }

    private static async Task CompleteRecoveredRun(TestRig rig, JobItem job)
    {
        rig.Runner.Observation = new(RunStage.Running, "Synthetic recovered running", true);
        await rig.TickAsync();
        rig.Runner.Observation = new(RunStage.ReadyToSave, "Synthetic recovered completion", true);
        await rig.TickAsync();
        rig.Probe.Processes.Clear();
        rig.Runner.Observation = new(RunStage.Succeeded, "Synthetic recovered save and close", false);
        await rig.TickAsync(2);
        Check.Equal(JobStatus.Completed, job.Status, "A paused queue still reconciles its owned run to completion");
    }
}
