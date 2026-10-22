using System.Reflection;
using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.Tests;

internal static class PowerDcWindowsTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);

    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return Test("Executable validation enforces the selected simulation tool", SelectedExecutable);
        yield return Test("Unknown tools cannot validate configuration or generate launch scripts", UnsupportedTool);
        yield return Test("PowerDC result export must succeed before SAVED and exit", ResultExportGuard);
        yield return Test("Tool scripts preserve saved workflows and PowerSI has no DC export", PreserveSavedWorkflow);
        yield return Test("Mixed PowerSI and PowerDC roots retain mesh and solver descendants", MixedProcessTrees);
        yield return Test("Active PowerDC blocks the shared launch gate", ActivePowerDcBlocks);
        yield return Test("PowerDC BbsCon children never inherit the PowerSI overlap exemption", PowerDcBbsBlocks);
        yield return ("Native PowerDC progress requires current-run open and begin markers", NativeProgressMarkers);
        yield return ("A stale PowerDC Done pane cannot authorize saving", StaleDone);
        yield return ("Completion rules are isolated by the selected simulation tool", SelectedToolRules);
        yield return ("Fresh PowerDC completion rechecks status and waits for SAVED plus exit", FreshCompletion);
    }

    private static (string, Func<Task>) Test(string name, Action action) =>
        (name, () => { action(); return Task.CompletedTask; });

    private static void SelectedExecutable()
    {
        using var rig = new TestRig();
        string dc = rig.CreateFile("PowerDC.exe");
        string si = rig.CreateFile("PowerSI.exe");
        Check.Equal(dc, PowerSiDiscovery.ResolveExecutablePath(dc, SimulationTool.PowerDC), "Accept the selected DC text fixture without executing it");
        Check.Equal(si, PowerSiDiscovery.ResolveExecutablePath(si), "Preserve the default SI selection");
        Check.Throws<ArgumentException>(() => PowerSiDiscovery.ResolveExecutablePath(si, SimulationTool.PowerDC), "DC must reject the SI executable");
        Check.Throws<ArgumentException>(() => PowerSiDiscovery.ResolveExecutablePath(dc), "SI must reject the DC executable");
        var settings = new AppSettings { PowerDcPath = dc, PowerSiPath = "invalid unused SI path" };
        new PowerSiRunner().ValidateConfiguration(settings, SimulationTool.PowerDC);
        Check.Equal(dc, settings.PowerDcPath, "DC-only validation uses only its selected executable");
    }

    private static void UnsupportedTool()
    {
        using var rig = new TestRig();
        var unsupported = (SimulationTool)123;
        Check.Throws<ArgumentException>(() => PowerSiDiscovery.ResolveExecutablePath(rig.CreateFile("PowerDC.exe"), unsupported), "Unknown tool cannot resolve an executable");
        Check.Throws<ArgumentException>(() => new PowerSiRunner().ValidateConfiguration(new(), unsupported), "Unknown tool cannot validate a runner");
        Check.Throws<ArgumentException>(() => PowerSiRunner.CreateScript(Guid.NewGuid(), "synthetic.spd", "synthetic-run", unsupported), "Unknown tool cannot generate a launch script");
    }

    private static void ResultExportGuard()
    {
        string script = PowerSiRunner.CreateScript(Guid.NewGuid(), "synthetic.spd", "synthetic-run", SimulationTool.PowerDC);
        int result = script.IndexOf("set result [file join $::scheduler_dir powerdc-results.xml]", StringComparison.Ordinal);
        int export = script.IndexOf("sigrity::save pdcResults $result {!}", StringComparison.Ordinal);
        int guard = script.IndexOf("if {![file exists $result] || [file size $result] == 0}", StringComparison.Ordinal);
        int saved = script.IndexOf("scheduler_mark SAVED", StringComparison.Ordinal);
        int exit = script.IndexOf("sigrity::exit -noSave", StringComparison.Ordinal);
        Check.That(result >= 0 && result < export && export < guard && guard < saved && saved < exit,
            "Result export and nonempty-file validation must precede save confirmation and exit");
        string failure = script[guard..saved];
        Check.That(failure.Contains("scheduler_mark SAVE_FAILED") && failure.Contains("return"), "Failed or empty export preserves the process without SAVED");
    }

    private static void PreserveSavedWorkflow()
    {
        var id = Guid.NewGuid();
        string defaultSi = PowerSiRunner.CreateScript(id, "synthetic.spd", "synthetic-run");
        Check.Equal(defaultSi, PowerSiRunner.CreateScript(id, "synthetic.spd", "synthetic-run", SimulationTool.PowerSI), "Explicit SI retains the original default script");
        Check.That(!defaultSi.Contains("pdcResults") && !defaultSi.Contains("powerdc-results.xml"), "SI must not export PowerDC results");
        foreach (var tool in new[] { SimulationTool.PowerSI, SimulationTool.PowerDC })
        {
            string script = PowerSiRunner.CreateScript(id, "synthetic.spd", "synthetic-run", tool);
            Check.That(script.Contains("sigrity::open document $::scheduler_spd") && script.Contains("sigrity::begin simulation"), "Run the saved document using the existing simulation command");
            Check.That(!script.Contains("update workflow", StringComparison.OrdinalIgnoreCase) &&
                !script.Contains("open workflow", StringComparison.OrdinalIgnoreCase) &&
                !script.Contains("IgnoreMode", StringComparison.OrdinalIgnoreCase) && !script.Contains("pdcSimMode", StringComparison.OrdinalIgnoreCase),
                "Program selection must not rewrite the user's saved subworkflow");
        }
    }

    private static ProcessSample Sample(int pid, int parent, int started, string name, ProcessSignal signal = ProcessSignal.None, double cpu = 0) =>
        new(new ProcessIdentity(pid, Epoch.AddSeconds(started), Path.Combine(@"C:\SyntheticOnly", name)), parent, cpu, 1024, 0, Signal: signal);

    private static void MixedProcessTrees()
    {
        var samples = new[] { Sample(204, 202, 4, "PdcSolver.exe"), Sample(100, 1, 0, "PowerSI.exe"),
            Sample(201, 200, 1, "PdcMesh.exe"), Sample(200, 1, 0, "PowerDC.exe"),
            Sample(202, 201, 2, "PdcSolver.exe"), Sample(101, 100, 1, "solver.exe"), Sample(999, 1, 0, "PdcSolver.exe") };
        var rows = samples.ToDictionary(sample => sample.Identity.Pid, sample => (sample.ParentPid, Path.GetFileName(sample.Identity.ImagePath)));
        var snapshot = (ProcessSnapshot)typeof(WindowsProcessProbe).GetMethod("SelectProcesses", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new WindowsProcessProbe(), [rows, (Func<int, ProcessSample?>)(pid =>
            {
                Check.That(pid != 999, "An unrelated solver name must not seed a process tree");
                return samples.Single(sample => sample.Identity.Pid == pid);
            }), CancellationToken.None])!;
        Check.That(snapshot.Complete && snapshot.Processes.Select(sample => sample.Identity.Pid).SequenceEqual(new[] { 100, 101, 200, 201, 202, 204 }),
            "Monitor both GUI roots and every validated descendant generation");
    }

    private static void ActivePowerDcBlocks()
    {
        var gate = new IdleGate();
        var settings = new AppSettings();
        Check.That(gate.Evaluate(new(Epoch, []), settings).CanStart, "The empty inventory begins eligible");
        var dc = Sample(200, 1, 0, "PowerDC.exe", ProcessSignal.Running);
        Check.That(!gate.Confirm(new(Epoch.AddSeconds(1), [dc]), settings).CanStart, "A DC process appearing before launch invalidates the final check");
        for (int seconds = 0; seconds <= 180; seconds += 60)
            Check.That(!gate.Evaluate(new(Epoch.AddSeconds(seconds), [dc]), settings).CanStart, "Running DC blocks a following SI or DC job even without resource growth");
    }

    private static void PowerDcBbsBlocks()
    {
        foreach (string tool in new[] { "PowerSI.exe", "PowerDC.exe" })
        {
            var gate = new IdleGate();
            var settings = new AppSettings { AllowBbsPostProcessing = true, IdleSeconds = 60 };
            GateDecision decision = new(false, false, "");
            for (int seconds = 0; seconds <= 180; seconds += 60)
                decision = gate.Evaluate(new(Epoch.AddSeconds(seconds),
                    [Sample(200, 1, 0, tool, ProcessSignal.Completed), Sample(201, 200, 1, "BbsCon.exe", cpu: seconds)]), settings);
            Check.Equal(tool == "PowerSI.exe", decision.CanStart, "Only validated PowerSI BbsCon ancestry can exempt a busy child");
        }
    }

    private static JobItem Job(TestRig rig, SimulationTool tool = SimulationTool.PowerDC)
    {
        string directory = Path.Combine(rig.DirectoryPath, "run");
        Directory.CreateDirectory(directory);
        return new JobItem { FilePath = rig.CreateFile("synthetic.spd"), Tool = tool, Status = JobStatus.Running,
            Run = new RunRecord { DirectoryPath = directory, CreatedAt = Epoch,
                Process = new ProcessIdentity(int.MaxValue, Epoch, @"C:\SyntheticOnly\" + tool + ".exe") } };
    }

    private static PowerSiRunner Runner(Func<string?> status, Func<bool>? alive = null) =>
        (PowerSiRunner)Activator.CreateInstance(typeof(PowerSiRunner), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [(Func<ProcessIdentity, (bool Alive, bool Readable, string? Detail)>)(_ => (alive?.Invoke() ?? true, true, null)),
                (Func<ProcessIdentity, CancellationToken, Task<string?>>)((_, _) => Task.FromResult(status()))], null)!;

    private static Task<RunObservation> Inspect(PowerSiRunner runner, JobItem job, AppSettings? settings = null) =>
        runner.InspectAsync(job, settings ?? new(), new ProcessSnapshot(Epoch, []), CancellationToken.None);

    private static void Markers(JobItem job, params string[] markers) =>
        File.WriteAllText(Path.Combine(job.Run!.DirectoryPath, "events.log"), string.Join('\n', markers.Select(marker => $"{job.Run.Id:N} {marker}")));

    private static void Output(JobItem job, string text) => File.WriteAllText(Path.Combine(job.Run!.DirectoryPath, "output.log"), text);

    private static void NoSave(JobItem job) => Check.That(!job.Run!.SaveRequested &&
        !File.Exists(Path.Combine(job.Run.DirectoryPath, "completion.txt")) && !File.Exists(Path.Combine(job.Run.DirectoryPath, "control.txt")),
        "Insufficient evidence must not authorize saving or exit");

    private static async Task NativeProgressMarkers()
    {
        foreach (var markers in new[] { Array.Empty<string>(), new[] { "OPENED" }, new[] { "BEGIN_REQUESTED" }, new[] { "OPENED", "BEGIN_REQUESTED" } })
        {
            using var rig = new TestRig();
            var job = Job(rig);
            Markers(job, markers);
            Output(job, "Arranging data for IR drop analysis ...\nGeometry processing ...\nGenerating FEM mesh ...");
            var result = await Inspect(Runner(() => null), job);
            Check.Equal(markers.Length == 2 ? RunStage.Running : RunStage.Starting, result.Stage, "Native DC output needs both current-run markers");
            NoSave(job);
        }
        using var siRig = new TestRig();
        var si = Job(siRig, SimulationTool.PowerSI);
        Markers(si, "OPENED", "BEGIN_REQUESTED");
        Output(si, "Geometry processing ...\nGenerating FEM mesh ...");
        Check.Equal(RunStage.Starting, (await Inspect(Runner(() => null), si)).Stage, "Unprefixed DC progress cannot start an SI run");
        NoSave(si);
    }

    private static async Task StaleDone()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        Markers(job, "OPENED", "BEGIN_REQUESTED");
        Output(job, "Geometry processing ...");
        var runner = Runner(() => "Simulation Done.");
        Check.Equal(RunStage.CompletionUnconfirmed, (await Inspect(runner, job)).Stage, "First-observed Done cannot prove a fresh DC completion");
        Check.Equal(RunStage.CompletionUnconfirmed, (await Inspect(runner, job)).Stage, "Repeated stale Done cannot manufacture a transition");
        NoSave(job);
    }

    private static async Task SelectedToolRules()
    {
        foreach (var tool in new[] { SimulationTool.PowerSI, SimulationTool.PowerDC })
        {
            using var rig = new TestRig();
            var job = Job(rig, tool);
            Markers(job, "OPENED", "BEGIN_REQUESTED");
            var settings = new AppSettings { CompletionRules = new[] { SimulationTool.PowerSI, SimulationTool.PowerDC }.Select(value =>
                new CompletionRule { Tool = value, Workflow = value.ToString(), SpdPattern = "Synthetic", StartedPattern = value + "_START", CompletedPattern = value + "_DONE" }).ToList() };
            var other = tool == SimulationTool.PowerSI ? SimulationTool.PowerDC : SimulationTool.PowerSI;
            Output(job, other + "_START\n" + other + "_DONE");
            var runner = Runner(() => null);
            Check.Equal(RunStage.Starting, (await Inspect(runner, job, settings)).Stage, "Another tool's log markers cannot establish start or completion");
            NoSave(job);
            Output(job, tool + "_START\n" + tool + "_DONE");
            Check.Equal(RunStage.ReadyToSave, (await Inspect(runner, job, settings)).Stage, "Identical SPD matchers across tools must not create false ambiguity");
            Check.Equal(tool.ToString(), job.Run!.Workflow, "Record only the selected tool's workflow");
        }
    }

    private static async Task FreshCompletion()
    {
        using var rig = new TestRig();
        var job = Job(rig);
        Markers(job, "OPENED", "BEGIN_REQUESTED");
        string status = "Simulation running";
        bool alive = true;
        var runner = Runner(() => status, () => alive);
        Check.Equal(RunStage.Running, (await Inspect(runner, job)).Stage, "Observe a fresh DC start");
        status = "Simulation Done.";
        Check.Equal(RunStage.ReadyToSave, (await Inspect(runner, job)).Stage, "Fresh running-to-Done transition permits save review");
        status = "Simulation running";
        bool rejected = false;
        try { await runner.RequestSaveAndExitAsync(job, CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        Check.That(rejected, "New DC activity immediately before save must preserve the process");
        NoSave(job);
        status = "Simulation Done.";
        await Inspect(runner, job);
        await runner.RequestSaveAndExitAsync(job, CancellationToken.None);
        Check.Equal($"SAVE_EXIT {job.Run!.Id:N}", File.ReadAllText(Path.Combine(job.Run.DirectoryPath, "control.txt")), "Deliver only this run's save/exit token");
        Check.Equal(RunStage.Saving, (await Inspect(runner, job)).Stage, "Save request alone cannot complete the job");
        Markers(job, "OPENED", "BEGIN_REQUESTED", "SAVED");
        Check.Equal(RunStage.Saving, (await Inspect(runner, job)).Stage, "SAVED still waits for owned process exit");
        alive = false;
        Check.Equal(RunStage.Succeeded, (await Inspect(runner, job)).Stage, "SAVED plus confirmed owned exit completes DC");
    }
}
