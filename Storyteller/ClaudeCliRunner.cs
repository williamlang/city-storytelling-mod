using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Colossal.Logging;
using Newtonsoft.Json.Linq;

namespace CityStoryMod.Storyteller
{
    // Runs the storyteller by shelling out to the `claude` CLI (Claude Code) with
    // the city dir as the working directory. The CLI uses whatever auth it's been
    // logged into (Max subscription, Pro, or its own API key), so no key flows
    // through this mod. The CLI also runs its OWN tool loop with Read/Write/Edit/
    // Glob/Grep against the cwd — we don't use the AgentLoop / ToolExecutor in
    // this mod for this path.
    //
    // Output mode is `--output-format stream-json --verbose`, which makes Claude
    // Code emit a JSONL event stream on stdout. We parse each line as it arrives
    // and synthesize AssistantTurn / ToolResult events on Mod.Storyteller so the
    // PromptUISystem renders the chat the same way it does for API providers.
    //
    // The prompt is piped on stdin rather than passed as a positional argument —
    // sidesteps any Windows command-line quoting hazards for free-form user text.
    public static class ClaudeCliRunner
    {
        // Subdirs under the city dir where storyteller writes land. Skips snapshots/
        // (mod-written) and .claude/ (template, never rewritten).
        static readonly string[] s_TrackedSubdirs =
        {
            "canon", "characters", "companies", "places", "factions",
            "events", "sessions", "stories", "secrets",
        };

        // Convenience entry point for slash-command runs (e.g. /story-driven).
        public static Task<RunResult> RunAsync(
            string cityDir, string commandName, ILog log, CancellationToken ct) =>
            RunPromptAsync(cityDir, "/" + commandName, log, ct);

        public static async Task<RunResult> RunPromptAsync(
            string cityDir, string prompt, ILog log, CancellationToken ct)
        {
            if (!Directory.Exists(cityDir))
                return RunResult.Failed($"City dir does not exist: {cityDir}");
            if (string.IsNullOrWhiteSpace(prompt))
                return RunResult.Failed("Empty prompt.");

            string exe = ResolveClaudeExe();
            if (exe == null)
                return RunResult.Failed(
                    "Could not find the `claude` executable on PATH. "
                    + "Install Claude Code (https://claude.ai/code) and confirm `claude --version` "
                    + "works from the same shell CS2 was launched from.");

            DateTime startedUtc = DateTime.UtcNow;
            StorytellerDispatcher dispatcher = Mod.Storyteller;

            // Per-run state mutated from the stdout reader thread. Locked in
            // EmitFromLine so individual events stay self-consistent; the
            // dispatcher.Emit* methods are themselves event invocations and
            // safe to call from a worker thread (PromptUISystem queues).
            var lockObj = new object();
            TokenUsage totalUsage = default;
            var stderrTail = new StringBuilder();
            string resultSubtype = null;
            string resultErrors = null;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                // --verbose is required to use stream-json with -p.
                Arguments = "-p --output-format stream-json --verbose",
                WorkingDirectory = cityDir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                // ProcessStartInfo.StandardInputEncoding doesn't exist in net48;
                // set the stream's encoding after Start() instead.
            };

            log.Info($"ClaudeCliRunner: spawning `{exe} -p --output-format stream-json --verbose` (cwd={cityDir}) prompt='{prompt}'");

            using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                proc.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    try
                    {
                        JObject obj = JObject.Parse(e.Data);
                        // "result" is the final summary event — capture its
                        // subtype + errors for the outer RunResult.Message.
                        // is_error in this object only reflects whether the
                        // *model* returned an error; subtype reflects whether
                        // the run itself succeeded ("success" vs an
                        // "error_*" variant). Use subtype as the signal.
                        if ((string)obj["type"] == "result")
                        {
                            lock (lockObj)
                            {
                                resultSubtype = (string)obj["subtype"];
                                JArray errs = obj["errors"] as JArray;
                                if (errs != null && errs.Count > 0)
                                    resultErrors = string.Join("; ", errs);
                            }
                        }
                        TokenUsage perEvent = EmitFromLine(obj, dispatcher, log);
                        if (perEvent.InputTokens != 0 || perEvent.OutputTokens != 0
                            || perEvent.CacheReadTokens != 0 || perEvent.CacheWriteTokens != 0)
                        {
                            lock (lockObj) totalUsage += perEvent;
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Warn($"ClaudeCliRunner: failed to parse line ({ex.Message}): {Truncate(e.Data, 200)}");
                    }
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (lockObj) stderrTail.AppendLine(e.Data);
                };

                try { proc.Start(); }
                catch (Exception ex)
                {
                    return RunResult.Failed($"Failed to start `claude`: {ex.Message}");
                }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                // Feed the prompt over stdin so any quotes / newlines / shell
                // metacharacters in user-typed text don't need escaping.
                try
                {
                    await proc.StandardInput.WriteAsync(prompt);
                    proc.StandardInput.Close();
                }
                catch (Exception ex)
                {
                    log.Warn($"ClaudeCliRunner: stdin write failed: {ex.Message}");
                }

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
                TokenUsage finalUsage;
                string errTail;
                string subtype;
                string errors;
                lock (lockObj)
                {
                    finalUsage = totalUsage;
                    errTail = stderrTail.ToString();
                    subtype = resultSubtype;
                    errors = resultErrors;
                }

                if (exit != 0)
                {
                    string tail = TruncateTail(errTail, 400);
                    log.Warn($"ClaudeCliRunner: exit={exit}, stderr tail: {tail}");
                    return RunResult.Failed($"claude -p exited with code {exit}: {tail}")
                        .WithUsage(finalUsage);
                }

