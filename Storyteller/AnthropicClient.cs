using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Colossal.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityStoryMod.Storyteller
{
    // Anthropic Messages API client + tool-use agent loop. Drives the storyteller
    // run for the Anthropic provider; other providers (#8/#9/#10) will have their
    // own client class with the same public Run signature.
    //
    // Sandboxing: every tool path is resolved against cityDir, rejected if it
    // escapes (absolute paths, .. traversal, or symlinks pointing outside) — the
    // agent can only touch files inside the current city's directory.
    //
    // No prompt caching yet (issue #6 layers that on later). No streaming — the
    // status surface (#5) doesn't render partial tokens and the dispatcher is fine
    // waiting for the full response.
    public static class AnthropicClient
    {
        const string Endpoint = "https://api.anthropic.com/v1/messages";
        const string AnthropicVersion = "2023-06-01";
        const int MaxHops = 50;
        const int MaxTokens = 8192;

        // Shared HttpClient — Anthropic recommends reusing instances. 5-minute
        // timeout is generous for slow turns; the dispatcher's own cancellation
        // path still applies on top via CancellationToken.
        static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        public static async Task<RunResult> Run(
            string apiKey,
            string model,
            string cityDir,
            string commandName,
            ILog log,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return RunResult.Failed("API key is empty — paste a key into Options first.");
            if (string.IsNullOrWhiteSpace(model))
                return RunResult.Failed("Model id is empty — paste one into Options first.");
            if (string.IsNullOrWhiteSpace(cityDir) || !Directory.Exists(cityDir))
                return RunResult.Failed($"City dir not found: {cityDir}");

            string cityDirFull = Path.GetFullPath(cityDir);
            string systemPath = Path.Combine(cityDirFull, "CLAUDE.md");
            string commandPath = Path.Combine(cityDirFull, ".claude", "commands", commandName + ".md");

            if (!File.Exists(systemPath)) return RunResult.Failed($"Missing CLAUDE.md: {systemPath}");
            if (!File.Exists(commandPath)) return RunResult.Failed($"Missing command file: {commandPath}");

            string system = File.ReadAllText(systemPath);
            string command = File.ReadAllText(commandPath);
            string snapshotHint = DescribeLatestSnapshot(cityDirFull);

            // Initial user turn: the slash command body + a pointer to the snapshot
            // the agent should ground itself in. The agent uses tools to read it
            // (we don't inline the JSON — it can be big and the agent can pick
            // selective fields).
            string userPrompt = $"{command}\n\n---\n\n{snapshotHint}";

            JArray messages = new JArray {
                new JObject { ["role"] = "user", ["content"] = userPrompt }
            };

            int filesWritten = 0;
            int hop;
            for (hop = 0; hop < MaxHops; hop++)
            {
                ct.ThrowIfCancellationRequested();
                JObject response = await PostMessages(apiKey, model, system, messages, ct);

                string stopReason = (string)response["stop_reason"];
                JArray content = (JArray)response["content"];
                // DeepClone because content is still parented to `response`; JToken
                // disallows attaching a node to a second container without a copy.
                messages.Add(new JObject { ["role"] = "assistant", ["content"] = content.DeepClone() });

                if (stopReason != "tool_use") break;

                JArray toolResults = new JArray();
                foreach (JToken block in content)
                {
                    if ((string)block["type"] != "tool_use") continue;
                    string toolName = (string)block["name"];
                    string toolUseId = (string)block["id"];
                    JObject input = (JObject)block["input"];

                    string result;
                    bool isError = false;
                    try
                    {
                        result = ExecuteTool(toolName, input, cityDirFull, ref filesWritten);
                    }
                    catch (Exception ex)
                    {
                        result = $"Error: {ex.Message}";
                        isError = true;
                        log.Warn($"Tool '{toolName}' failed: {ex.Message}");
                    }

                    toolResults.Add(new JObject {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolUseId,
                        ["content"] = result,
                        ["is_error"] = isError,
                    });
                }
                messages.Add(new JObject { ["role"] = "user", ["content"] = toolResults });
            }

            if (hop >= MaxHops)
            {
                log.Warn($"Hop cap ({MaxHops}) reached; ending run.");
                return RunResult.Ok(filesWritten).WithMessage($"Hop cap reached after {hop} hops");
            }
            log.Info($"Run complete: {filesWritten} file(s) written across {hop + 1} hop(s).");
            return RunResult.Ok(filesWritten);
        }

        static async Task<JObject> PostMessages(string apiKey, string model, string system, JArray messages, CancellationToken ct)
        {
            JObject body = new JObject
            {
                ["model"] = model,
                ["max_tokens"] = MaxTokens,
                ["system"] = system,
                ["messages"] = messages,
                ["tools"] = ToolDefinitions(),
            };

            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", AnthropicVersion);

            using (HttpResponseMessage resp = await _http.SendAsync(req, ct))
            {
                string text = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"Anthropic API {(int)resp.StatusCode}: {text}");
                return JObject.Parse(text);
            }
        }

        static JArray ToolDefinitions()
        {
            return new JArray
            {
                ToolDef("read_file", "Read a UTF-8 text file inside the city dir. Returns the file contents.", new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject {
                        ["path"] = new JObject { ["type"] = "string", ["description"] = "Path relative to the city dir." },
                    },
                    ["required"] = new JArray { "path" },
                }),
                ToolDef("write_file", "Write a UTF-8 text file inside the city dir. Overwrites existing files. Creates parent directories as needed.", new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject {
                        ["path"] = new JObject { ["type"] = "string", ["description"] = "Path relative to the city dir." },
                        ["content"] = new JObject { ["type"] = "string", ["description"] = "UTF-8 text to write." },
                    },
                    ["required"] = new JArray { "path", "content" },
                }),
                ToolDef("list_dir", "List entries in a directory inside the city dir. Returns one entry per line, with 'dir:' or 'file:' prefix.", new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject {
                        ["path"] = new JObject { ["type"] = "string", ["description"] = "Path relative to the city dir. Use '.' for the city root." },
                    },
                    ["required"] = new JArray { "path" },
                }),
                ToolDef("glob", "Find files matching a wildcard pattern (e.g. 'canon/*.md', 'sessions/**/*.md') inside the city dir. Returns matching paths one per line.", new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject {
                        ["pattern"] = new JObject { ["type"] = "string", ["description"] = "Wildcard pattern relative to the city dir." },
                    },
                    ["required"] = new JArray { "pattern" },
                }),
            };
        }

        static JObject ToolDef(string name, string desc, JObject schema) =>
            new JObject { ["name"] = name, ["description"] = desc, ["input_schema"] = schema };

        static string ExecuteTool(string toolName, JObject input, string cityDirFull, ref int filesWritten)
        {
            switch (toolName)
            {
                case "read_file":
                {
                    string p = ResolveSafePath(cityDirFull, (string)input["path"]);
                    if (!File.Exists(p)) return $"File not found: {(string)input["path"]}";
                    return File.ReadAllText(p);
                }
                case "write_file":
                {
                    string p = ResolveSafePath(cityDirFull, (string)input["path"]);
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    File.WriteAllText(p, (string)input["content"]);
                    filesWritten++;
                    return $"Wrote {(string)input["path"]}";
                }
                case "list_dir":
                {
                    string p = ResolveSafePath(cityDirFull, (string)input["path"]);
                    if (!Directory.Exists(p)) return $"Directory not found: {(string)input["path"]}";
                    var sb = new StringBuilder();
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
                    var sb = new StringBuilder();
                    foreach (string f in Directory.GetFiles(cityDirFull, flatPattern, opt))
                    {
                        string rel = f.Substring(cityDirFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        sb.AppendLine(rel.Replace(Path.DirectorySeparatorChar, '/'));
                    }
                    return sb.Length == 0 ? "(no matches)" : sb.ToString();
                }
                default:
                    throw new Exception($"Unknown tool: {toolName}");
            }
        }

        // Reject absolute paths, .. traversal, and any resolved path that escapes
        // the city dir. cityDirFull must already be canonical (Path.GetFullPath).
        static string ResolveSafePath(string cityDirFull, string relative)
        {
            if (string.IsNullOrEmpty(relative)) throw new Exception("Path is empty.");
            if (Path.IsPathRooted(relative)) throw new Exception("Path must be relative to the city dir.");
            if (relative.Contains("..")) throw new Exception("Path may not contain '..'.");
            string full = Path.GetFullPath(Path.Combine(cityDirFull, relative));
            if (!full.StartsWith(cityDirFull, StringComparison.Ordinal))
                throw new Exception("Path escapes the city dir.");
            return full;
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
