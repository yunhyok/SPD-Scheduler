using System.IO;

namespace SPDScheduler.Windows;

public static class PowerSiDiscovery
{
    public static IReadOnlyList<string> FindInstalled()
    {
        var root = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Cadence");
        if (!Directory.Exists(root)) return [];
        try
        {
            return Directory.EnumerateDirectories(root, "Sigrity*")
                .Select(folder => Path.Combine(folder, "tools", "bin", "powersi.exe"))
                .Where(File.Exists).OrderDescending(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
