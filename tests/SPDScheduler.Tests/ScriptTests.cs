using System.Text;
using System.Text.RegularExpressions;
using SPDScheduler.Windows;

namespace SPDScheduler.Tests;

internal static class ScriptTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return Test("Generated Tcl transports adversarial paths as UTF-8 data", PathsAreData);
        yield return Test("Generated Tcl preserves saved workflow settings", PreservesWorkflow);
        yield return Test("Generated Tcl requires a matching command before save", RequiresAuthorization);
        yield return Test("Returning from begin does not generate a saved marker", BeginReturnIsNotSuccess);
    }

    private static (string, Func<Task>) Test(string name, Action test) =>
        (name, () => { test(); return Task.CompletedTask; });

    private static void PathsAreData()
    {
        const string spd = "C:\\한글 folder\\$[exec bad]; { }\\fixture.spd\nputs injected";
        const string directory = "C:\\runs\\{ } [exit] $;\r\n한글";
        var script = PowerSiRunner.CreateScript(Guid.NewGuid(), spd, directory);
        var encoded = Regex.Matches(script, @"binary decode base64 \{\s*([A-Za-z0-9+/=]+)\s*\}")
            .Select(match => Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value))).ToArray();
        Check.Equal(2, encoded.Length, "Both untrusted paths must be encoded");
        Check.Equal(spd, encoded[0], "SPD path must survive Unicode and metacharacters verbatim");
        Check.Equal(directory, encoded[1], "Run path must survive Unicode and metacharacters verbatim");
        Check.That(!script.Contains(spd, StringComparison.Ordinal) && !script.Contains(directory, StringComparison.Ordinal),
            "Untrusted paths must not enter Tcl source literally");
    }

    private static void PreservesWorkflow()
    {
        var script = PowerSiRunner.CreateScript(Guid.NewGuid(), "synthetic.spd", "synthetic-run");
        foreach (var setting in new[] { "-IgnoreMode", "-workflowkey", "-product", "extraction" })
            Check.That(!script.Contains(setting, StringComparison.OrdinalIgnoreCase), $"Generated script must not force {setting}");
        Check.That(script.Contains("sigrity::open document $::scheduler_spd", StringComparison.Ordinal),
            "The selected saved SPD must be opened");
    }

    private static void RequiresAuthorization()
    {
        var id = Guid.NewGuid();
        var script = PowerSiRunner.CreateScript(id, "synthetic.spd", "synthetic-run");
        Check.That(script.Contains(id.ToString("N"), StringComparison.Ordinal), "The script must bind commands to this run UUID");
        Check.That(script.Contains("$command eq \"SAVE_EXIT $::scheduler_id\" && !$::scheduler_rejected", StringComparison.Ordinal),
            "Save must require the exact current-run authorization and an accepted start");
        Check.That(script.Contains("$command eq \"ABORT_NO_SAVE $::scheduler_id\" && $::scheduler_rejected", StringComparison.Ordinal),
            "No-save close must require both explicit authorization and rejected startup");
        Check.That(script.Contains("catch {sigrity::save {!}}", StringComparison.Ordinal), "Save errors must be caught");
    }

    private static void BeginReturnIsNotSuccess()
    {
        var script = PowerSiRunner.CreateScript(Guid.NewGuid(), "synthetic.spd", "synthetic-run");
        var begin = script.IndexOf("sigrity::begin simulation", StringComparison.Ordinal);
        Check.That(begin >= 0, "A simulation start command must exist");
        var afterBegin = script[begin..];
        Check.That(afterBegin.Contains("scheduler_mark BEGIN_RETURNED", StringComparison.Ordinal), "Begin return needs its own marker");
        Check.That(!afterBegin.Contains("scheduler_mark SAVED", StringComparison.Ordinal), "Begin return cannot directly mark a successful save");
        Check.That(afterBegin.Contains("vwait", StringComparison.Ordinal), "The script must await explicit host control");
    }
}
