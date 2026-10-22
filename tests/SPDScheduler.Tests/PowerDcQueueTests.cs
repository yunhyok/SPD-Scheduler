using SPDScheduler.Core;
using System.Text.Json.Nodes;

namespace SPDScheduler.Tests;

internal static class PowerDcQueueTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return ("Legacy queues and completion rules default to PowerSI", LegacyDefaults);
        yield return ("PowerDC jobs paths and rules survive persistence", PowerDcRoundTrip);
        yield return ("Unknown simulation tools are rejected at queue and rule boundaries", InvalidTools);
        yield return ("Queued tool selection persists without changing scheduling", SelectQueuedTool);
        yield return ("Tool selection rejects every nonqueued status", RejectNonqueuedToolChange);
        yield return ("Tool selection rolls back when persistence fails", FailedSelectionSave);
        yield return ("Tool selection is serialized against launch preflight", SelectionDuringPreflight);
        yield return ("Duplicate queue detection uses both SPD path and tool", DuplicateTools);
        yield return ("Active duplicate tool selection is rejected", ActiveDuplicate);
        yield return ("Start validates every distinct queued tool", ValidateSelectedTools);
        yield return ("Start with no queued work retains PowerSI validation", ValidateEmptyQueue);
        yield return ("Start with only an active PowerDC job validates PowerDC", ValidateActiveTool);
        yield return ("Prelaunch validates the current tool before creating run intent", ValidateBeforeLaunch);
        yield return ("Mixed tools preserve schedule priority and FIFO", MixedQueueOrder);
        yield return ("PowerDC owned completion honors Pause before the next PowerSI job", PausePowerDc);
    }

    private static Task LegacyDefaults()
    {
        using var rig = new TestRig();
        var job = new JobItem { FilePath = rig.CreateFile("legacy.spd") };
        rig.Store.Save(new QueueDocument { Jobs = [job], Settings = new AppSettings { CompletionRules = [Rule()] } });
        var json = JsonNode.Parse(File.ReadAllText(rig.Store.QueuePath))!;
        json["SchemaVersion"] = 1;
        json["Jobs"]![0]!.AsObject().Remove("Tool");
        json["Settings"]!["CompletionRules"]![0]!.AsObject().Remove("Tool");
        json["Settings"]!.AsObject().Remove("PowerDcPath");
        File.WriteAllText(rig.Store.QueuePath, json.ToJsonString());
        var loaded = rig.Store.Load();
        Check.Equal(SimulationTool.PowerSI, loaded.Jobs.Single().Tool, "Legacy job keeps its original simulation meaning");
        Check.Equal(SimulationTool.PowerSI, loaded.Settings.CompletionRules.Single().Tool, "Legacy rule stays PowerSI only");
        Check.Equal("", loaded.Settings.PowerDcPath, "Legacy settings need no PowerDC path");
        Check.Equal(2, loaded.SchemaVersion, "Migrate legacy documents to a tool-aware schema");
        return Task.CompletedTask;
    }

    private static Task PowerDcRoundTrip()
    {
        using var rig = new TestRig();
        var rule = Rule();
        rule.Tool = SimulationTool.PowerDC;
        rig.Store.Save(new QueueDocument
        {
            Jobs = [new JobItem { FilePath = rig.CreateFile("dc.spd"), Tool = SimulationTool.PowerDC }],
            Settings = new AppSettings { PowerDcPath = @"C:\SyntheticOnly\PowerDC.exe", CompletionRules = [rule] }
        });
        var loaded = rig.Store.Load();
        Check.Equal(SimulationTool.PowerDC, loaded.Jobs.Single().Tool, "Persist selected tool");
        Check.Equal(SimulationTool.PowerDC, loaded.Settings.CompletionRules.Single().Tool, "Persist rule tool");
        Check.Equal(@"C:\SyntheticOnly\PowerDC.exe", loaded.Settings.PowerDcPath, "Persist configured path");
        Check.Equal(2, JsonNode.Parse(File.ReadAllText(rig.Store.QueuePath))!["SchemaVersion"]!.GetValue<int>(),
            "Old schedulers must reject the queue instead of silently dropping tool selection");
        return Task.CompletedTask;
    }

    private static async Task ValidateActiveTool()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("active-dc.spd");
        rig.Engine.SetTool(job.Id, SimulationTool.PowerDC);
        job.Status = JobStatus.Running;
        rig.Engine.Start();
        Check.Equal(SimulationTool.PowerDC, rig.Runner.ValidatedTools.Single(), "No unused PowerSI installation is required");
    }

    private static Task InvalidTools()
    {
        using var rig = new TestRig();
        rig.Store.Save(new QueueDocument { Jobs = [new JobItem { FilePath = rig.CreateFile("invalid.spd") }] });
        var original = File.ReadAllText(rig.Store.QueuePath);
        foreach (var invalid in new[] { "\"Unknown\"", "\"42\"", "42" })
        {
            var json = original.Replace("\"Tool\": \"PowerSI\"", "\"Tool\": " + invalid);
            File.WriteAllText(rig.Store.QueuePath, json);
            Check.Throws<InvalidDataException>(() => rig.Store.Load(), "Unknown persisted tools must not be launched as PowerSI");
            Check.Equal(json, File.ReadAllText(rig.Store.QueuePath), "Rejecting invalid state preserves the source");
        }
        var rule = Rule();
        rule.Tool = (SimulationTool)42;
        Check.Throws<ArgumentException>(() => SettingsValidator.Validate(new AppSettings { CompletionRules = [rule] }), "Unknown completion rule tools must be rejected");
        return Task.CompletedTask;
    }

    private static async Task SelectQueuedTool()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("select.spd");
        Check.Equal(SimulationTool.PowerSI, job.Tool, "Dropped SPD defaults to PowerSI");
        var scheduledAt = rig.Clock.Now.AddHours(1);
        rig.Engine.SetSchedule(job.Id, scheduledAt);
        var order = job.Order;
        rig.Engine.SetTool(job.Id, SimulationTool.PowerDC);
        Check.Equal(SimulationTool.PowerDC, rig.Store.Load().Jobs.Single().Tool, "Save selection immediately");
        Check.Equal(scheduledAt, job.ScheduledAt, "Selection keeps schedule");
        Check.Equal(order, job.Order, "Selection keeps insertion order");
        Check.That(job.Run is null, "Selection creates no execution intent");
        Check.Throws<ArgumentException>(() => rig.Engine.SetTool(job.Id, (SimulationTool)42), "Reject undefined selection");
        Check.Equal(SimulationTool.PowerDC, job.Tool, "Rejected selection retains previous tool");
    }

    private static async Task RejectNonqueuedToolChange()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("owned.spd");
        foreach (var status in Enum.GetValues<JobStatus>().Where(status => status != JobStatus.Queued))
        {
            job.Status = status;
            Check.Throws<InvalidOperationException>(() => rig.Engine.SetTool(job.Id, SimulationTool.PowerDC), "Only queued jobs can change tool: " + status);
            Check.Equal(SimulationTool.PowerSI, job.Tool, "Do not mutate nonqueued job tool");
        }
    }

    private static async Task FailedSelectionSave()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("save-failure.spd");
        rig.Engine.Start();
        using (File.Open(rig.Store.QueuePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Check.Throws<IOException>(() => rig.Engine.SetTool(job.Id, SimulationTool.PowerDC), "Persistence failure must be reported");
        Check.Equal(SimulationTool.PowerSI, job.Tool, "Rollback in-memory selection after failed save");
        Check.Equal(SimulationTool.PowerSI, rig.Store.Load().Jobs.Single().Tool, "Keep durable previous tool");
        Check.That(!rig.Engine.IsArmed, "Persistence failure pauses automatic launches");
        rig.Engine.SetTool(job.Id, SimulationTool.PowerDC);
        Check.Equal(SimulationTool.PowerDC, job.Tool, "Failed save releases selection lock");
    }

    private static async Task SelectionDuringPreflight()
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
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Throws<InvalidOperationException>(() => rig.Engine.SetTool(job.Id, SimulationTool.PowerDC), "An in-flight tick owns job selection");
            Check.Equal(SimulationTool.PowerSI, job.Tool, "Failed concurrent selection keeps validated tool");
        }
        finally { release.TrySetResult(); await tick; }
        Check.Equal(SimulationTool.PowerSI, rig.Runner.LaunchedTools.Single(), "Launch preserves validated tool");
    }

    private static async Task DuplicateTools()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var dc = rig.Add("shared.spd");
        Check.Equal(1, rig.Engine.AddFiles([dc.FilePath]).Count, "Duplicate PowerSI is rejected");
        rig.Engine.SetTool(dc.Id, SimulationTool.PowerDC);
        Check.Equal(0, rig.Engine.AddFiles([dc.FilePath]).Count, "Same SPD can enter independently as PowerSI");
        var si = rig.Engine.Jobs.Single(job => job.Tool == SimulationTool.PowerSI);
        Check.Throws<InvalidOperationException>(() => rig.Engine.SetTool(si.Id, SimulationTool.PowerDC), "Tool change cannot duplicate queued path and tool");
        Check.Equal(SimulationTool.PowerSI, si.Tool, "Duplicate rejection retains original tool");
        Check.Equal(1, rig.Engine.AddFiles([dc.FilePath.ToUpperInvariant()]).Count, "Duplicate path comparison is case insensitive");
        dc.Status = JobStatus.Completed;
        rig.Engine.SetTool(si.Id, SimulationTool.PowerDC);
        Check.Equal(SimulationTool.PowerDC, si.Tool, "Historical completed job does not block a new queued run");
    }

    private static async Task ActiveDuplicate()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var active = rig.Add("active-shared.spd");
        rig.Engine.SetTool(active.Id, SimulationTool.PowerDC);
        rig.Engine.Start();
        await rig.TickAsync();
        Check.Equal(0, rig.Engine.AddFiles([active.FilePath]).Count, "Active PowerDC permits a queued PowerSI of the same SPD");
        var queued = rig.Engine.Jobs.Single(job => job.Status == JobStatus.Queued);
        Check.Throws<InvalidOperationException>(() => rig.Engine.SetTool(queued.Id, SimulationTool.PowerDC), "Do not duplicate an active path and tool");
        Check.Throws<InvalidOperationException>(() => rig.Engine.SetTool(active.Id, SimulationTool.PowerSI), "Do not alter owned run tool");
    }

    private static async Task ValidateSelectedTools()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var dc = rig.Add("dc-only.spd");
        rig.Engine.SetTool(dc.Id, SimulationTool.PowerDC);
        rig.Runner.BeforeValidateToolConfiguration = (_, tool) =>
        {
            if (tool == SimulationTool.PowerSI) throw new InvalidOperationException("Synthetic missing PowerSI path");
        };
        rig.Engine.Start();
        Check.That(rig.Runner.ValidatedTools.SequenceEqual([SimulationTool.PowerDC]), "PowerDC-only queue does not require PowerSI configuration");
        rig.Add("si-missing.spd");
        rig.Runner.ValidatedTools.Clear();
        Check.Throws<InvalidOperationException>(rig.Engine.Start, "Mixed queue validates PowerSI as well");
        Check.That(!rig.Engine.IsArmed, "Configuration failure disarms queue");
        Check.That(rig.Runner.ValidatedTools.SequenceEqual([SimulationTool.PowerDC, SimulationTool.PowerSI]), "Validate every queued tool in encounter order");
        rig.Runner.BeforeValidateToolConfiguration = null;
        var secondDc = rig.Add("dc-second.spd");
        rig.Engine.SetTool(secondDc.Id, SimulationTool.PowerDC);
        rig.Runner.ValidatedTools.Clear();
        rig.Engine.Start();
        Check.That(rig.Runner.ValidatedTools.SequenceEqual([SimulationTool.PowerDC, SimulationTool.PowerSI]), "Validate each distinct tool once");
    }

    private static async Task ValidateEmptyQueue()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        rig.Engine.Start();
        Check.That(rig.Runner.ValidatedTools.SequenceEqual([SimulationTool.PowerSI]), "Empty queue preserves existing PowerSI validation");
    }

    private static async Task ValidateBeforeLaunch()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("changed-selection.spd");
        rig.Engine.Start();
        rig.Engine.SetTool(job.Id, SimulationTool.PowerDC);
        rig.Runner.BeforeValidateToolConfiguration = (_, tool) =>
        {
            if (tool == SimulationTool.PowerDC) throw new InvalidOperationException("Synthetic missing PowerDC path");
        };
        await rig.TickAsync();
        Check.That(rig.Runner.ValidatedTools.SequenceEqual([SimulationTool.PowerSI, SimulationTool.PowerDC]), "Prelaunch checks current selection");
        Check.Equal(0, rig.Runner.Launched.Count, "Invalid current tool does not launch");
        Check.That(job.Run is null && job.Status == JobStatus.Queued, "Configuration failure consumes no queued work");
        Check.That(!rig.Engine.IsArmed, "Prelaunch validation failure pauses queue");
    }

    private static async Task MixedQueueOrder()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var si = rig.Add("fifo-si.spd");
        var scheduledDc = rig.Add("scheduled-dc.spd");
        rig.Engine.SetTool(scheduledDc.Id, SimulationTool.PowerDC);
        rig.Engine.SetSchedule(scheduledDc.Id, rig.Clock.Now);
        var fifoDc = rig.Add("fifo-dc.spd");
        rig.Engine.SetTool(fifoDc.Id, SimulationTool.PowerDC);
        rig.Engine.Start();
        await rig.TickAsync();
        Check.Equal(scheduledDc.Id, rig.Runner.Launched.Single(), "Due schedule precedes FIFO across tools");
        await CompleteOwned(rig);
        await rig.TickAsync();
        Check.Equal(si.Id, rig.Runner.Launched.Last(), "PowerSI keeps earlier FIFO position");
        await CompleteOwned(rig);
        await rig.TickAsync();
        Check.Equal(fifoDc.Id, rig.Runner.Launched.Last(), "PowerDC follows FIFO");
        Check.That(rig.Runner.LaunchedTools.SequenceEqual([SimulationTool.PowerDC, SimulationTool.PowerSI, SimulationTool.PowerDC]), "Mixed queue launches selected tools serially");
    }

    private static async Task PausePowerDc()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var dc = rig.Add("paused-dc.spd");
        rig.Engine.SetTool(dc.Id, SimulationTool.PowerDC);
        rig.Add("waiting-si.spd");
        rig.Engine.Start();
        await rig.TickAsync();
        rig.Engine.Pause();
        rig.Runner.Observation = new(RunStage.ReadyToSave, "Synthetic PowerDC completed", true);
        await rig.TickAsync();
        Check.Equal(dc.Id, rig.Runner.SaveRequests.Single(), "Pause permits saving owned PowerDC work");
        await CompleteOwned(rig);
        await rig.TickAsync();
        Check.Equal(JobStatus.Completed, dc.Status, "Owned PowerDC work completes while paused");
        Check.Equal(1, rig.Runner.Launched.Count, "Pause blocks next PowerSI job");
    }

    private static async Task CompleteOwned(TestRig rig)
    {
        rig.Probe.Processes.Clear();
        rig.Runner.Observation = new(RunStage.Succeeded, "Synthetic saved and closed", false);
        await rig.TickAsync();
        rig.Runner.Observation = new(RunStage.Running, "Synthetic running", true);
    }

    private static CompletionRule Rule() => new()
    {
        Workflow = "Synthetic workflow", SpdPattern = "Synthetic", StartedPattern = "started", CompletedPattern = "completed"
    };
}
