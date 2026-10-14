using System.Reflection;
using System.Reflection.PortableExecutable;
using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.Tests;

internal static class WorkerTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return ("Status reader is a dedicated Windows console executable", ConsoleSubsystem);
        yield return ("Repeated invalid-identity reads use the dedicated worker safely", RepeatedInvalidIdentity);
        yield return ("Missing status reader reports an explicit installation error", MissingReader);
    }

    private static Task ConsoleSubsystem()
    {
        string executable = Resolve(AppContext.BaseDirectory);
        using var file = File.OpenRead(executable);
        using var reader = new PEReader(file);
        Check.Equal(Subsystem.WindowsCui, reader.PEHeaders.PEHeader!.Subsystem,
            "The actual worker apphost must use the console subsystem to avoid GUI startup feedback");
        Check.That(!Path.GetFileName(executable).Equals("SPDScheduler.exe", StringComparison.OrdinalIgnoreCase),
            "The worker must never use the GUI scheduler executable");
        foreach (string extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            Check.That(File.Exists(Path.Combine(AppContext.BaseDirectory, "SPDScheduler.StatusReader" + extension)),
                $"Development output must contain the worker {extension}");
        return Task.CompletedTask;
    }

    private static async Task RepeatedInvalidIdentity()
    {
        var method = typeof(WindowsTextWorker).GetMethod("ReadAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var identity = new ProcessIdentity(int.MaxValue, DateTimeOffset.UtcNow, @"C:\SyntheticOnly\PowerSI.exe");
        for (int i = 0; i < 6; i++)
        {
            var read = (Task<string?>)method.Invoke(null, [identity, CancellationToken.None])!;
            Check.That(await read.WaitAsync(TimeSpan.FromSeconds(5)) is null,
                "A nonexistent identity must be rejected before any PowerSI UI read");
        }
    }

    private static Task MissingReader()
    {
        using var rig = new TestRig();
        try { Resolve(rig.DirectoryPath); }
        catch (TargetInvocationException exception) when (exception.InnerException is FileNotFoundException missing)
        {
            Check.That(missing.Message.Contains("status reader", StringComparison.OrdinalIgnoreCase), "Missing helper must have a clear diagnostic");
            return Task.CompletedTask;
        }
        throw new InvalidOperationException("Missing helper must fail explicitly; GUI fallback is forbidden.");
    }

    private static string Resolve(string directory) =>
        (string)typeof(WindowsTextWorker).GetMethod("ResolveExecutable", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [directory])!;
}
