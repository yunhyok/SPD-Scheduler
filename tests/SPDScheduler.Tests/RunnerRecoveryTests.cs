using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.Tests;

internal static class RunnerRecoveryTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return ("Saved marker plus readable exited identities confirms success", SavedAndGone);
        yield return ("Exit failure overrides a saved marker and holds completion", ExitFailed);
        yield return ("Saved marker cannot bypass incomplete process inventory", SavedWithIncompleteInventory);
        yield return ("Saved marker cannot bypass a known live descendant", SavedWithDescendant);
        yield return ("Another run's saved marker is never success", ForeignSavedMarker);
        yield return ("Persisted save intent without delivered control is not Saving", UndeliveredSaveIntent);
        yield return ("Delivered save token is idempotent during recovery", IdempotentSaveToken);
        yield return ("Unreadable child preserves fresh GUI start and completion across restart", ChildIdentityRecovery);
        yield return ("Unreadable root defers observation without authorizing stale GUI completion", RootIdentityRecovery);
        yield return ("Saved run waits for an unreadable child identity to resolve", SavedWithUnreadableChild);
        yield return ("Transient identity failure immediately before save keeps the queue armed", SaveIdentityRecovery);
    }

    private static JobItem Job(TestRig rig)
    {
        var directory = Path.Combine(rig.DirectoryPath, "run");
        Directory.CreateDirectory(directory);
        return new JobItem { FilePath = rig.CreateFile("synthetic.spd"), Status = JobStatus.Running,
            Run = new RunRecord { DirectoryPath = directory, CreatedAt = rig.Clock.Now,
                Process = new ProcessIdentity(int.MaxValue, rig.Clock.Now, @"C:\SyntheticOnly\PowerSI.exe") } };
    }

    private static Task<RunObservation> Inspect(JobItem job, bool complete = true, ProcessSample[]? samples = null) =>
        new PowerSiRunner().InspectAsync(job, new AppSettings(),
            new ProcessSnapshot(DateTimeOffset.UtcNow, samples ?? [], complete), CancellationToken.None);

    private static void Marker(JobItem job, string marker, Guid? runId = null) =>
        File.WriteAllText(Path.Combine(job.Run!.DirectoryPath, "events.log"), $"{(runId ?? job.Run.Id):N} {marker}\n");

    private static PowerSiRunner Runner(Func<ProcessIdentity, (bool Alive, bool Readable, string? Detail)> identity,
        Func<ProcessIdentity, CancellationToken, Task<string?>> status) => (PowerSiRunner)Activator.CreateInstance(
            typeof(PowerSiRunner), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [identity, status], null)!;

    private static async Task ChildIdentityRecovery()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        var run = job.Run!;
        var child = new ProcessIdentity(int.MaxValue - 1, rig.Clock.Now, @"C:\SyntheticOnly\solver.exe");
        run.Children.Add(child);
        File.WriteAllText(Path.Combine(run.DirectoryPath, "events.log"), $"{run.Id:N} OPENED\n{run.Id:N} BEGIN_REQUESTED\n");
        var snapshot = new ProcessSnapshot(rig.Clock.Now, [new ProcessSample(run.Process!, 0, 0, 0, 0)], true);
        bool childReadable = false;
        string status = "Simulation running";
        (bool, bool, string?) Identity(ProcessIdentity value) => value == child
            ? (false, childReadable, childReadable ? null : "Win32 5: access denied") : (true, true, null);
        Task<string?> Status(ProcessIdentity _, CancellationToken token) => Task.FromResult<string?>(status);
        var runner = Runner(Identity, Status);
        var waiting = await runner.InspectAsync(job, new(), snapshot, CancellationToken.None);
        Check.Equal(RunStage.ObservationPending, waiting.Stage, "A temporary child read error is a retryable observation");
        Check.That(waiting.Detail.Contains(child.Pid.ToString()) && waiting.Detail.Contains("solver.exe") && waiting.Detail.Contains("Win32 5"),
            "Diagnostic must identify the failed PID, image and error");
        Check.That(run.StartedObserved, "Root start evidence must not be lost because a child was unreadable");
        status = "Ready\nSimulation Done.";
        runner = Runner(Identity, Status);
        Check.Equal(RunStage.ObservationPending, (await runner.InspectAsync(job, new(), snapshot, CancellationToken.None)).Stage,
            "Even a completed GUI cannot bypass an unreadable owned child");
        Check.That(!File.Exists(Path.Combine(run.DirectoryPath, "completion.txt")), "No completion authorization while identities are unreadable");
        childReadable = true;
        Check.Equal(RunStage.ReadyToSave, (await runner.InspectAsync(job, new(), snapshot, CancellationToken.None)).Stage,
            "Persisted fresh start must allow completion after the identity recovers");
        await runner.RequestSaveAndExitAsync(job, CancellationToken.None);
        Check.Equal($"SAVE_EXIT {run.Id:N}", File.ReadAllText(Path.Combine(run.DirectoryPath, "control.txt")),
            "Recovery requests save only for the same run");
    }

    private static async Task RootIdentityRecovery()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        var run = job.Run!;
        File.WriteAllText(Path.Combine(run.DirectoryPath, "events.log"), $"{run.Id:N} OPENED\n{run.Id:N} BEGIN_REQUESTED\n");
        bool readable = false;
        int statusReads = 0;
        var runner = Runner(_ => (readable, readable, readable ? null : "Win32 5"), (_, _) =>
        { statusReads++; return Task.FromResult<string?>("Simulation Done."); });
        var snapshot = new ProcessSnapshot(rig.Clock.Now, [], true);
        Check.Equal(RunStage.ObservationPending, (await runner.InspectAsync(job, new(), snapshot, CancellationToken.None)).Stage,
            "Unknown root identity must hold without abandoning the run");
        Check.Equal(0, statusReads, "Do not read status through an unverified root identity");
        readable = true;
        Check.Equal(RunStage.Starting, (await runner.InspectAsync(job, new(), snapshot, CancellationToken.None)).Stage,
            "A Done status without fresh current-run start remains insufficient");
        Check.That(!File.Exists(Path.Combine(run.DirectoryPath, "completion.txt")), "Never authorize a stale completion");
    }

    private static async Task SavedWithUnreadableChild()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        var child = new ProcessIdentity(int.MaxValue - 1, rig.Clock.Now, @"C:\SyntheticOnly\solver.exe");
        job.Run!.Children.Add(child);
        Marker(job, "SAVED");
        bool readable = false;
        var runner = Runner(value => (false, value != child || readable, "Win32 5"),
            (_, _) => throw new InvalidOperationException("SAVED should not read GUI status"));
        var snapshot = new ProcessSnapshot(rig.Clock.Now, [], true);
        Check.Equal(RunStage.ObservationPending, (await runner.InspectAsync(job, new(), snapshot, CancellationToken.None)).Stage,
            "Save confirmation cannot prove an unreadable child has exited");
        readable = true;
        Check.Equal(RunStage.Succeeded, (await runner.InspectAsync(job, new(), snapshot, CancellationToken.None)).Stage,
            "Confirmed child exit must unblock the saved run");
    }

    private static async Task SaveIdentityRecovery()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        var run = job.Run!;
        run.DirectoryPath = Path.Combine(rig.Store.DataDirectory, "runs", run.Id.ToString("N"));
        Directory.CreateDirectory(run.DirectoryPath);
        File.WriteAllText(Path.Combine(run.DirectoryPath, "events.log"), $"{run.Id:N} OPENED\n{run.Id:N} BEGIN_REQUESTED\n");
        rig.Probe.Processes.Add(new ProcessSample(run.Process!, 0, 0, 0, 0));
        string status = "Simulation running";
        bool denyNextIdentity = false, injectOnce = true;
        var runner = Runner(_ =>
        {
            bool readable = !denyNextIdentity;
            denyNextIdentity = false;
            return (readable, readable, readable ? null : "Win32 5");
        }, (_, _) =>
        {
            if (status == "Simulation Done." && injectOnce) { denyNextIdentity = true; injectOnce = false; }
            return Task.FromResult<string?>(status);
        });
        await runner.InspectAsync(job, new(), await rig.Probe.CaptureAsync(CancellationToken.None), CancellationToken.None);
        rig.Store.Save(new QueueDocument { Jobs = [job, new JobItem { FilePath = rig.CreateFile("next.spd"), Order = 1 }],
            Settings = new AppSettings { PowerSiPath = rig.CreateFile("PowerSI.exe") } });
        var engine = new SchedulerEngine(rig.Store, runner, rig.Probe, rig.Clock);
        await engine.InitializeAsync();
        engine.Start();
        status = "Simulation Done.";
        await engine.TickAsync();
        Check.That(engine.IsArmed, "An unreadable save-time identity must not permanently pause the queue");
        Check.That(!File.Exists(Path.Combine(run.DirectoryPath, "control.txt")), "Do not save through an unreadable identity");
        await engine.TickAsync();
        Check.That(engine.IsArmed, "Successful re-observation keeps subsequent work armed");
        Check.Equal($"SAVE_EXIT {run.Id:N}", File.ReadAllText(Path.Combine(run.DirectoryPath, "control.txt")),
            "Only fresh re-observation after identity recovery may deliver save");
        Check.Equal(JobStatus.Queued, engine.Jobs.Last().Status, "Later work still waits for confirmed save and exit");
    }

    private static async Task SavedAndGone()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        Marker(job, "SAVED");
        var result = await Inspect(job);
        Check.Equal(RunStage.Succeeded, result.Stage, "Current-run saved marker and confirmed exits should complete");
        Check.That(!result.ProcessAlive, "Exited synthetic identity must not appear alive");
    }

    private static async Task SavedWithIncompleteInventory()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        Marker(job, "SAVED");
        var result = await Inspect(job, complete: false);
        Check.Equal(RunStage.Saving, result.Stage, "Incomplete process inventory cannot authorize completion");
    }

    private static async Task ExitFailed()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        File.WriteAllText(Path.Combine(job.Run!.DirectoryPath, "events.log"),
            $"{job.Run.Id:N} SAVED\n{job.Run.Id:N} EXIT_FAILED\n");
        var result = await Inspect(job);
        Check.Equal(RunStage.Unknown, result.Stage, "An exit error must require reconciliation even after save confirmation");
    }

    private static async Task SavedWithDescendant()
    {
        foreach (string image in new[] { "solver.exe", "BbsCon.exe" })
        {
            using var rig = new TestRig();
            var job = Job(rig);
            var child = new ProcessIdentity(int.MaxValue - 1, rig.Clock.Now, Path.Combine(@"C:\SyntheticOnly", image));
            job.Run!.Children.Add(child);
            Marker(job, "SAVED");
            var result = await Inspect(job, samples: [new ProcessSample(child, int.MaxValue, 0, 1024, 0)]);
            Check.Equal(RunStage.Saving, result.Stage, "External overlap permission must not bypass owned solver/BBS cleanup");
            Check.That(result.ProcessAlive, "Known descendant must count as alive");
        }
    }

    private static async Task ForeignSavedMarker()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        Marker(job, "SAVED", Guid.NewGuid());
        Check.Equal(RunStage.Unknown, (await Inspect(job)).Stage, "Another run's marker cannot be adopted");
    }

    private static async Task UndeliveredSaveIntent()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        job.Run!.SaveRequested = true;
        Check.Equal(RunStage.Unknown, (await Inspect(job)).Stage, "Persisted intent alone cannot claim the save command was delivered");
    }

    private static async Task IdempotentSaveToken()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        var path = Path.Combine(job.Run!.DirectoryPath, "control.txt");
        var token = $"SAVE_EXIT {job.Run.Id:N}";
        File.WriteAllText(path, token);
        var runner = new PowerSiRunner();
        await runner.RequestSaveAndExitAsync(job, CancellationToken.None);
        await runner.RequestSaveAndExitAsync(job, CancellationToken.None);
        Check.Equal(token, File.ReadAllText(path), "Idempotent recovery must preserve the delivered token");
        Check.That(job.Run.SaveRequested, "Recovered token should reconcile persisted intent");
    }
}
