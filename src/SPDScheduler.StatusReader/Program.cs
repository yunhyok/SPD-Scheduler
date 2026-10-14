using SPDScheduler.Windows;

namespace SPDScheduler.StatusReader;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => WindowsTextWorker.TryRun(args) ?? 2;
}
