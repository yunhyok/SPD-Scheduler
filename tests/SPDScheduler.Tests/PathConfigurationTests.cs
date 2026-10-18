using System.Reflection;
using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.Tests;

internal static class PathConfigurationTests
{
    private static readonly Func<string?, Func<IReadOnlyList<string>>, string> Resolve =
        typeof(PowerSiDiscovery).GetMethod("ResolveExecutablePath", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<string?, Func<IReadOnlyList<string>>, string>>();

    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return ("Blank executable settings resolve without launching PowerSI", ResolvesPaths);
        yield return ("Missing or invalid executable settings produce an actionable error", RejectsInvalidPaths);
        yield return ("Start persists the discovered executable before a future reservation", PersistsBeforeReservation);
        yield return ("Invalid shared configuration leaves every reservation queued", InvalidStartPreservesQueue);
        yield return ("Disappeared executable pauses before consuming a reserved job", () => MissingAtLaunch(false));
        yield return ("Configuration is checked again after the final resource capture", () => MissingAtLaunch(true));
        yield return ("Failed terminal jobs remain visible after later polling", FailureRemainsVisible);
        yield return ("Invalid runner configuration cannot create a run or process", InvalidRunnerCannotLaunch);
    }

    private static string FixtureExecutable(TestRig rig, string name = "powersi.exe") =>
        rig.CreateFile(name); // Text fixture only; never executed.

