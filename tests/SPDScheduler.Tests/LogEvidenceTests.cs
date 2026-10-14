using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.Tests;

internal static class LogEvidenceTests
{
    private static CompletionRule Rule(string start = "START", string complete = "DONE") =>
        new() { Workflow = "synthetic", SpdPattern = "synthetic", StartedPattern = start, CompletedPattern = complete };

    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return Test("Ordered current-run log evidence confirms completion", OrderedCompletion);
        yield return Test("A newer start invalidates previously observed completion", NewStartInvalidates);
        yield return Test("Suspension or license wait invalidates completion until a later terminal signal", BlockedCompletion);
        yield return Test("Completion requires an observed start in this run", RequiresStart);
        yield return Test("Invalid or empty log expressions never confirm completion", InvalidExpressions);
        yield return Test("Timed-out log expression never confirms completion", TimedOutExpression);
        yield return Test("Coincident start and completion matches are not terminal evidence", CoincidentMatches);
    }

    private static (string, Func<Task>) Test(string name, Action test) =>
        (name, () => { test(); return Task.CompletedTask; });

    private static void OrderedCompletion()
    {
        var result = PowerSiRunner.EvaluateLogEvidence("START\nworking\nDONE", Rule(), false);
        Check.That(result.Reliable && result.Started && result.Completed, "Start followed by terminal evidence should complete");
        Check.That(!result.NewerStart && !result.Blocked, "Current terminal evidence must have no newer busy event");
    }

    private static void NewStartInvalidates()
    {
        var first = PowerSiRunner.EvaluateLogEvidence("START\nDONE", Rule(), false);
        Check.That(first.Completed, "Fixture must establish prior completion");
        var restarted = PowerSiRunner.EvaluateLogEvidence("START\nDONE\nSTART", Rule(), first.Started);
        Check.That(restarted.Reliable && restarted.NewerStart && !restarted.Completed,
            "A newer start must invalidate the earlier completion rather than latch success");
        var finishedAgain = PowerSiRunner.EvaluateLogEvidence("START\nDONE\nSTART\nDONE", Rule(), restarted.Started);
        Check.That(finishedAgain.Completed && !finishedAgain.NewerStart, "A later terminal signal may restore completion");
    }

    private static void BlockedCompletion()
    {
        foreach (var busy in new[] { "Simulation is suspended.", "Waiting for license", "License unavailable", "Insufficient memory" })
        {
            var output = "START\nDONE\n" + busy;
            var blocked = PowerSiRunner.EvaluateLogEvidence(output, Rule(), true);
            Check.That(blocked.Reliable && blocked.Blocked && !blocked.Completed, $"{busy} must override earlier completion");
            var terminal = PowerSiRunner.EvaluateLogEvidence(output + "\nDONE", Rule(), blocked.Started);
            Check.That(terminal.Completed && !terminal.Blocked, "Only a newer terminal signal should clear the blocker");
        }
    }

    private static void RequiresStart()
    {
        Check.That(!PowerSiRunner.EvaluateLogEvidence("DONE", Rule(), false).Completed,
            "An isolated terminal line cannot prove a new run started");
        Check.That(PowerSiRunner.EvaluateLogEvidence("DONE", Rule(), true).Completed,
            "Persisted current-run start evidence may survive log-tail truncation");
    }

    private static void InvalidExpressions()
    {
        foreach (var rule in new[] { Rule("["), Rule(complete: "["), Rule(""), Rule(complete: "") })
        {
            var result = PowerSiRunner.EvaluateLogEvidence("START\nDONE", rule, true);
            Check.That(!result.Reliable && !result.Completed, "Invalid expressions must fail closed");
        }
    }

    private static void TimedOutExpression()
    {
        var output = "START\n" + new string('a', 2048) + "!";
        var result = PowerSiRunner.EvaluateLogEvidence(output, Rule(complete: "(a+)+$"), true);
        Check.That(!result.Reliable && !result.Completed, "A regex timeout cannot yield positive terminal evidence");
    }

    private static void CoincidentMatches()
    {
        var result = PowerSiRunner.EvaluateLogEvidence("DONE", Rule("DONE", "DONE"), false);
        Check.That(result.Reliable && !result.Completed, "The same match cannot prove both start and later completion");
    }
}
