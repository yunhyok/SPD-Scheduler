using System.Text.Json.Nodes;
using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.Tests;

internal static class ResourceThreadTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return ("Legacy queues migrate with 64 simulation threads", MigrateLegacyThreads);
        yield return ("Queued thread choices persist and survive retry", SelectAndRetry);
        yield return ("Invalid thread choices are rejected at queue and launch boundaries", RejectInvalidThreads);
        yield return ("Thread choices are blocked for nonqueued jobs and during launch preflight", RejectUnsafeChanges);
        yield return ("Thread choices roll back when persistence fails", RollBackSelection);
        yield return ("Tcl configures local threads before simulation and blocks begin if rejected", ConfigureBeforeBegin);
    }

    private static async Task MigrateLegacyThreads()
    {
        foreach (int version in new[] { 1, 2 })
        {
            using var rig = new TestRig();
            var job = new JobItem { FilePath = rig.CreateFile("legacy.spd"),
                Tool = version == 1 ? SimulationTool.PowerSI : SimulationTool.PowerDC };
            rig.Store.Save(new QueueDocument { Jobs = [job] });
            var json = JsonNode.Parse(File.ReadAllText(rig.Store.QueuePath))!;
            json["SchemaVersion"] = version;
            json["Jobs"]![0]!.AsObject().Remove("ThreadCount");
            if (version == 1) json["Jobs"]![0]!.AsObject().Remove("Tool");
            File.WriteAllText(rig.Store.QueuePath, json.ToJsonString());

            await rig.InitializeAsync();
            var migrated = rig.Engine.Jobs.Single();
            Check.Equal(64, migrated.ThreadCount, "Jobs without a persisted choice use 64 threads");
            Check.Equal(job.Tool, migrated.Tool, "Preserve the legacy tool selection");
            var saved = JsonNode.Parse(File.ReadAllText(rig.Store.QueuePath))!;
            Check.Equal(3, saved["SchemaVersion"]!.GetValue<int>(), "Older schedulers must reject queues with thread settings");
            Check.Equal(64, saved["Jobs"]![0]!["ThreadCount"]!.GetValue<int>(), "Persist the migrated default");
        }
    }

    private static async Task SelectAndRetry()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("thread-choice.spd");
        var other = rig.Add("other.spd");
        Check.Equal(64, job.ThreadCount, "New jobs default to 64 threads");
        rig.Engine.SetTool(job.Id, SimulationTool.PowerDC);
        rig.Engine.SetSchedule(job.Id, rig.Clock.Now);
        rig.Engine.SetThreadCount(job.Id, 24);
        Check.Equal(24, rig.Store.Load().Jobs.Single(item => item.Id == job.Id).ThreadCount, "Save the new count immediately");
        Check.Equal(64, other.ThreadCount, "Changing one SPD must not affect the others");
        Check.Equal(rig.Clock.Now, job.ScheduledAt, "Changing threads preserves the schedule");
        Check.Equal(SimulationTool.PowerDC, job.Tool, "Changing threads preserves the selected tool");

        int launches = 0;
        rig.Runner.BeforeLaunch = launched =>
        {
            Check.Equal(job.Id, launched.Id, "The due job launches before FIFO work");
            Check.Equal(24, launched.ThreadCount, "Every launch receives the persisted count");
            if (++launches == 1) throw new IOException("Synthetic pre-spawn failure");
        };
        rig.Engine.Start();
        await rig.TickAsync();
        Check.Equal(JobStatus.Failed, job.Status, "Set up a retry from a confirmed failure");
        rig.Engine.Retry(job.Id);
        Check.Equal(24, rig.Store.Load().Jobs.Single(item => item.Id == job.Id).ThreadCount, "Retry preserves the saved count");
        await rig.TickAsync();
        Check.Equal(2, launches, "The retried job launches again");
        Check.Equal(job.Id, rig.Runner.Launched.Single(), "The second launch succeeds");
    }

    private static async Task RejectInvalidThreads()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("invalid-threads.spd");
        foreach (int invalid in new[] { -1, 0, 8, 17, 40, 65 })
        {
            Check.Throws<ArgumentException>(() => rig.Engine.SetThreadCount(job.Id, invalid), "Reject invalid queue edits");
            Check.Throws<ArgumentException>(() => PowerSiRunner.CreateScript(Guid.NewGuid(), "fixture.spd", "run", threadCount: invalid),
                "Reject invalid values before generating launch Tcl");
        }
        Check.Equal(64, job.ThreadCount, "No rejected selection changes the job");
        string original = File.ReadAllText(rig.Store.QueuePath);
        job.ThreadCount = 63;
        Check.Throws<ArgumentException>(() => rig.Store.Save(rig.Engine.Document), "Do not write invalid thread values");
        Check.Equal(original, File.ReadAllText(rig.Store.QueuePath), "Reject invalid writes before replacing the queue");
        foreach (string invalid in new[] { "63", "\"64\"", "null" })
        {
            string corrupt = original.Replace("\"ThreadCount\": 64", "\"ThreadCount\": " + invalid);
            File.WriteAllText(rig.Store.QueuePath, corrupt);
            Check.Throws<InvalidDataException>(() => rig.Store.Load(), "Reject an invalid persisted value instead of silently defaulting");
            Check.Equal(corrupt, File.ReadAllText(rig.Store.QueuePath), "Preserve rejected queue data");
        }
    }

    private static async Task RejectUnsafeChanges()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("launching.spd");
        rig.Engine.SetThreadCount(job.Id, 48);
        foreach (var status in Enum.GetValues<JobStatus>().Where(status => status != JobStatus.Queued))
        {
            job.Status = status;
            Check.Throws<InvalidOperationException>(() => rig.Engine.SetThreadCount(job.Id, 16), $"Do not edit threads for {status}");
            Check.Equal(48, job.ThreadCount, "Reject edits without changing the saved setting");
        }
        job.Status = JobStatus.Queued;

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Probe.BeforeCaptureAsync = capture =>
        {
            if (capture != 2) return Task.CompletedTask;
            reached.SetResult();
            return release.Task;
        };
        rig.Runner.BeforeLaunch = launched => Check.Equal(48, launched.ThreadCount, "Launch uses the choice held through preflight");
        rig.Engine.Start();
        var tick = rig.Engine.TickAsync();
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Throws<InvalidOperationException>(() => rig.Engine.SetThreadCount(job.Id, 16), "A launch preflight owns the queued job");
        }
        finally { release.TrySetResult(); await tick; }
        Check.Equal(job.Id, rig.Runner.Launched.Single(), "The serialized preflight reaches launch");
    }

    private static async Task RollBackSelection()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("threads-save-failure.spd");
        rig.Engine.Start();
        using (File.Open(rig.Store.QueuePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Check.Throws<IOException>(() => rig.Engine.SetThreadCount(job.Id, 32), "Report failed persistence");
        Check.Equal(64, job.ThreadCount, "Roll back the in-memory selection");
        Check.Equal(64, rig.Store.Load().Jobs.Single().ThreadCount, "Keep the last durable selection");
        Check.That(!rig.Engine.IsArmed, "Pause launch after a save failure");
        rig.Engine.SetThreadCount(job.Id, 32);
        Check.Equal(32, rig.Store.Load().Jobs.Single().ThreadCount, "Release the edit lock after failure");
    }

    private static Task ConfigureBeforeBegin()
    {
        Guid id = Guid.NewGuid();
        Check.Equal(PowerSiRunner.CreateScript(id, "fixture.spd", "run", threadCount: 64),
            PowerSiRunner.CreateScript(id, "fixture.spd", "run"), "The default script requests 64 threads");
        foreach (var tool in Enum.GetValues<SimulationTool>())
        foreach (int count in new[] { 16, 24, 32, 48, 64 })
        {
            string script = PowerSiRunner.CreateScript(id, "fixture.spd", "run", tool, count).ReplaceLineEndings("\n");
            string command = "sigrity::update HybridComputerResource -smt 0 -local -cn localhost -cpus " + count + " -dp_solver false {!}";
            int open = script.IndexOf("sigrity::open document $::scheduler_spd {!}", StringComparison.Ordinal);
            int update = script.IndexOf(command, StringComparison.Ordinal);
            int reject = script.IndexOf("scheduler_mark START_REJECTED", StringComparison.Ordinal);
            int begin = script.IndexOf("sigrity::begin simulation {!}", StringComparison.Ordinal);
            Check.That(open >= 0 && open < update && update < reject && reject < begin, $"Apply the exact {count}-thread command before starting {tool}");
            Check.That(script[..update].Contains("set ::scheduler_rejected 0\nif {[catch {", StringComparison.Ordinal) &&
                script[update..begin].Contains("} message]} {\n    set ::scheduler_rejected 1\n    scheduler_error \"$step $message\"\n    scheduler_mark START_REJECTED\n}\nif {!$::scheduler_rejected} {\n    scheduler_mark BEGIN_REQUESTED", StringComparison.Ordinal),
                "A setup error must report rejection and bypass the entire begin block");
            Check.That(script[open..update].Contains($"Could not set simulation threads to {count}:", StringComparison.Ordinal),
                "Describe the rejected step and requested count in error.txt");
        }
        return Task.CompletedTask;
    }
}
