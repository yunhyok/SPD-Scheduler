using System.Reflection;
using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.Tests;

internal static class StatusReaderTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return Test("Status pane text/value content detects exact completion", PaneCompletion);
        yield return Test("Busy and blocked panes override a stale done pane", BlockingPanes);
        yield return Test("Ready and historical done descriptions never confirm completion", AmbiguousText);
        yield return Test("Truncated or excessive status text cannot confirm completion", IncompleteText);
        yield return Test("Unknown status diagnostics preserve bounded readable text", StatusDiagnostics);
    }

    private static (string, Func<Task>) Test(string name, Action test) =>
        (name, () => { test(); return Task.CompletedTask; });

    private static string? Normalize(params string?[] values) => (string?)typeof(WindowsTextWorker)
        .GetMethod("NormalizeStatusText", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [values]);

    private static ProcessSignal Classify(string? text) => (ProcessSignal)typeof(WindowsProcessProbe)
        .GetMethod("ClassifyStatus", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [text])!;

    private static string Evidence(string? text) => (string)typeof(WindowsProcessProbe)
        .GetMethod("StatusEvidence", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [text])!;

    private static void PaneCompletion()
    {
        var text = Normalize("Status bar", null, "Simulation Done.\r\n", "Simulation Done.");
        Check.Equal(ProcessSignal.Completed, Classify(text), "A current exact completion pane exposed by UIA text/value must be detected");
        Check.Equal(1, text!.Split('\n').Count(line => line == "Simulation Done."), "Duplicate native/UIA text must not multiply pane evidence");
    }

    private static void BlockingPanes()
    {
        foreach (var (text, signal) in new[]
        {
            ("Busy", ProcessSignal.Running), ("Busy.", ProcessSignal.Running),
            ("Simulation running", ProcessSignal.Running), ("Simulation is suspended.", ProcessSignal.Suspended),
            ("Waiting for license", ProcessSignal.LicenseWaiting), ("Insufficient memory", ProcessSignal.Error),
            ("Solver failed", ProcessSignal.Error)
        })
            Check.Equal(signal, Classify(Normalize("Simulation Done.", text)), "Current blocked or busy status must win across panes");
    }

    private static void AmbiguousText()
    {
        foreach (var text in new[] { "Ready", "Ready for simulation", "Previous: Simulation Done.", "Simulation Done. Next simulation running", "" })
            Check.That(Classify(Normalize(text)) != ProcessSignal.Completed, "Ready or embedded historical completion cannot substitute for exact current completion");
        foreach (var text in new[] { "0 errors", @"C:\case_error\failed_input.spd" })
            Check.Equal(ProcessSignal.None, Classify(text), "Innocent counts and paths must not become errors");
        Check.Equal(ProcessSignal.Completed, Classify(Normalize("Simulation Done.", "0 errors")),
            "A zero-error counter must not override valid completion");
    }

    private static void IncompleteText()
    {
        Check.That(Normalize("Simulation Done.", new string('x', 1025)) == null, "An overlong pane must invalidate the full read");
        Check.That(Normalize(Enumerable.Range(0, 65).Select(i => "Pane " + i).ToArray()) == null,
            "An excessive number of panes must invalidate the full read");
        Check.That(Normalize("Simulation Done.\0Busy") == null, "Control-character corruption must not hide busy evidence");
        Check.Equal(ProcessSignal.None, Classify(null), "An incomplete read must not become completed");
    }

    private static void StatusDiagnostics()
    {
        Check.That(Evidence(null).Contains("읽기 실패"), "Unreadable status must have an explicit diagnostic");
        Check.Equal("Ready\nLayer 1", Evidence("Ready\r\n\tLayer 1\0"), "Unrecognized current text must remain readable and sanitized");
        Check.Equal(1024, Evidence(new string('x', 2048)).Length, "Raw diagnostic text must be bounded per PID");
        Check.That(Evidence(string.Join('\n', Enumerable.Range(0, 30))).Split('\n').Length <= 16,
            "Raw diagnostic line count must be bounded per PID");
    }
}
