using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Colossal.Logging;

namespace CityStoryMod.Storyteller
{
    // Runs the storyteller by shelling out to the `claude` CLI (Claude Code) with
    // the city dir as the working directory. The CLI uses whatever auth it's been
    // logged into (Max subscription, Pro, or its own API key), so no key flows
    // through this mod. The CLI also runs its OWN tool loop with Read/Write/Edit/
    // Glob/Grep against the cwd — we don't use the AgentLoop / ToolExecutor in
    // this mod for this path.
    //
    // The prompt is just the slash-command name (e.g. "/story-driven"); Claude
    // Code resolves it against the .claude/commands/ tree scaffolded into the
    // city dir from template/.
    //
    // Files-written count: post-run wall of mtimes inside the city dir, counting
    // files touched after the subprocess started. Cheap (one Directory walk).
    public static class ClaudeCliRunner
    {
        // Subdirs under the city dir where storyteller writes land. Skips snapshots/
        // (mod-written) and .claude/ (template, never rewritten).
        static readonly string[] s_TrackedSubdirs =
        {
            "canon", "characters", "companies", "places", "factions",
            "events", "sessions", "stories", "secrets",
        };

        public static async Task<RunResult> RunAsync(
            string cityDir, string commandName, ILog log, CancellationToken ct)
        {
            if (!Directory.Exists(cityDir))
                return RunResult.Failed($"City dir does not exist: {cityDir}");

            string exe = ResolveClaudeExe();
            if (exe == null)
                return RunResult.Failed(
                    "Could not find the `claude` executable on PATH. "
                    + "Install Claude Code (https://claude.ai/code) and confirm `claude --version` "
                    + "works from the same shell CS2 was launched from.");

            string prompt = "/" + commandName;
            DateTime startedUtc = DateTime.UtcNow;

            // .NET Framework 4.8 has no ProcessStartInfo.ArgumentList — single Arguments
            // string only. The prompt is always a slash-command (e.g. /story-driven)
            // with no spaces or quotes, so naive double-quoting is safe. Re-evaluate
            // if commandName ever takes free-form text.
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-p \"" + prompt.Replace("\"", "\\\"") + "\"",
                WorkingDirectory = cityDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            log.Info($"ClaudeCliRunner: spawning `{exe} -p \"{prompt}\"` (cwd={cityDir})");

            using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var stdout = new System.Text.StringBuilder();
                var stderr = new System.Text.StringBuilder();
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                try { proc.Start(); }
                catch (Exception ex)
                {
                    return RunResult.Failed($"Failed to start `claude`: {ex.Message}");
                }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(); } catch { } }))
                {
                    await Task.Run(() => proc.WaitForExit(), CancellationToken.None);
                }

                if (ct.IsCancellationRequested)
                {
                    log.Info("ClaudeCliRunner: cancelled.");
                    return RunResult.Cancelled();
                }

                int exit = proc.ExitCode;
                int filesWritten = CountFilesTouchedSince(cityDir, startedUtc);

                if (exit != 0)
                {
                    string tail = TruncateTail(stderr.Length > 0 ? stderr.ToString() : stdout.ToString(), 400);
                    log.Warn($"ClaudeCliRunner: exit={exit}, stderr/stdout tail: {tail}");
                    return RunResult.Failed($"claude -p exited with code {exit}: {tail}");
                }

                log.Info($"ClaudeCliRunner: exit=0, files touched={filesWritten}.");
                return RunResult.Ok(filesWritten);
            }
        }

        // PATH lookup for `claude` / `claude.exe` / `claude.cmd` — Claude Code on
        // Windows ships as a .cmd wrapper around the Node binary. ProcessStartInfo
        // with UseShellExecute=false won't resolve PATH or extensions on its own,
        // so we do it explicitly.
        static string ResolveClaudeExe()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] dirs = path.Split(Path.PathSeparator);
            string[] names = { "claude.cmd", "claude.exe", "claude.bat", "claude" };
            foreach (string dir in dirs)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (string name in names)
                {
                    string candidate;
                    try { candidate = Path.Combine(dir, name); }
                    catch { continue; }
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        static int CountFilesTouchedSince(string cityDir, DateTime sinceUtc)
        {
            int n = 0;
            foreach (string sub in s_TrackedSubdirs)
            {
                string dir = Path.Combine(cityDir, sub);
                if (!Directory.Exists(dir)) continue;
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { if (File.GetLastWriteTimeUtc(f) >= sinceUtc) n++; }
                    catch { }
                }
            }
            return n;
        }

        static string TruncateTail(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "(empty)";
            s = s.Trim();
            if (s.Length <= max) return s;
            return "…" + s.Substring(s.Length - max);
        }
    }
}
