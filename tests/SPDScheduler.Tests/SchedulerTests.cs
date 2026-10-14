using SPDScheduler.Core;

namespace SPDScheduler.Tests;

internal static class SchedulerTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return ("Queue starts paused and respects Start/Pause", StartsPaused);
        yield return ("Pause during launch preflight prevents process creation", () => ChangesDuringPreflight("pause"));
        yield return ("Remove during launch preflight cannot create an orphan", () => ChangesDuringPreflight("remove"));
        yield return ("Reschedule during launch preflight defers the selected job", () => ChangesDuringPreflight("schedule"));
        yield return ("Final idle check allows activity below the configured threshold", () => FreshIdleCheck(false));
        yield return ("Final idle check blocks newly busy external PowerSI", () => FreshIdleCheck(true));
        yield return ("Due schedules precede FIFO and future jobs wait", ScheduledPriority);
        yield return ("Unscheduled jobs run in insertion order", FifoOrder);
        yield return ("A newly due job never preempts an owned run", NoPreemption);
        yield return ("Pause finishes owned work without launching another job", PauseFinishesOwnedRun);
        yield return ("Launching is persisted before runner invocation", PersistBeforeSpawn);
        yield return ("Ambiguous launch exception holds instead of allowing replay", AmbiguousLaunchException);
        yield return ("Confirmed pre-spawn launch failure allows the next job", PreSpawnFailure);
        yield return ("Unknown startup timeout holds without cleanup", UnknownStartup);
        yield return ("Startup confirmation timeout preserves the owned process", StartupTimeout);
        yield return ("Observed solver activity prevents a startup timeout", ActiveRunHasNoStartupTimeout);
        yield return ("Confirmed runtime failure cleans own identity then continues", () => FailureContinuesAfterCleanup(RunStage.RuntimeFailed));
        yield return ("Confirmed start failure cleans own identity then continues", () => FailureContinuesAfterCleanup(RunStage.StartFailed));
        yield return ("Unsafe cleanup pauses and blocks the next job", UnsafeCleanupHolds);
        yield return ("Pending cleanup blocks the next job", PendingCleanupHolds);
        yield return ("Save failure holds without closing or advancing", SaveFailureHolds);
        yield return ("Retry and Skip cannot abandon a live unresolved instance", LiveRunCannotBeAbandoned);
        yield return ("Executable path casing cannot mark a live run resolved", IdentityCasing);
        yield return ("An alive reattached descendant overrides an empty probe snapshot", ReattachedChildStillAlive);
        yield return ("Save intent is persisted before command delivery", PersistBeforeSave);
        yield return ("Recovered unbound launch never launches again automatically", RecoverUnboundLaunch);
        yield return ("Recovered owned run reattaches without duplication", RecoverOwnedRun);
        yield return ("Missing queued file fails safely before launch", MissingFile);
        yield return ("Only SPD files enter the queue", RejectNonSpd);
        yield return ("Queue persists schedules and workflow rules", PersistenceRoundTrip);
        yield return ("Failed persistence leaves the previous queue intact", FailedSavePreservesState);
        yield return ("Corrupt state is preserved and rejected", CorruptState);
        yield return ("Unsupported schema is preserved and rejected", UnsupportedSchema);
        yield return ("Settings reject unsafe thresholds", InvalidSettings);
    }

    private static async Task StartsPaused()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        rig.Add("one.spd");
        await rig.TickAsync();
        Check.That(!rig.Engine.IsArmed, "Initialization must not arm the queue");
        Check.Equal(0, rig.Runner.Launched.Count, "Paused queue must not launch");
        rig.Engine.Start();
        rig.Engine.Pause();
        await rig.TickAsync();
        Check.Equal(0, rig.Runner.Launched.Count, "Pause must prevent a pending launch");
        rig.Engine.Start();
        await rig.TickAsync();
        Check.Equal(1, rig.Runner.Launched.Count, "Start must arm queued work");
    }

    private static async Task ScheduledPriority()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        rig.Add("fifo.spd");
        var future = rig.Add("future.spd");
        var later = rig.Add("due-later.spd");
        var earlier = rig.Add("due-earlier.spd");
        rig.Engine.SetSchedule(future.Id, rig.Clock.Now.AddHours(1));
        rig.Engine.SetSchedule(later.Id, rig.Clock.Now.AddMinutes(-1));
        rig.Engine.SetSchedule(earlier.Id, rig.Clock.Now.AddMinutes(-2));
        rig.Engine.Start();
        await rig.TickAsync();
        Check.Equal(earlier.Id, rig.Runner.Launched.Single(), "Earliest due schedule must win");

        using var futureOnly = new TestRig();
        await futureOnly.InitializeAsync();
        var job = futureOnly.Add("later.spd");
        futureOnly.Engine.SetSchedule(job.Id, futureOnly.Clock.Now.AddMinutes(1));
        futureOnly.Engine.Start();
        await futureOnly.TickAsync();
        Check.Equal(0, futureOnly.Runner.Launched.Count, "Future schedule must wait");
        futureOnly.Clock.Advance(TimeSpan.FromMinutes(1));
        await futureOnly.TickAsync();
        Check.Equal(job.Id, futureOnly.Runner.Launched.Single(), "Schedule is due at its exact instant");
    }

    private static async Task ChangesDuringPreflight(string change)
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("preflight.spd");
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
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        switch (change)
        {
            case "pause": rig.Engine.Pause(); break;
            case "remove": rig.Engine.Remove(job.Id); break;
            case "schedule": rig.Engine.SetSchedule(job.Id, rig.Clock.Now.AddHours(1)); break;
        }
        release.SetResult();
        await tick;
        Check.Equal(0, rig.Runner.Launched.Count, $"{change} during preflight must prevent the pending launch");
        Check.That(!rig.Store.Load().Jobs.Any(item => item.Status == JobStatus.Starting),
            "Canceled preflight must not leave a launch-intent record");
    }

    private static async Task FifoOrder()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var first = rig.Add("first.spd");
        rig.Add("second.spd");
        rig.Engine.Start();
        await rig.TickAsync();
        Check.Equal(first.Id, rig.Runner.Launched.Single(), "FIFO must retain insertion order");
    }

    private static async Task FreshIdleCheck(bool becomesBusy)
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        rig.Engine.Settings.IdleSeconds = 10;
        rig.Add("after-external.spd");
        var identity = new ProcessIdentity(5000, rig.Clock.Now, @"C:\SyntheticOnly\PowerSI.exe");
        rig.Probe.BeforeCaptureAsync = capture =>
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(2));
            rig.Probe.Processes.Clear();
            rig.Probe.Processes.Add(new ProcessSample(identity, 0,
                capture * 0.001 + (becomesBusy && capture == 7 ? 1 : 0), 1024, (ulong)capture));
            return Task.CompletedTask;
        };
        rig.Engine.Start();
        await rig.TickAsync(6);
        Check.Equal(becomesBusy ? 0 : 1, rig.Runner.Launched.Count,
            "Launch preflight must reuse the configured resource thresholds");
    }

    private static async Task NoPreemption()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var running = rig.Add("running.spd");
        rig.Engine.Start();
        await rig.TickAsync();
        var due = rig.Add("due.spd");
        rig.Engine.SetSchedule(due.Id, rig.Clock.Now.AddMinutes(-1));
        await rig.TickAsync(3);
        Check.Equal(running.Id, rig.Runner.Launched.Single(), "Due work cannot overlap an active run");
        Check.Equal(0, rig.Runner.CleanupIdentities.Count, "Scheduling must not close an active run");
    }

    private static async Task PauseFinishesOwnedRun()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var first = rig.Add("first.spd");
        rig.Add("next.spd");
        rig.Engine.Start();
        await rig.TickAsync();
        rig.Engine.Pause();
        rig.Runner.Observation = new(RunStage.ReadyToSave, "Fresh terminal evidence", true);
        await rig.TickAsync();
        Check.Equal(first.Id, rig.Runner.SaveRequests.Single(), "Pause must still allow successful save");
        rig.Runner.Observation = new(RunStage.Succeeded, "Saved and closed", false);
        rig.Probe.Processes.Clear();
        await rig.TickAsync(2);
        Check.Equal(JobStatus.Completed, first.Status, "Owned run should finish while paused");
        Check.Equal(1, rig.Runner.Launched.Count, "Pause must block the following job");
    }

    private static async Task PersistBeforeSpawn()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var first = rig.Add("first.spd");
        var called = false;
        rig.Runner.BeforeLaunch = job =>
        {
            called = true;
            var persisted = new QueueStore(rig.Store.DataDirectory).Load().Jobs.Single(item => item.Id == job.Id);
            Check.Equal(JobStatus.Starting, persisted.Status, "Persist launch intent before invoking runner");
            Check.That(persisted.Run is not null && persisted.Run.Id != Guid.Empty, "Persist a run UUID before spawn");
            Check.Equal(job.Run!.Id, persisted.Run!.Id, "Persist the same run passed to the runner");
            Check.That(persisted.Run.Process is null, "The pre-spawn record cannot claim a process yet");
        };
        rig.Engine.Start();
        await rig.TickAsync();
        Check.That(called, "Fake launch hook should run");
        Check.Equal(first.Id, rig.Runner.Launched.Single(), "The intended job should launch");
    }

    private static async Task UnknownStartup()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("unknown.spd");
        rig.Add("next.spd");
        rig.Runner.Observation = new(RunStage.Unknown, "No verified startup signal", true);
        rig.Engine.Start();
        await rig.TickAsync();
        rig.Clock.Advance(TimeSpan.FromMinutes(11));
        await rig.TickAsync(2);
        Check.Equal(JobStatus.NeedsAttention, job.Status, "Unknown startup must require attention after timeout");
        Check.That(!rig.Engine.IsArmed, "Unknown startup must disarm the queue");
        Check.Equal(0, rig.Runner.CleanupIdentities.Count, "Missing activity must not authorize termination");
        Check.Equal(0, rig.Runner.SaveRequests.Count, "Unknown completion must not authorize saving");
        Check.Equal(1, rig.Runner.Launched.Count, "Unknown startup must block following jobs");
    }

    private static async Task AmbiguousLaunchException()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("ambiguous.spd");
        rig.Add("next.spd");
        rig.Runner.ThrowAfterSpawn = true;
        rig.Engine.Start();
        await rig.TickAsync(2);
        Check.Equal(JobStatus.NeedsAttention, job.Status, "A launch exception cannot prove no process was created");
        Check.That(!rig.Engine.IsArmed, "Unbound launch exception must disarm");
        Check.Equal(1, rig.Runner.Launched.Count, "Ambiguous launch must not be repeated automatically");
        Check.Equal(0, rig.Runner.CleanupIdentities.Count, "Unbound identity must not authorize cleanup");
    }

    private static async Task PreSpawnFailure()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var first = rig.Add("rejected.spd");
        var next = rig.Add("next.spd");
        rig.Runner.BeforeLaunch = _ => throw new IOException("Synthetic invalid executable before any process creation");
        rig.Engine.Start();
        await rig.TickAsync();
        Check.Equal(JobStatus.Failed, first.Status, "A confirmed pre-spawn error should fail this job");
        Check.Equal(0, rig.Probe.Processes.Count, "The fixture must not fabricate a process before rejection");
        rig.Runner.BeforeLaunch = null;
        await rig.TickAsync();
        Check.Equal(next.Id, rig.Runner.Launched.Single(), "A pre-spawn error should not stall later work");
    }

    private static async Task StartupTimeout()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("starting.spd");
        rig.Runner.Observation = new(RunStage.Starting, "Waiting for startup confirmation", true);
        rig.Engine.Start();
        await rig.TickAsync();
        rig.Clock.Advance(TimeSpan.FromSeconds(599));
        await rig.TickAsync();
        Check.Equal(JobStatus.Starting, job.Status, "Startup must be allowed its configured confirmation window");
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.TickAsync();
        Check.Equal(JobStatus.NeedsAttention, job.Status, "The exact ten-minute boundary should require attention");
        Check.Equal(0, rig.Runner.CleanupIdentities.Count, "A startup timeout cannot authorize termination");
    }

    private static async Task ActiveRunHasNoStartupTimeout()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("active.spd");
        rig.Engine.Start();
        await rig.TickAsync(2);
        rig.Clock.Advance(TimeSpan.FromDays(2));
        rig.Runner.Observation = new(RunStage.Running, "Solver still running", true, false);
        await rig.TickAsync();
        Check.That(job.Status is JobStatus.Running or JobStatus.Starting, "Long solves must remain active");
        Check.Equal(0, rig.Runner.CleanupIdentities.Count, "A quiet active solve must not be killed");
    }

    private static async Task FailureContinuesAfterCleanup(RunStage stage)
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var failed = rig.Add("failed.spd");
        var next = rig.Add("next.spd");
        rig.Engine.Start();
        await rig.TickAsync();
        var owned = failed.Run!.Process!;
        var external = new ProcessIdentity(20_001, rig.Clock.Now, "external-powersi.exe");
        rig.Probe.Processes.Add(new ProcessSample(external, 0, 10, 200 * 1024 * 1024, 0, Signal: ProcessSignal.Running));
        rig.Runner.Observation = new(stage, "Confirmed solver error", true);
        await rig.TickAsync();
        Check.Equal(JobStatus.Failed, failed.Status, "Confirmed failure should remain visible");
        Check.Equal(owned, rig.Runner.CleanupIdentities.Single(), "Cleanup must target only the failed owned instance");
        Check.That(rig.Probe.Processes.Any(sample => sample.Identity == external), "External instance must be untouched");
        Check.Equal(1, rig.Runner.Launched.Count, "Busy external process still blocks continuation");
        rig.Probe.Processes.Clear();
        rig.Runner.Observation = new(RunStage.Running, "Next run", true, true);
        await rig.TickAsync(2);
        Check.Equal(next.Id, rig.Runner.Launched.Last(), "Queue should continue after successful failure cleanup");
    }

    private static async Task UnsafeCleanupHolds()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        rig.Add("failed.spd");
        rig.Add("next.spd");
        rig.Engine.Start();
        await rig.TickAsync();
        rig.Runner.Cleanup = CleanupState.Unsafe;
        rig.Runner.Observation = new(RunStage.RuntimeFailed, "Confirmed error", true);
        await rig.TickAsync(2);
        Check.That(!rig.Engine.IsArmed, "Unsafe cleanup must disarm");
        Check.Equal(1, rig.Runner.Launched.Count, "Unsafe cleanup must prevent overlap");
    }

    private static async Task PendingCleanupHolds()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        rig.Add("failed.spd");
        rig.Add("next.spd");
        rig.Engine.Start();
        await rig.TickAsync();
        rig.Runner.Cleanup = CleanupState.Pending;
        rig.Runner.Observation = new(RunStage.RuntimeFailed, "Confirmed error", true);
        await rig.TickAsync(3);
        Check.Equal(1, rig.Runner.Launched.Count, "Pending cleanup must block the next launch");
    }

    private static async Task SaveFailureHolds()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("save-failure.spd");
        rig.Add("next.spd");
        rig.Engine.Start();
        await rig.TickAsync();
        rig.Runner.Observation = new(RunStage.SaveFailed, "Disk full", true);
        await rig.TickAsync(2);
        Check.Equal(JobStatus.NeedsAttention, job.Status, "Unsaved result must require attention");
        Check.That(!rig.Engine.IsArmed, "Save failure must disarm");
        Check.Equal(0, rig.Runner.CleanupIdentities.Count, "Save failure must preserve the unsaved GUI");
        Check.Equal(1, rig.Runner.Launched.Count, "Save failure must prevent next launch");
    }

    private static async Task PersistBeforeSave()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        rig.Add("completed.spd");
        rig.Engine.Start();
        await rig.TickAsync();
        rig.Runner.BeforeSave = job =>
        {
            var persisted = rig.Store.Load().Jobs.Single(item => item.Id == job.Id);
            Check.That(persisted.Run!.SaveRequested, "Save intent must survive a crash before token delivery");
        };
        rig.Runner.Observation = new(RunStage.ReadyToSave, "Fresh terminal signal", true);
        await rig.TickAsync(3);
        Check.That(rig.Runner.SaveRequests.Count >= 1, "Fresh completion must request save delivery");
        Check.Equal(0, rig.Runner.CleanupIdentities.Count, "Success must not use failed-run cleanup");
    }

    private static async Task LiveRunCannotBeAbandoned()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("unsaved.spd");
        rig.Add("next.spd");
        rig.Engine.Start();
        await rig.TickAsync();
        var runId = job.Run!.Id;
        rig.Runner.Observation = new(RunStage.SaveFailed, "Unsaved result", true);
        await rig.TickAsync();
        foreach (var action in new Action[] { () => rig.Engine.Retry(job.Id), () => rig.Engine.Skip(job.Id) })
        {
            try { action(); }
            catch (InvalidOperationException) { }
            Check.Equal(JobStatus.NeedsAttention, job.Status, "An unresolved live instance must retain attention status");
            Check.Equal(runId, job.Run!.Id, "User actions must preserve the unresolved run identity");
        }
        try { rig.Engine.Start(); }
        catch (InvalidOperationException) { }
        await rig.TickAsync();
        Check.Equal(1, rig.Runner.Launched.Count, "Start cannot bypass an unresolved instance");
    }

    private static async Task RecoverUnboundLaunch()
    {
        using var rig = new TestRig();
        var job = new JobItem { FilePath = rig.CreateFile("interrupted.spd"), Status = JobStatus.Starting,
            Run = new RunRecord { CreatedAt = rig.Clock.Now } };
        await rig.InitializeAsync(new QueueDocument { Jobs = [job] });
        Check.That(!rig.Engine.IsArmed, "Recovery must remain paused");
        try { rig.Engine.Start(); }
        catch (InvalidOperationException) { }
        await rig.TickAsync(2);
        Check.Equal(0, rig.Runner.Launched.Count, "Unbound interrupted launch must not be replayed");
        Check.Equal(JobStatus.NeedsAttention, rig.Engine.Jobs.Single().Status, "Ambiguous launch must remain visible");
    }

    private static async Task IdentityCasing()
    {
        using var rig = new TestRig();
        var identity = new ProcessIdentity(12_345, rig.Clock.Now.AddMinutes(-1), @"C:\CADENCE\POWERSI.EXE");
        var job = new JobItem { FilePath = rig.CreateFile("unsaved.spd"), Status = JobStatus.NeedsAttention,
            Run = new RunRecord { CreatedAt = identity.StartedAt, Process = identity, StartedObserved = true } };
        rig.Probe.Processes.Add(new ProcessSample(identity with { ImagePath = @"c:\Cadence\PowerSI.exe" }, 0, 20, 100 * 1024 * 1024, 0));
        rig.Runner.Observation = new(RunStage.SaveFailed, "Unsaved result is still open", true);
        await rig.InitializeAsync(new QueueDocument { Jobs = [job] });
        await rig.TickAsync();
        var recovered = rig.Engine.Jobs.Single();
        foreach (var action in new Action[] { () => rig.Engine.Retry(recovered.Id), () => rig.Engine.Skip(recovered.Id) })
        {
            try { action(); }
            catch (InvalidOperationException) { }
            Check.Equal(JobStatus.NeedsAttention, recovered.Status, "Windows path casing cannot authorize abandoning a live process");
            Check.Equal(identity, recovered.Run!.Process!, "Unresolved ownership must remain intact");
        }
    }

    private static async Task ReattachedChildStillAlive()
    {
        using var rig = new TestRig();
        var identity = new ProcessIdentity(12_345, rig.Clock.Now.AddMinutes(-1), @"C:\Synthetic\PowerSI.exe");
        var child = new ProcessIdentity(12_346, rig.Clock.Now, @"C:\Synthetic\solver.exe");
        var job = new JobItem { FilePath = rig.CreateFile("unsaved.spd"), Status = JobStatus.NeedsAttention,
            Run = new RunRecord { CreatedAt = identity.StartedAt, Process = identity, Children = [child], StartedObserved = true } };
        rig.Runner.Observation = new(RunStage.Unknown, "Persisted child identity is still alive after the root exited", true);
        await rig.InitializeAsync(new QueueDocument { Jobs = [job] });
        await rig.TickAsync();
        var recovered = rig.Engine.Jobs.Single();
        try { rig.Engine.Retry(recovered.Id); }
        catch (InvalidOperationException) { }
        Check.Equal(JobStatus.NeedsAttention, recovered.Status, "A confirmed live child must override its absence from a filtered process snapshot");
        Check.Equal(child, recovered.Run!.Children.Single(), "Unresolved descendant ownership must be preserved");
    }

    private static async Task RecoverOwnedRun()
    {
        using var rig = new TestRig();
        var identity = new ProcessIdentity(12_345, rig.Clock.Now.AddMinutes(-1), @"C:\Synthetic\PowerSI.exe");
        var job = new JobItem { FilePath = rig.CreateFile("running.spd"), Status = JobStatus.Running,
            Run = new RunRecord { CreatedAt = identity.StartedAt, Process = identity, StartedObserved = true } };
        rig.Probe.Processes.Add(new ProcessSample(identity, 0, 20, 100 * 1024 * 1024, 0));
        await rig.InitializeAsync(new QueueDocument { Jobs = [job] });
        await rig.TickAsync();
        Check.Equal(0, rig.Runner.Launched.Count, "Reattach must not spawn a replacement");
        Check.Equal(identity, rig.Engine.Jobs.Single().Run!.Process!, "Recovery must retain the full identity");
        Check.That(!rig.Engine.IsArmed, "Reattached run must not arm following work");
    }

    private static async Task MissingFile()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("deleted.spd");
        File.Delete(job.FilePath);
        rig.Engine.Start();
        await rig.TickAsync();
        Check.Equal(JobStatus.Failed, job.Status, "Missing input must fail with a visible status");
        Check.That(!string.IsNullOrWhiteSpace(job.LastError), "Missing input must explain the failure");
        Check.Equal(0, rig.Runner.Launched.Count, "Missing input must not reach process launch");
    }

    private static async Task RejectNonSpd()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var invalid = rig.CreateFile("notes.txt");
        var missing = Path.Combine(rig.DirectoryPath, "missing.spd");
        Check.Equal(2, rig.Engine.AddFiles([invalid, missing]).Count, "Invalid or missing inputs must be rejected");
        Check.Equal(0, rig.Engine.Jobs.Count, "Rejected inputs must not become queued jobs");
        rig.Add("CAPITAL.SPD");
    }

    private static Task PersistenceRoundTrip()
    {
        using var rig = new TestRig();
        var at = new DateTimeOffset(2026, 10, 14, 14, 30, 0, TimeSpan.FromHours(9));
        var job = new JobItem { FilePath = rig.CreateFile("문자 } [한글] $ fixture.spd"), ScheduledAt = at,
            ScheduleTimeZone = "Korea Standard Time", Order = 4 };
        var document = new QueueDocument { Jobs = [job], Settings = new AppSettings { CompletionRules =
            [new() { Workflow = "extraction", SpdPattern = "extraction", StartedPattern = "start-a", CompletedPattern = "done-a" },
             new() { Workflow = "another-mode", SpdPattern = "another", StartedPattern = "start-b", CompletedPattern = "done-b" }] } };
        rig.Store.Save(document);
        document.Jobs[0].Order = 5;
        rig.Store.Save(document);
        var loaded = new QueueStore(rig.Store.DataDirectory).Load();
        Check.Equal(job.FilePath, loaded.Jobs.Single().FilePath, "Unicode and Tcl metacharacters must survive storage");
        Check.Equal(at, loaded.Jobs.Single().ScheduledAt!.Value, "Schedule instant must survive storage");
        Check.Equal(5L, loaded.Jobs.Single().Order, "Second atomic save must replace the first document");
        Check.Equal(2, loaded.Settings.CompletionRules.Count, "All workflow modes must survive storage");
        return Task.CompletedTask;
    }

    private static string QueuePath(TestRig rig)
    {
        rig.Store.Save(new QueueDocument());
        return Directory.GetFiles(rig.Store.DataDirectory, "*.json").Single();
    }

    private static Task FailedSavePreservesState()
    {
        using var rig = new TestRig();
        var path = QueuePath(rig);
        var before = File.ReadAllText(path);
        var invalid = new QueueDocument { Settings = new AppSettings { IdleCpuPercent = double.NaN } };
        Check.Throws<ArgumentException>(() => rig.Store.Save(invalid), "Unserializable values must fail explicitly");
        Check.Equal(before, File.ReadAllText(path), "Failed atomic save must leave previous state intact");
        Check.Equal(0, Directory.GetFiles(rig.Store.DataDirectory, "*.tmp").Length, "Failed save must remove its temporary file");
        return Task.CompletedTask;
    }

    private static Task CorruptState()
    {
        using var rig = new TestRig();
        var path = QueuePath(rig);
        const string corrupt = "{\"jobs\":[unexpected";
        File.WriteAllText(path, corrupt);
        Check.Throws<InvalidDataException>(() => rig.Store.Load(), "Corrupt state must produce an actionable error");
        Check.Equal(corrupt, File.ReadAllText(path), "Load must preserve corrupt state for recovery");
        return Task.CompletedTask;
    }

    private static Task UnsupportedSchema()
    {
        using var rig = new TestRig();
        var path = QueuePath(rig);
        const string unsupported = "{\"SchemaVersion\":999,\"Jobs\":[]}";
        File.WriteAllText(path, unsupported);
        Check.Throws<InvalidDataException>(() => rig.Store.Load(), "Future schema must not be interpreted as an empty queue");
        Check.Equal(unsupported, File.ReadAllText(path), "Future schema must be preserved");
        return Task.CompletedTask;
    }

    private static async Task InvalidSettings()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        foreach (var invalid in new AppSettings[]
        {
            new() { IdleSeconds = 0 }, new() { IdleCpuPercent = double.NaN },
            new() { IdleMemoryMiB = -1 }, new() { IdleIoKiBPerSecond = -1 }, new() { StartTimeoutMinutes = 0 }
        })
        {
            Check.Throws<ArgumentException>(() => rig.Engine.UpdateSettings(invalid), "Invalid thresholds must be rejected");
        }
        Check.Equal(180, rig.Engine.Settings.IdleSeconds, "Rejected settings must not replace valid settings");
    }
}