    private static Task ResolvesPaths()
    {
        using var rig = new TestRig();
        string directory = Path.Combine(rig.DirectoryPath, "한글 경로 [테스트]");
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "PowerSI.exe");
        File.WriteAllText(executable, "Non-executable fixture");
        foreach (string? path in new[] { null, "", " \t " })
            Check.Equal(executable, Resolve(path, () => [executable]), "Blank configuration must use the discovered path");
        Check.Equal(executable, Resolve(" " + executable + " ", () => throw new Exception("Configured paths must not trigger discovery")),
            "A user's configured installation must be preserved");
        var settings = new AppSettings { PowerSiPath = executable };
        new PowerSiRunner().ValidateConfiguration(settings);
        Check.Equal(executable, settings.PowerSiPath, "Validation must be read-only with respect to the executable");
        string alias = FixtureExecutable(rig, "pwrsi.exe");
        Check.Equal(alias, Resolve(alias, () => []), "The supported PowerSI executable alias remains valid");
        return Task.CompletedTask;
    }

    private static Task RejectsInvalidPaths()
    {
        using var rig = new TestRig();
        string existing = FixtureExecutable(rig);
        foreach (string? path in new[] { null, "", " \t " })
        {
            try { Resolve(path, () => []); throw new InvalidOperationException("Missing install was accepted"); }
            catch (ArgumentException ex)
            {
                Check.That(ex.Message.Contains("PowerSI") && ex.Message.Contains("설정"), "The message must identify the missing executable setting");
                Check.That(!ex.Message.Contains("The path is empty"), "The raw empty-path error must never reach the user");
            }
        }
        foreach (string path in new[] { "powersi.exe", Path.Combine(rig.DirectoryPath, "missing", "powersi.exe"),
            FixtureExecutable(rig, "unrelated.exe"), FixtureExecutable(rig, "powersi.cmd"), rig.DirectoryPath })
            Check.Throws<ArgumentException>(() => Resolve(path, () => [existing]), "Invalid explicit paths must not silently fall back to another installation");
        return Task.CompletedTask;
    }

    private static async Task PersistsBeforeReservation()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        string executable = FixtureExecutable(rig);
        var job = rig.Add("reserved.spd");
        var scheduled = rig.Clock.Now.AddHours(1);
        rig.Engine.SetSchedule(job.Id, scheduled);
        rig.Runner.BeforeValidateConfiguration = settings => settings.PowerSiPath = Resolve(settings.PowerSiPath, () => [executable]);
        rig.Engine.Start();
        Check.Equal(executable, rig.Store.Load().Settings.PowerSiPath, "The discovered path must be saved at Start, not only displayed in settings");
        await rig.TickAsync();
        Check.Equal(0, rig.Runner.Launched.Count, "Validation must not start a future job");
        Check.Equal(scheduled, job.ScheduledAt!.Value, "Preflight must preserve the reservation");
        rig.Clock.Advance(TimeSpan.FromHours(1));
        await rig.TickAsync();
        Check.Equal(job.Id, rig.Runner.Launched.Single(), "The fake runner should launch once when the reservation becomes due");
    }

    private static async Task InvalidStartPreservesQueue()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var first = rig.Add("first.spd");
        var second = rig.Add("second.spd");
        rig.Engine.SetSchedule(first.Id, rig.Clock.Now.AddHours(1));
        rig.Runner.BeforeValidateConfiguration = settings => settings.PowerSiPath = Resolve(settings.PowerSiPath, () => []);
        Check.Throws<ArgumentException>(rig.Engine.Start, "Missing executable must be caught at Start");
        await rig.TickAsync();
        Check.That(!rig.Engine.IsArmed && rig.Engine.StatusText.Contains("실행 설정 확인 필요"), "Configuration failure must pause with an actionable reason");
        foreach (var job in new[] { first, second })
            Check.That(job.Status == JobStatus.Queued && job.Run is null && job.LastError is null, "Shared configuration failure must not consume any job");
        Check.That(rig.Store.Load().Jobs.All(j => j.Status == JobStatus.Queued), "Reservations remain queued on disk");
        Check.Equal(0, rig.Runner.Launched.Count, "No process may launch");
    }

    private static async Task MissingAtLaunch(bool duringCapture)
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        string executable = FixtureExecutable(rig);
        var job = rig.Add("reserved.spd");
        rig.Add("following.spd");
        rig.Engine.SetSchedule(job.Id, rig.Clock.Now.AddHours(1));
        rig.Runner.BeforeValidateConfiguration = settings => settings.PowerSiPath = Resolve(settings.PowerSiPath, () => [executable]);
        rig.Engine.Start();
        rig.Clock.Advance(TimeSpan.FromHours(1));
        if (duringCapture)
            rig.Probe.BeforeCaptureAsync = count => { if (count == 2) File.Delete(executable); return Task.CompletedTask; };
        else File.Delete(executable);
        await rig.TickAsync();
        Check.That(!rig.Engine.IsArmed && rig.Engine.StatusText.Contains("PowerSI"), "The queue must pause when the configured executable disappears");
        Check.That(rig.Engine.Jobs.All(j => j.Status == JobStatus.Queued && j.Run is null), "No job or run may be consumed before validation");
        Check.Equal(0, rig.Runner.Launched.Count, "No fake or real process may launch on bad configuration");
        File.WriteAllText(executable, "Non-executable fixture");
        rig.Engine.Start();
        await rig.TickAsync();
        Check.Equal(job.Id, rig.Runner.Launched.Single(), "A corrected configuration resumes the original reservation once");
    }

    private static async Task FailureRemainsVisible()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var job = rig.Add("missing.spd");
        File.Delete(job.FilePath);
        rig.Engine.Start();
        await rig.TickAsync(3);
        Check.Equal(JobStatus.Failed, job.Status, "Missing input must remain failed");
        Check.That(rig.Engine.StatusText.Contains("실패 1건") && !rig.Engine.StatusText.Contains("목록 완료"),
            "Later polls must not hide terminal failures behind a success-like message");
    }

    private static async Task InvalidRunnerCannotLaunch()
    {
        using var rig = new TestRig();
        var job = new JobItem { FilePath = rig.CreateFile("synthetic.spd") };
        var settings = new AppSettings { PowerSiPath = Path.Combine(rig.DirectoryPath, "missing", "powersi.exe") };
        try { await new PowerSiRunner().LaunchAsync(job, settings, CancellationToken.None); throw new InvalidOperationException("Invalid path launched"); }
        catch (ArgumentException ex) { Check.That(ex.Message.Contains("PowerSI"), "Runner must share actionable configuration validation"); }
        Check.That(job.Run is null, "Validation must run before creating any run record or process");
    }
}
