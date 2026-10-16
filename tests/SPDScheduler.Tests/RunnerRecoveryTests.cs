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
