using SPDScheduler.Tests;

var tests = SchedulerTests.All().Concat(IdleGateTests.All()).Concat(ScriptTests.All())
    .Concat(RunnerRecoveryTests.All()).Concat(LogEvidenceTests.All()).Concat(QueueValidationTests.All()).Concat(WorkerTests.All())
    .Concat(ProcessTreeTests.All()).Concat(SettingsMigrationTests.All()).Concat(StatusReaderTests.All()).ToArray();
var failures = 0;
foreach (var (name, test) in tests)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed; {failures} failed.");
return failures == 0 ? 0 : 1;