                // Exit code 0 but the CLI's own result event reported a
                // non-success subtype (e.g. error_during_execution from the
                // 2.0.76 effortLevel bug). Treat as failure so the UI shows
                // a visible error instead of a silent "success, 0 files".
                if (subtype != null && subtype != "success")
                {
                    string msg = errors != null
                        ? $"claude -p result: {subtype} — {Truncate(errors, 400)}"
                        : $"claude -p result: {subtype}";
                    log.Warn($"ClaudeCliRunner: {msg}");
                    return RunResult.Failed(msg).WithUsage(finalUsage);
                }

                log.Info($"ClaudeCliRunner: exit=0, files touched={filesWritten}, tokens in={finalUsage.InputTokens} out={finalUsage.OutputTokens}.");
                return RunResult.Ok(filesWritten).WithUsage(finalUsage);
            }
        }

        // Parses one stream-json line and fires the matching dispatcher event.
        // Returns the per-line token usage so the caller can accumulate the run
        // total. Recognized event types:
        //   "assistant" — model response (text + tool_use blocks)
        //   "user"      — when message.content has tool_result blocks
        //   "result"    — final summary event with total usage
        // Anything else (system/init, partial) is silently ignored.
        static TokenUsage EmitFromLine(JObject obj, StorytellerDispatcher dispatcher, ILog log)
        {
            string type = (string)obj["type"];
            switch (type)
            {
                case "assistant":
                {
                    JObject message = obj["message"] as JObject;
                    if (message == null) return default;

                    var text = new StringBuilder();
                    var calls = new List<ToolCall>();
                    JArray content = message["content"] as JArray;
                    if (content != null)
                    {
                        foreach (JToken block in content)
                        {
                            string btype = (string)block["type"];
                            if (btype == "text")
                            {
                                text.Append((string)block["text"]);
                            }
                            else if (btype == "tool_use")
                            {
                                calls.Add(new ToolCall
                                {
                                    Id = (string)block["id"],
                                    Name = (string)block["name"],
                                    Input = block["input"] as JObject ?? new JObject(),
                                });
                            }
                        }
                    }

                    TokenUsage usage = ParseAnthropicUsage(message["usage"] as JObject);
                    AssistantTurn turn = new AssistantTurn
                    {
                        TextContent = text.ToString(),
                        ToolCalls = calls,
                        RequiresToolResponse = calls.Count > 0,
                        Usage = usage,
                    };
                    dispatcher?.EmitAssistantTurn(turn);
                    return usage;
                }

                case "user":
                {
                    JObject message = obj["message"] as JObject;
                    JArray content = message?["content"] as JArray;
                    if (content == null) return default;

                    var results = new List<ToolResult>();
                    foreach (JToken block in content)
                    {
                        if ((string)block["type"] != "tool_result") continue;
                        results.Add(new ToolResult
                        {
                            ToolUseId = (string)block["tool_use_id"],
                            Content = StringifyToolResultContent(block["content"]),
                            IsError = (bool?)block["is_error"] ?? false,
                        });
                    }
                    if (results.Count > 0) dispatcher?.EmitToolResults(results);
                    return default;
                }

                case "result":
                {
                    // Authoritative final usage block from the CLI. The CLI sums
                    // every internal turn including ones we may have missed if a
                    // line couldn't be parsed, so it's the source of truth — but
                    // we still accumulate per-assistant-event usage above so the
                    // live token counter ticks up during the run, not just at end.
                    TokenUsage resultUsage = ParseAnthropicUsage(obj["usage"] as JObject);
                    // Return zero so we don't double-count: assistant events
                    // already contributed. The caller will see the running
                    // total match the result event's value at completion.
                    if (resultUsage.InputTokens > 0 || resultUsage.OutputTokens > 0)
                        log.Info($"ClaudeCliRunner: result usage in={resultUsage.InputTokens} out={resultUsage.OutputTokens}");
                    return default;
                }

                default:
                    return default;
            }
        }

        // tool_result block.content can be a plain string OR an array of
        // {type:"text",text:"..."} parts. Normalize to a single string.
        static string StringifyToolResultContent(JToken contentToken)
        {
            if (contentToken == null) return "";
            if (contentToken.Type == JTokenType.String) return (string)contentToken;
            if (contentToken is JArray arr)
            {
                var sb = new StringBuilder();
                foreach (JToken t in arr)
                {
                    if ((string)t["type"] == "text") sb.Append((string)t["text"]);
                }
                return sb.ToString();
            }
            return contentToken.ToString();
        }

        static TokenUsage ParseAnthropicUsage(JObject u)
        {
            if (u == null) return default;
            return new TokenUsage
            {
                InputTokens = u["input_tokens"]?.Value<int>() ?? 0,
                CacheReadTokens = u["cache_read_input_tokens"]?.Value<int>() ?? 0,
                CacheWriteTokens = u["cache_creation_input_tokens"]?.Value<int>() ?? 0,
                OutputTokens = u["output_tokens"]?.Value<int>() ?? 0,
            };
        }

        // PATH lookup for `claude` / `claude.exe` / `claude.cmd` — Claude Code on
        // Windows ships as a .cmd wrapper around the Node binary. ProcessStartInfo
        // with UseShellExecute=false won't resolve PATH or extensions on its own.
        // Pure helper lives in PathUtils.FindExecutable so the test project can
        // exercise it without referencing Game.dll.
        static readonly string[] s_ClaudeExeNames = { "claude.cmd", "claude.exe", "claude.bat", "claude" };
        static string ResolveClaudeExe() =>
            PathUtils.FindExecutable(Environment.GetEnvironmentVariable("PATH") ?? "", s_ClaudeExeNames);

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

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
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
