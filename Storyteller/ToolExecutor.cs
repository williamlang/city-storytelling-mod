using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace CityStoryMod.Storyteller
{
    // Executes the file/dir tools the agent calls. All paths are resolved against
    // the city dir; absolute paths, .. traversal, and any resolved path that
    // escapes the city root are rejected. State is per-run — instantiate one per
    // AgentLoop invocation.
    public class ToolExecutor
    {
        readonly string _cityDirFull;

        public int FilesWritten { get; private set; }

        // Quickstart founding result, stashed by a wizard_done tool call and
        // drained by PromptUISystem on the next OnUpdate into the wizardDone
        // value binding. Static because the executor is per-run and the UI
        // system holds no reference to it — this carries the payload across the
        // run boundary the same way _pendingMessages carries chat turns. The
        // dispatcher gates runs to one at a time, so a single slot is enough.
        static string _pendingWizardDone;

        // Pops the pending wizard_done payload (or null if none). Thread-safe:
        // the tool fires on the run's worker thread, the drain runs on the main
        // thread in OnUpdate.
        public static string TakePendingWizardDone()
            => System.Threading.Interlocked.Exchange(ref _pendingWizardDone, null);

        public ToolExecutor(string cityDir)
        {
            _cityDirFull = Path.GetFullPath(cityDir);
        }

        // Returns the tool's textual result. Throws on bad input — the AgentLoop
        // catches and surfaces those as tool_result with is_error=true so the
        // model can recover instead of the whole run failing.
        public string Execute(string toolName, JObject input)
        {
            switch (toolName)
            {
                case "read_file":
                {
                    string p = ResolveSafePath((string)input["path"]);
                    if (!File.Exists(p)) return $"File not found: {(string)input["path"]}";
                    return File.ReadAllText(p);
                }
                case "write_file":
                {
                    string p = ResolveSafePath((string)input["path"]);
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    File.WriteAllText(p, (string)input["content"]);
                    FilesWritten++;
                    return $"Wrote {(string)input["path"]}";
                }
                case "list_dir":
                {
                    string p = ResolveSafePath((string)input["path"]);
                    if (!Directory.Exists(p)) return $"Directory not found: {(string)input["path"]}";
                    StringBuilder sb = new StringBuilder();
                    foreach (string d in Directory.GetDirectories(p))
                        sb.Append("dir:  ").AppendLine(Path.GetFileName(d));
                    foreach (string f in Directory.GetFiles(p))
                        sb.Append("file: ").AppendLine(Path.GetFileName(f));
                    return sb.Length == 0 ? "(empty)" : sb.ToString();
                }
                case "glob":
                {
                    string pattern = (string)input["pattern"];
                    if (pattern.Contains("..")) throw new Exception("Pattern may not contain '..'.");
                    SearchOption opt = pattern.Contains("**") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    string flatPattern = pattern.Replace("**/", "").Replace("**", "*");
                    StringBuilder sb = new StringBuilder();
                    foreach (string f in Directory.GetFiles(_cityDirFull, flatPattern, opt))
                    {
                        string rel = f.Substring(_cityDirFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        sb.AppendLine(rel.Replace(Path.DirectorySeparatorChar, '/'));
                    }
                    return sb.Length == 0 ? "(no matches)" : sb.ToString();
                }
                case "wizard_done":
                {
                    string payload = QuickstartConfig.NormalizeWizardDone(input);
                    System.Threading.Interlocked.Exchange(ref _pendingWizardDone, payload);
                    return "Founding summary recorded; the player's result card will show it.";
                }
                default:
                    throw new Exception($"Unknown tool: {toolName}");
            }
        }

        // Reject absolute paths, .. traversal, and any resolved path that escapes
        // the city dir. Tolerate a leading '/' or '\' (small local models like
        // llama3.1:8b tend to write paths POSIX-style as if the city dir were
        // root, e.g. "/sessions/foo.md" instead of "sessions/foo.md"); strip
        // any leading separators before validating. Windows-drive prefixes
        // ("C:\foo") still fail the IsPathRooted check after trimming, so the
        // safety property is preserved.
        string ResolveSafePath(string relative)
        {
            if (string.IsNullOrEmpty(relative)) throw new Exception("Path is empty.");
            relative = relative.TrimStart('/', '\\');
            if (string.IsNullOrEmpty(relative)) throw new Exception("Path is empty.");
            if (Path.IsPathRooted(relative)) throw new Exception("Path must be relative to the city dir.");
            if (relative.Contains("..")) throw new Exception("Path may not contain '..'.");
            string full = Path.GetFullPath(Path.Combine(_cityDirFull, relative));
            if (!full.StartsWith(_cityDirFull, StringComparison.Ordinal))
                throw new Exception("Path escapes the city dir.");
            return full;
        }
    }
}
