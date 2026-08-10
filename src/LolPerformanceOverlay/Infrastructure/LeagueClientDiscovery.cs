using System.Diagnostics;
using System.IO;

namespace LolPerformanceOverlay.Infrastructure;

internal sealed record LeagueClientCredentials(int ProcessId, int Port, string Password, string Protocol);

internal static class LeagueClientDiscovery
{
    private const int MaximumLockfileCharacters = 4_096;

    public static LeagueClientCredentials? TryDiscover()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var processName in new[] { "LeagueClientUx", "LeagueClient" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(executable))
                    {
                        var directory = Path.GetDirectoryName(executable);
                        if (!string.IsNullOrWhiteSpace(directory))
                        {
                            candidates.Add(Path.Combine(directory, "lockfile"));
                            var parent = Directory.GetParent(directory)?.FullName;
                            if (!string.IsNullOrWhiteSpace(parent))
                            {
                                candidates.Add(Path.Combine(parent, "lockfile"));
                            }
                        }
                    }
                }
                catch
                {
                    // The client can restart between enumeration and inspection.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Riot Games",
            "League of Legends",
            "lockfile"));
        candidates.Add(@"C:\Riot Games\League of Legends\lockfile");

        foreach (var path in candidates.Where(File.Exists))
        {
            var credentials = ParseLockfile(path);
            if (credentials is not null)
            {
                return credentials;
            }
        }

        return null;
    }

    internal static LeagueClientCredentials? ParseLockfile(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaximumLockfileCharacters)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var buffer = new char[MaximumLockfileCharacters + 1];
            var length = reader.ReadBlock(buffer, 0, buffer.Length);
            if (length > MaximumLockfileCharacters || reader.Peek() >= 0)
            {
                return null;
            }

            var parts = new string(buffer, 0, length).Trim().Split(':');
            if (parts.Length < 5 ||
                !int.TryParse(parts[1], out var processId) ||
                !int.TryParse(parts[2], out var port) ||
                string.IsNullOrWhiteSpace(parts[3]))
            {
                return null;
            }

            return new LeagueClientCredentials(processId, port, parts[3], parts[4]);
        }
        catch
        {
            return null;
        }
    }
}
