using System.IO;

namespace CityStoryMod
{
    // Pure-C# PATH utilities. Same testability rule as TextUtils: no Unity /
    // Game.dll / Colossal.* refs so `CityStoryMod.Tests` can link this file
    // directly. The split from TextUtils is by concern (text vs filesystem),
    // not by Unity-coupling — both files are equally pure.
    public static class PathUtils
    {
        // Searches each directory in a PATH-style string (entries separated by
        // Path.PathSeparator) for the first file matching any of `executableNames`.
        // Returns the full path of the first hit, or null if nothing matches.
        //
        // Used by ClaudeCliRunner because ProcessStartInfo with UseShellExecute=false
        // doesn't resolve PATH or extension fallbacks on Windows. Pass the env's
        // PATH plus the platform-appropriate name list (e.g. "claude.cmd",
        // "claude.exe", "claude") and the first hit is what cmd.exe would have
        // launched.
        //
        // Skips entries that are null, whitespace, or contain invalid path chars
        // (Path.Combine would throw on these — defensive against weird PATH values).
        public static string FindExecutable(string path, string[] executableNames)
        {
            if (string.IsNullOrEmpty(path) || executableNames == null) return null;
            string[] dirs = path.Split(Path.PathSeparator);
            foreach (string dir in dirs)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (string name in executableNames)
                {
                    string candidate;
                    try { candidate = Path.Combine(dir, name); }
                    catch { continue; }
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }
    }
}
