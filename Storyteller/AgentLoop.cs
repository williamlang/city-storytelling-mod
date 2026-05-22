using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Colossal.Logging;

namespace CityStoryMod.Storyteller
{
    // Provider-agnostic hop loop. Given a Conversation (whichever provider it
    // wraps), this loads the city's CLAUDE.md as the system prompt and runs
    // the agent loop:
    //
    //   SendInitial → assistant turn → execute tools → SendToolResults → repeat
    //
    // until the assistant returns RequiresToolResponse=false or the hop cap is
    // hit. Token usage and files-written counters are aggregated and logged.
    //
    // Two entry points: RunCommandAsync loads the user prompt from a slash
    // command file under .claude/commands/, while RunPromptAsync uses a raw
    // string (used by the in-game prompt panel for free-form input).
    public static class AgentLoop
    {
        const int MaxHops = 50;

        public static Task<RunResult> RunCommandAsync(
            Conversation conv,
            string cityDir,
            string commandName,
            ILog log,
            CancellationToken ct)
        {
            string cityDirFull = Path.GetFullPath(cityDir ?? "");
            string commandPath = Path.Combine(cityDirFull, ".claude", "commands", commandName + ".md");
            if (!File.Exists(commandPath))
                return Task.FromResult(RunResult.Failed($"Missing command file: {commandPath}"));

            string command = File.ReadAllText(commandPath);
            string snapshotHint = DescribeLatestSnapshot(cityDirFull);
            string userPrompt = $"{command}\n\n---\n\n{snapshotHint}";
            return RunPromptAsync(conv, cityDir, userPrompt, log, ct);
        }

        public static async Task<RunResult> RunPromptAsync(
            Conversation conv,
            string cityDir,
            string userPrompt,
            ILog log,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(cityDir) || !Directory.Exists(cityDir))
                return RunResult.Failed($"City dir not found: {cityDir}");
            if (string.IsNullOrWhiteSpace(userPrompt))
                return RunResult.Failed("Empty prompt.");

            string cityDirFull = Path.GetFullPath(cityDir);
            string systemPath = Path.Combine(cityDirFull, "CLAUDE.md");
            if (!File.Exists(systemPath)) return RunResult.Failed($"Missing CLAUDE.md: {systemPath}");

            string system = File.ReadAllText(systemPath);
            ToolExecutor tools = new ToolExecutor(cityDirFull);

            AssistantTurn turn = await conv.SendInitial(system, userPrompt, ToolSchemas.Default, ct);
            LogHop(log, 0, turn.Usage);

            int hop = 0;
            for (; hop < MaxHops; hop++)
            {
                ct.ThrowIfCancellationRequested();
                if (!turn.RequiresToolResponse) break;

                List<ToolResult> results = new List<ToolResult>(turn.ToolCalls.Count);
                foreach (ToolCall call in turn.ToolCalls)
                {
                    string content;
                    bool isError = false;
                    try
                    {
                        content = tools.Execute(call.Name, call.Input);
                    }
                    catch (Exception ex)
                    {
                        content = $"Error: {ex.Message}";
                        isError = true;
                        log.Warn($"Tool '{call.Name}' failed: {ex.Message}");
                    }
                    results.Add(new ToolResult { ToolUseId = call.Id, Content = content, IsError = isError });
                }

                conv.NotifyToolResults(results);
                turn = await conv.SendToolResults(results, ct);
                LogHop(log, hop + 1, turn.Usage);
            }

            // Token totals come straight from the conversation — each provider's
            // BuildTurn() call accumulates into conv.TotalUsage, so we don't sum
            // here and risk drifting from the per-turn values.
            TokenUsage totalUsage = conv.TotalUsage;
            log.Info($"Token totals: input={totalUsage.InputTokens} (cache read={totalUsage.CacheReadTokens}, write={totalUsage.CacheWriteTokens}), output={totalUsage.OutputTokens}");
            if (hop >= MaxHops)
            {
                log.Warn($"Hop cap ({MaxHops}) reached; ending run.");
                return RunResult.Ok(tools.FilesWritten)
                    .WithMessage($"Hop cap reached after {hop} hops")
                    .WithUsage(totalUsage);
            }
            log.Info($"Run complete: {tools.FilesWritten} file(s) written across {hop + 1} hop(s).");
            return RunResult.Ok(tools.FilesWritten).WithUsage(totalUsage);
        }

        static void LogHop(ILog log, int hop, TokenUsage u)
        {
            log.Info($"Hop {hop}: input={u.InputTokens} (cache read={u.CacheReadTokens}, write={u.CacheWriteTokens}), output={u.OutputTokens}");
        }

        static string DescribeLatestSnapshot(string cityDirFull)
        {
            string snapDir = Path.Combine(cityDirFull, "snapshots");
            if (!Directory.Exists(snapDir)) return "No snapshots have been exported yet for this city.";
            string[] files = Directory.GetFiles(snapDir, "snapshot-*.json");
            if (files.Length == 0) return "No snapshots have been exported yet for this city.";
            Array.Sort(files);
            string latest = files[files.Length - 1];
            string rel = latest.Substring(cityDirFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/');
            return $"Latest snapshot: {rel}\nUse the read_file tool to load it.";
        }
    }
}
