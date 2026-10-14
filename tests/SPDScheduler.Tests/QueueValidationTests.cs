using System.Text.Json.Nodes;
using SPDScheduler.Core;

namespace SPDScheduler.Tests;

internal static class QueueValidationTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        (string Name, Action<JsonObject> Tamper)[] cases =
        [
            ("external run directory", root => Run(root)["DirectoryPath"] = Path.GetTempPath()),
            ("relative run directory", root => Run(root)["DirectoryPath"] = @"runs\relative"),
            ("empty run UUID", root => Run(root)["Id"] = Guid.Empty.ToString()),
            ("duplicate run UUID", root =>
            {
                var duplicate = Job(root).DeepClone().AsObject();
                duplicate["Id"] = Guid.NewGuid().ToString();
                root["Jobs"]!.AsArray().Add(duplicate);
            }),
            ("null child collection", root => Run(root)["Children"] = null),
            ("null child identity", root => Run(root)["Children"]![0] = null),
            ("invalid root PID", root => Run(root)["Process"]!["Pid"] = 0),
            ("invalid child PID", root => Child(root)["Pid"] = -1),
            ("relative root image", root => Run(root)["Process"]!["ImagePath"] = "PowerSI.exe"),
            ("relative child image", root => Child(root)["ImagePath"] = "solver.exe"),
            ("default root start time", root => Run(root)["Process"]!["StartedAt"] = "0001-01-01T00:00:00+00:00"),
            ("default child start time", root => Child(root)["StartedAt"] = "0001-01-01T00:00:00+00:00"),
            ("undefined numeric-string status", root => Job(root)["Status"] = "999"),
            ("non-SPD input", root => Job(root)["FilePath"] = Path.Combine(Path.GetTempPath(), "notes.txt")),
            ("relative input", root => Job(root)["FilePath"] = "fixture.spd"),
            ("empty job UUID", root => Job(root)["Id"] = Guid.Empty.ToString())
        ];
        foreach (var (name, tamper) in cases)
            yield return ($"Queue rejects {name} without changing the source", () => RejectsMalformedState(tamper));
    }

    private static JsonObject Job(JsonObject root) => root["Jobs"]![0]!.AsObject();
    private static JsonObject Run(JsonObject root) => Job(root)["Run"]!.AsObject();
    private static JsonObject Child(JsonObject root) => Run(root)["Children"]![0]!.AsObject();

    private static Task RejectsMalformedState(Action<JsonObject> tamper)
    {
        using var rig = new TestRig();
        var run = new RunRecord
        {
            CreatedAt = rig.Clock.Now,
            Process = new ProcessIdentity(10_000, rig.Clock.Now, @"C:\Synthetic\PowerSI.exe"),
            Children = [new ProcessIdentity(10_001, rig.Clock.Now, @"C:\Synthetic\solver.exe")]
        };
        run.DirectoryPath = Path.Combine(rig.Store.DataDirectory, "runs", run.Id.ToString("N"));
        var document = new QueueDocument { Jobs = [new JobItem
            { FilePath = rig.CreateFile("synthetic.spd"), Status = JobStatus.Running, Run = run }] };
        rig.Store.Save(document);
        var json = JsonNode.Parse(File.ReadAllText(rig.Store.QueuePath))!.AsObject();
        tamper(json);
        var malformed = json.ToJsonString();
        File.WriteAllText(rig.Store.QueuePath, malformed);
        var sentinel = rig.CreateFile("completion.txt");
        var sentinelBefore = File.ReadAllText(sentinel);

        Check.Throws<InvalidDataException>(() => rig.Store.Load(), "Malformed nested state must be rejected before recovery");
        Check.Equal(malformed, File.ReadAllText(rig.Store.QueuePath), "Validation failure must preserve the original queue bytes");
        Check.Equal(sentinelBefore, File.ReadAllText(sentinel), "Validation must not touch files outside the owned run directory");
        return Task.CompletedTask;
    }
}
