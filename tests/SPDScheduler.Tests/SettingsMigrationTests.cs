using SPDScheduler.Core;

namespace SPDScheduler.Tests;

internal static class SettingsMigrationTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return ("Legacy resource defaults migrate once without changing queued jobs", LegacyDefaults);
        yield return ("Missing legacy thresholds use legacy defaults before migration", MissingFields);
        yield return ("Custom resource limits and durations survive migration", CustomSettings);
        yield return ("Invalid legacy thresholds are rejected before migration", InvalidSettings);
        yield return ("External BBS overlap defaults on and an explicit opt-out persists", BbsPolicy);
    }

    private static async Task LegacyDefaults()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("synthetic-migration.spd");
        rig.Engine.SetSchedule(job.Id, rig.Clock.Now.AddHours(1));
        string json = File.ReadAllText(rig.Store.QueuePath)
            .Replace("\"ResourcePolicyVersion\": 1,", "")
            .Replace("\"IdleCpuPercent\": 50", "\"IdleCpuPercent\": 1")
            .Replace("\"IdleMemoryMiB\": 128", "\"IdleMemoryMiB\": 16")
            .Replace("\"IdleSeconds\": 60", "\"IdleSeconds\": 180");
        File.WriteAllText(rig.Store.QueuePath, json);
        var loaded = rig.Store.Load();
        Check.Equal(50d, loaded.Settings.IdleCpuPercent, "Untouched legacy CPU default must migrate");
        Check.Equal(128, loaded.Settings.IdleMemoryMiB, "Untouched legacy memory default must migrate");
        Check.Equal(60, loaded.Settings.IdleSeconds, "Untouched legacy quiet duration must migrate");
        Check.Equal(job.Id, loaded.Jobs.Single().Id, "Job identity must survive");
        Check.Equal(job.ScheduledAt, loaded.Jobs.Single().ScheduledAt, "Reservation must survive");
        Check.Equal(json, File.ReadAllText(rig.Store.QueuePath), "Load alone must not modify the original file");
        loaded.Settings.IdleCpuPercent = 1;
        loaded.Settings.IdleMemoryMiB = 16;
        loaded.Settings.IdleSeconds = 180;
        rig.Store.Save(loaded);
        var reopened = rig.Store.Load();
        Check.Equal(1d, reopened.Settings.IdleCpuPercent, "Explicit strict settings in the new policy must not migrate again");
        Check.Equal(180, reopened.Settings.IdleSeconds, "Explicit quiet duration must be preserved");
    }

    private static Task MissingFields()
    {
        using var rig = new TestRig();
        foreach (string json in new[] { "{\"SchemaVersion\":1,\"Jobs\":[]}", "{\"SchemaVersion\":1,\"Settings\":{},\"Jobs\":[]}" })
        {
            File.WriteAllText(rig.Store.QueuePath, json);
            var settings = rig.Store.Load().Settings;
            Check.Equal(50d, settings.IdleCpuPercent, "Absent legacy CPU migrates");
            Check.Equal(128, settings.IdleMemoryMiB, "Absent legacy memory migrates");
            Check.Equal(60, settings.IdleSeconds, "Absent legacy duration migrates");
        }
        return Task.CompletedTask;
    }

    private static Task CustomSettings()
    {
        using var rig = new TestRig();
        foreach (var (cpu, memory, seconds) in new[] { (2.5, 16, 180), (1d, 0, 180), (50d, 128, 180), (0.01, 16, 900) })
        {
            var document = new QueueDocument { Settings = new AppSettings { IdleCpuPercent = cpu, IdleMemoryMiB = memory, IdleSeconds = seconds, PollIntervalMinutes = 5 } };
            rig.Store.Save(document);
            File.WriteAllText(rig.Store.QueuePath, File.ReadAllText(rig.Store.QueuePath).Replace("\"ResourcePolicyVersion\": 1,", ""));
            var settings = rig.Store.Load().Settings;
            Check.Equal(cpu, settings.IdleCpuPercent, "Custom CPU must survive");
            Check.Equal(memory, settings.IdleMemoryMiB, "Custom memory including strict zero must survive");
            Check.Equal(seconds, settings.IdleSeconds, "Custom duration must survive");
            Check.Equal(5, settings.PollIntervalMinutes, "Polling cadence must survive");
        }
        File.WriteAllText(rig.Store.QueuePath, "{\"Settings\":{\"IdleCpuPercent\":1,\"IdleMemoryMiB\":16,\"IdleSeconds\":900}}");
        Check.Equal(900, rig.Store.Load().Settings.IdleSeconds, "A custom duration with old resource defaults must survive");
        File.WriteAllText(rig.Store.QueuePath, "{\"Settings\":{\"IdleCpuPercent\":2.5}}");
        Check.Equal(16, rig.Store.Load().Settings.IdleMemoryMiB, "Missing memory with custom CPU retains legacy strict meaning");
        return Task.CompletedTask;
    }

    private static Task InvalidSettings()
    {
        using var rig = new TestRig();
        foreach (string settings in new[] { "\"IdleCpuPercent\":-1", "\"IdleMemoryMiB\":-1", "\"IdleSeconds\":0", "\"ResourcePolicyVersion\":99" })
        {
            string json = "{\"Settings\":{" + settings + "}}";
            File.WriteAllText(rig.Store.QueuePath, json);
            Check.Throws<InvalidDataException>(() => rig.Store.Load(), "Invalid state must not be repaired through migration");
            Check.Equal(json, File.ReadAllText(rig.Store.QueuePath), "Invalid original must be preserved");
        }
        return Task.CompletedTask;
    }

    private static Task BbsPolicy()
    {
        using var rig = new TestRig();
        File.WriteAllText(rig.Store.QueuePath, "{\"SchemaVersion\":1,\"Jobs\":[],\"Settings\":{\"ResourcePolicyVersion\":1,\"IdleCpuPercent\":12,\"IdleMemoryMiB\":48}}");
        var loaded = rig.Store.Load();
        Check.That(loaded.Settings.AllowBbsPostProcessing, "Existing queues enable the requested external BBS overlap policy");
        Check.Equal(12d, loaded.Settings.IdleCpuPercent, "Enabling BBS overlap must not raise custom CPU limits");
        Check.Equal(48, loaded.Settings.IdleMemoryMiB, "Enabling BBS overlap must not change custom memory limits");
        loaded.Settings.AllowBbsPostProcessing = false;
        rig.Store.Save(loaded);
        Check.That(!rig.Store.Load().Settings.AllowBbsPostProcessing, "A saved opt-out must survive reload");
        return Task.CompletedTask;
    }
}
