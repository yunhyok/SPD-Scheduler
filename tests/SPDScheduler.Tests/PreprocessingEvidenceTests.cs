using System.Globalization;
using System.Text.Json;
using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.Tests;

internal static class PreprocessingEvidenceTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return ("Native preprocessing records establish current-run start without a workflow rule", NativeProgress);
        yield return ("Headers, old records and malformed preprocessing records cannot establish start", InsufficientLogs);
        yield return ("Preprocessing evidence requires both markers from this run", CurrentRunMarkers);
        yield return ("BEGIN_RETURNED alone cannot establish start or completion", BeginReturnedOnly);
        yield return ("Done on the first preprocessing observation remains stale", DoneFirst);
        yield return ("Readable idle GUI must be observed at or after preprocessing start", NonCompletedAfterStart);
        yield return ("Unreadable and empty GUI cannot authorize later Done", UnreadableGui);
        yield return ("Persisted preprocessing start and readable transition survive runner restart", RestartPersistence);
        yield return ("Unknown workflow AFS completion text cannot authorize completion", OtherWorkflowCompletion);
        yield return ("Simulation running establishes start regardless of letter case", CaseInsensitiveRunning);
        yield return ("Case changes in an old Done pane cannot manufacture a fresh transition", CaseVariantDone);
        yield return ("Blocked GUI overrides Done after preprocessing start", BlockedUi);
        yield return ("Preprocessing evidence retains process identity and inventory guards", IdentityGuards);
        yield return ("Preprocessing completion rechecks GUI before delivering save and waits for exit", SaveAndExit);
    }

    private static JobItem Job(TestRig rig)
    {
        string directory = Path.Combine(rig.DirectoryPath, "run");
        Directory.CreateDirectory(directory);
        var created = rig.Clock.Now.AddMilliseconds(750);
        return new JobItem { FilePath = rig.CreateFile("synthetic.spd"), Status = JobStatus.Running,
            Run = new RunRecord { DirectoryPath = directory, CreatedAt = created,
                Process = new ProcessIdentity(int.MaxValue, created, @"C:\SyntheticOnly\PowerSI.exe") } };
    }

    private static PowerSiRunner Runner(Func<string?> status,
        Func<ProcessIdentity, (bool Alive, bool Readable, string? Detail)>? identity = null) =>
        (PowerSiRunner)Activator.CreateInstance(typeof(PowerSiRunner),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [identity ?? (_ => (true, true, null)),
                (Func<ProcessIdentity, CancellationToken, Task<string?>>)((_, _) => Task.FromResult(status()))], null)!;

    private static Task<RunObservation> Inspect(PowerSiRunner runner, JobItem job, bool complete = true) =>
        runner.InspectAsync(job, new AppSettings(), new ProcessSnapshot(job.Run!.CreatedAt, [], complete), CancellationToken.None);

    private static void Markers(JobItem job, params string[] markers) =>
        File.WriteAllText(Path.Combine(job.Run!.DirectoryPath, "events.log"),
            string.Join('\n', markers.Select(marker => $"{job.Run.Id:N} {marker}")));

    private static string Record(JobItem job, string message, int seconds = 0) =>
        $"[{job.Run!.CreatedAt.LocalDateTime.AddSeconds(seconds).ToString("yyyy/MM/dd-HH:mm:ss", CultureInfo.InvariantCulture)}]--{message}\n";

    private static void Output(JobItem job, string text) =>
        File.WriteAllText(Path.Combine(job.Run!.DirectoryPath, "output.log"), text);

    private static void NoSave(JobItem job)
    {
        Check.That(!File.Exists(Path.Combine(job.Run!.DirectoryPath, "completion.txt")), "Insufficient evidence must not authorize completion");
        Check.That(!File.Exists(Path.Combine(job.Run.DirectoryPath, "control.txt")) && !job.Run.SaveRequested,
            "Observation must not deliver a save/exit request");
    }

    private static async Task NativeProgress()
    {
        foreach (string message in new[] { "Arranging Layout ...", "Arranging Circuits ...", "Processing Layout ...", "Processing Circuits ...",
            "Arranging Layout - Shapes", "Arranging Layout - Layers", "Arranging Layout - Nodes", "Arranging Layout - Vias",
            "Arranging Layout - Traces", "Arranging Layout - Coupled Traces", "Processing On Plane Via Nodes",
            "Processing On Plane Wire-bond Nodes", "Setting Node Property", "Handling Trace-Patch Connections",
            "Handling Overlapped Traces", "Handling Pads", "Handling Trace-Via and Trace-Pad Connections" })
        {
            using var rig = new TestRig();
            var job = Job(rig);
            Markers(job, "OPENED", "BEGIN_REQUESTED");
            Output(job, Record(job, message));
            Check.Equal(RunStage.Running, (await Inspect(Runner(() => null), job)).Stage,
                $"Native message in the creation second must establish start: {message}");
            Check.That(job.Run!.StartedObserved, "Start must be visible to the scheduler without a GUI response or completion rule");
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(job.Run.DirectoryPath, "freshness.json")));
            Check.That(saved.RootElement.GetProperty("Started").GetBoolean(), "Native start evidence must be persisted");
            Check.That(!saved.RootElement.GetProperty("SawNonCompletedUi").GetBoolean(), "A null GUI cannot count as a readable transition");
            NoSave(job);
        }
    }

    private static async Task InsufficientLogs()
    {
        foreach (string kind in new[] { "empty", "header", "old", "bad-date", "missing-prefix", "bad-separator", "near-message", "suffix" })
        {
            using var rig = new TestRig();
            var job = Job(rig);
            Markers(job, "OPENED", "BEGIN_REQUESTED");
            string text = kind switch
            {
                "empty" => "",
                "header" => Record(job, "Synthetic output window header"),
                "old" => Record(job, "Arranging Layout ...", -1),
                "bad-date" => "[2026/99/99-25:61:61]--Arranging Layout ...\n",
                "missing-prefix" => "Arranging Layout ...\n",
                "bad-separator" => Record(job, "Arranging Layout ...").Replace("]--", "] - "),
                "near-message" => Record(job, "Handling arbitrary data"),
                _ => Record(job, "Processing Circuits ... completed")
            };
            Output(job, text);
            Check.Equal(RunStage.Starting, (await Inspect(Runner(() => "Ready"), job)).Stage, $"Reject insufficient native evidence: {kind}");
            Check.That(!job.Run!.StartedObserved, "Rejected native records cannot establish start");
            NoSave(job);
        }
    }

    private static async Task CurrentRunMarkers()
    {
        foreach (string kind in new[] { "no-open", "no-begin", "foreign-open", "foreign-begin", "foreign-both" })
        {
            using var rig = new TestRig();
            var job = Job(rig);
            string current = job.Run!.Id.ToString("N"), foreign = Guid.NewGuid().ToString("N");
            string events = kind switch
            {
                "no-open" => $"{current} BEGIN_REQUESTED\n",
                "no-begin" => $"{current} OPENED\n",
                "foreign-open" => $"{foreign} OPENED\n{current} BEGIN_REQUESTED\n",
                "foreign-begin" => $"{current} OPENED\n{foreign} BEGIN_REQUESTED\n",
                _ => $"{foreign} OPENED\n{foreign} BEGIN_REQUESTED\n"
            };
            File.WriteAllText(Path.Combine(job.Run.DirectoryPath, "events.log"), events);
            Output(job, Record(job, "Processing Layout ..."));
            Check.Equal(RunStage.Starting, (await Inspect(Runner(() => "Ready"), job)).Stage, $"Require same-run OPENED and BEGIN_REQUESTED: {kind}");
            Check.That(!job.Run.StartedObserved, "Foreign or missing markers must not adopt native evidence");
            NoSave(job);
        }
    }

    private static async Task BeginReturnedOnly()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        Markers(job, "OPENED", "BEGIN_REQUESTED", "BEGIN_RETURNED");
        Check.Equal(RunStage.Starting, (await Inspect(Runner(() => "Simulation Done."), job)).Stage,
            "A returned begin command and old Done text do not prove that simulation started");
        Check.That(!job.Run!.StartedObserved, "BEGIN_RETURNED must not establish start");
        NoSave(job);
    }

    private static async Task DoneFirst()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        Markers(job, "OPENED", "BEGIN_REQUESTED");
        Output(job, Record(job, "Arranging Layout ..."));
        string status = "Ready\nSimulation Done.";
        var runner = Runner(() => status);
        for (int index = 0; index < 2; index++)
        {
            Check.Equal(RunStage.CompletionUnconfirmed, (await Inspect(runner, job)).Stage, "Repeated stale Done must hold for confirmation after native start");
            NoSave(job);
        }
        status = "Ready";
        Check.Equal(RunStage.Running, (await Inspect(runner, job)).Stage, "Readable idle GUI may establish a transition after start");
        NoSave(job);
        status = "Simulation Done.";
        Check.Equal(RunStage.ReadyToSave, (await Inspect(runner, job)).Stage, "Only a later exact Done may authorize completion");
        Check.That(!File.Exists(Path.Combine(job.Run!.DirectoryPath, "control.txt")), "Recognizing completion must not itself issue save");
    }

    private static async Task NonCompletedAfterStart()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        Markers(job, "OPENED", "BEGIN_REQUESTED");
        string status = "Ready";
        var runner = Runner(() => status);
        Check.Equal(RunStage.Starting, (await Inspect(runner, job)).Stage, "An idle GUI before any start must not latch freshness");
        Output(job, Record(job, "Processing Circuits ...", 1));
        status = "Simulation Done.";
        Check.Equal(RunStage.CompletionUnconfirmed, (await Inspect(runner, job)).Stage, "An earlier idle GUI cannot make first Done fresh");
        NoSave(job);
        status = "Processing synthetic layout";
        Check.Equal(RunStage.Running, (await Inspect(runner, job)).Stage, "Readable non-Simulation text after start counts as noncompleted UI");
        status = "Simulation Done.";
        Check.Equal(RunStage.ReadyToSave, (await Inspect(runner, job)).Stage, "A subsequent exact Done completes the transition");
    }

    private static async Task UnreadableGui()
    {
        foreach (string? initial in new string?[] { null, "", " \r\n\t" })
        {
            using var rig = new TestRig();
            var job = Job(rig);
            Markers(job, "OPENED", "BEGIN_REQUESTED");
            Output(job, Record(job, "Processing Layout ..."));
            string? status = initial;
            var runner = Runner(() => status);
            Check.Equal(RunStage.Running, (await Inspect(runner, job)).Stage, "Native start remains running while GUI is unreadable or empty");
            status = "Simulation Done.";
            Check.Equal(RunStage.CompletionUnconfirmed, (await Inspect(runner, job)).Stage, "An unreadable or empty GUI cannot make later Done fresh");
            NoSave(job);
        }
    }

    private static async Task RestartPersistence()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        Markers(job, "OPENED", "BEGIN_REQUESTED");
        Output(job, Record(job, "Arranging Circuits ..."));
        Check.Equal(RunStage.Running, (await Inspect(Runner(() => "Ready"), job)).Stage, "Native start and readable UI must be captured together");
        using (var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(job.Run!.DirectoryPath, "freshness.json"))))
            Check.That(saved.RootElement.GetProperty("Started").GetBoolean() && saved.RootElement.GetProperty("SawNonCompletedUi").GetBoolean(),
                "Both start and UI transition must survive scheduler exit");
        job.Run = JsonSerializer.Deserialize<RunRecord>(JsonSerializer.Serialize(job.Run))!;
        Output(job, "");
        Check.Equal(RunStage.ReadyToSave, (await Inspect(Runner(() => "Simulation Done."), job)).Stage,
            "A new runner must restore persisted freshness even when the start record has left the log");
        Check.That(!File.Exists(Path.Combine(job.Run.DirectoryPath, "control.txt")), "Restoring completion must not issue save");
    }

    private static async Task OtherWorkflowCompletion()
    {
        foreach (bool preprocessing in new[] { false, true })
        {
            using var rig = new TestRig();
            var job = Job(rig);
            Markers(job, "OPENED", "BEGIN_REQUESTED");
            Output(job, (preprocessing ? Record(job, "Processing Circuits ...") : "") + Record(job, "AFS Finished", 1));
            Check.Equal(preprocessing ? RunStage.Running : RunStage.Starting,
                (await Inspect(Runner(() => "AFS Finished"), job)).Stage,
                "Unknown workflow completion text cannot substitute for a supported completion rule or exact GUI Done");
            NoSave(job);
        }
    }

    private static async Task CaseInsensitiveRunning()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        Markers(job, "OPENED", "BEGIN_REQUESTED");
        string status = "sImUlAtIoN rUnNiNg";
        var runner = Runner(() => status);
        Check.Equal(RunStage.Running, (await Inspect(runner, job)).Stage, "Simulation running must be recognized case-insensitively");
        Check.That(job.Run!.StartedObserved, "Mixed-case running must persist start evidence");
        NoSave(job);
        status = "Simulation Done.";
        Check.Equal(RunStage.ReadyToSave, (await Inspect(runner, job)).Stage, "Fresh mixed-case running allows later exact Done");
    }

    private static async Task CaseVariantDone()
    {
        foreach (bool preprocessing in new[] { false, true })
        foreach (string oldDone in new[] { "simulation done.", "SIMULATION DONE.", "sImUlAtIoN DoNe." })
        {
            using var rig = new TestRig();
            var job = Job(rig);
            Markers(job, "OPENED", "BEGIN_REQUESTED");
            if (preprocessing) Output(job, Record(job, "Processing Circuits ..."));
            string status = oldDone;
            var runner = Runner(() => status);
            await Inspect(runner, job);
            status = "Simulation Done.";
            Check.Equal(preprocessing ? RunStage.CompletionUnconfirmed : RunStage.Starting, (await Inspect(runner, job)).Stage,
                "Changing only the case of Done must not authorize completion");
            NoSave(job);
            status = "Simulation running";
            await Inspect(runner, job);
            status = "Simulation Done.";
            Check.Equal(RunStage.ReadyToSave, (await Inspect(runner, job)).Stage, "An actual running transition still allows completion");
        }
    }

    private static async Task BlockedUi()
    {
        foreach (string blocked in new[] { "Simulation running", "Simulation is suspended.", "Insufficient memory", "Waiting for license" })
        {
            using var rig = new TestRig();
            var job = Job(rig);
            Markers(job, "OPENED", "BEGIN_REQUESTED");
            Output(job, Record(job, "Arranging Layout ..."));
            string status = "Ready";
            var runner = Runner(() => status);
            await Inspect(runner, job);
            status = $"Simulation Done.\n{blocked}";
            Check.Equal(RunStage.Running, (await Inspect(runner, job)).Stage, $"Active or blocked status must override Done: {blocked}");
            NoSave(job);
        }
    }

    private static async Task SaveAndExit()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        Markers(job, "OPENED", "BEGIN_REQUESTED");
        Output(job, Record(job, "Processing Circuits ..."));
        string status = "Ready";
        bool alive = true;
        var runner = Runner(() => status, _ => (alive, true, null));
        await Inspect(runner, job);
        status = "Simulation Done.";
        Check.Equal(RunStage.ReadyToSave, (await Inspect(runner, job)).Stage, "Fresh completion can request saving");
        status = "Simulation running";
        bool refused = false;
        try { await runner.RequestSaveAndExitAsync(job, CancellationToken.None); }
        catch (InvalidOperationException) { refused = true; }
        Check.That(refused, "New activity immediately before save must invalidate completion");
        NoSave(job);
        status = "Simulation Done.";
        await Inspect(runner, job);
        await runner.RequestSaveAndExitAsync(job, CancellationToken.None);
        Check.Equal($"SAVE_EXIT {job.Run!.Id:N}", File.ReadAllText(Path.Combine(job.Run.DirectoryPath, "control.txt")),
            "Only a fresh rechecked completion delivers this run's save token");
        Check.Equal(RunStage.Saving, (await Inspect(runner, job)).Stage, "Save request alone is not completion");
        Markers(job, "OPENED", "BEGIN_REQUESTED", "SAVED");
        Check.Equal(RunStage.Saving, (await Inspect(runner, job)).Stage, "Saved marker alone cannot bypass a live process");
        alive = false;
        Check.Equal(RunStage.Succeeded, (await Inspect(runner, job)).Stage, "Save and confirmed owned exit finish the run");
    }

    private static async Task IdentityGuards()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        Markers(job, "OPENED", "BEGIN_REQUESTED");
        Output(job, Record(job, "Processing Layout ..."));
        int statusReads = 0;
        var unreadable = Runner(() => { statusReads++; return "Ready"; }, _ => (false, false, "Synthetic identity read error"));
        Check.Equal(RunStage.ObservationPending, (await Inspect(unreadable, job)).Stage, "Unreadable root must defer observation");
        Check.Equal(0, statusReads, "Never read GUI through an unverified root identity");
        Check.That(!job.Run!.StartedObserved, "Unreadable root must not adopt log evidence");
        NoSave(job);

        string status = "Ready";
        bool childReadable = true;
        var child = new ProcessIdentity(int.MaxValue - 1, job.Run.CreatedAt, @"C:\SyntheticOnly\solver.exe");
        job.Run.Children.Add(child);
        var runner = Runner(() => status, identity => identity == child && !childReadable
            ? (false, false, "Synthetic child read error") : (true, true, null));
        await Inspect(runner, job);
        status = "Simulation Done.";
        childReadable = false;
        Check.Equal(RunStage.ObservationPending, (await Inspect(runner, job)).Stage, "Unreadable child must hold fresh Done");
        NoSave(job);
        childReadable = true;
        Check.Equal(RunStage.Running, (await Inspect(runner, job, complete: false)).Stage, "Incomplete process inventory cannot authorize completion");
        NoSave(job);
        Check.Equal(RunStage.ReadyToSave, (await Inspect(runner, job)).Stage, "Verified identities and inventory release fresh completion");
    }
}
