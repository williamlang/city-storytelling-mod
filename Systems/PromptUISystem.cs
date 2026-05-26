using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CityStoryMod.Storyteller;
using Colossal.Logging;
using Colossal.UI.Binding;
using Game;
using Game.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityStoryMod.Systems
{
    // Bridges the in-game React panel (UI/src/mods/promptWindow/*) to the C#
    // storyteller dispatcher. Registers five ValueBindings (read by React via
    // `useValue`) and three TriggerBindings (called by React via `trigger`):
    //
    //   messages          — JSON array of ChatMessage rows (user / assistant)
    //   isRunning         — true while a run is in flight
    //   tokenSummary      — short formatted token-usage line, updated per turn
    //   lastError         — non-empty when the most recent run failed
    //   availableCommands — JSON array of SlashCommand rows scanned from the
    //                       city's .claude/commands/*.md (name + description)
    //
    //   submitPrompt(text)   — start a free-form run with the given prompt
    //   cancelRun()          — cancel the in-flight run
    //   clearMessages()      — wipe the chat history (UI-side only)
    //
    // Conversation events fire on the dispatcher's worker thread; this system
    // buffers them on a ConcurrentQueue and drains onto the main thread in
    // OnUpdate before touching the value bindings (which require main-thread
    // access).
    public partial class PromptUISystem : UISystemBase
    {
        const string Group = "CityStoryMod";
        static readonly ILog _log = Mod.Log;

        ValueBinding<string> _messagesBinding;
        ValueBinding<bool> _isRunningBinding;
        ValueBinding<string> _tokenSummaryBinding;
        ValueBinding<string> _lastErrorBinding;
        ValueBinding<string> _availableCommandsBinding;
        ValueBinding<string> _canonTreeBinding;
        ValueBinding<bool> _cartoExportingBinding;
        ValueBinding<bool> _cartoAvailableBinding;

        // Caches the city dir we last scanned for slash commands / canon
        // tree. OnUpdate rescans (cheap directory listing) when
        // LastExportedCityDir changes, so the lists refresh as soon as the
        // first export happens for a city — no file watcher needed.
        string _commandsScannedCityDir;
        string _canonScannedCityDir;

        readonly List<ChatMessage> _messages = new List<ChatMessage>();
        readonly ConcurrentQueue<ChatMessage> _pendingMessages = new ConcurrentQueue<ChatMessage>();

        // Per-run accumulator updated off-thread by event handlers, drained
        // onto the binding in OnUpdate. Reset on each new run start.
        TokenUsage _liveUsage;
        bool _pendingUsageUpdate;
        bool _pendingRunStart;
        bool _pendingRunFinish;
        RunResult _pendingFinishResult;

        public override GameMode gameMode => GameMode.Game | GameMode.Editor | GameMode.MainMenu;

        protected override void OnCreate()
        {
            base.OnCreate();

            _messagesBinding = new ValueBinding<string>(Group, "messages", "[]");
            _isRunningBinding = new ValueBinding<bool>(Group, "isRunning", false);
            _tokenSummaryBinding = new ValueBinding<string>(Group, "tokenSummary", "");
            _lastErrorBinding = new ValueBinding<string>(Group, "lastError", "");
            _availableCommandsBinding = new ValueBinding<string>(Group, "availableCommands", "[]");
            _canonTreeBinding = new ValueBinding<string>(Group, "canonTree", "{}");
            _cartoExportingBinding = new ValueBinding<bool>(Group, "cartoExporting", false);
            _cartoAvailableBinding = new ValueBinding<bool>(Group, "cartoAvailable", false);
            AddBinding(_messagesBinding);
            AddBinding(_isRunningBinding);
            AddBinding(_tokenSummaryBinding);
            AddBinding(_lastErrorBinding);
            AddBinding(_availableCommandsBinding);
            AddBinding(_canonTreeBinding);
            AddBinding(_cartoExportingBinding);
            AddBinding(_cartoAvailableBinding);

            AddBinding(new TriggerBinding<string>(Group, "submitPrompt", OnSubmitPrompt));
            AddBinding(new TriggerBinding(Group, "cancelRun", OnCancelRun));
            AddBinding(new TriggerBinding(Group, "clearMessages", OnClearMessages));
            AddBinding(new TriggerBinding(Group, "refreshGeography", OnRefreshGeography));
            AddBinding(new TriggerBinding<string>(Group, "uiLog", OnUILog));

            StorytellerDispatcher d = Mod.Storyteller;
            if (d != null)
            {
                d.RunStarted += OnRunStarted;
                d.AssistantTurn += OnAssistantTurn;
                d.ToolResults += OnToolResults;
                d.RunFinished += OnRunFinished;
            }
            else
            {
                _log.Warn("PromptUISystem: Mod.Storyteller is null at OnCreate; dispatcher events will not be observed.");
            }
        }

        // ---- Dispatcher event handlers (background thread) ----

        void OnRunStarted(string runName)
        {
            _liveUsage = default;
            _pendingRunStart = true;
        }

        void OnAssistantTurn(AssistantTurn turn)
        {
            // Token totals always accumulate. We now surface tool-use turns
            // too — when the model emits no prose but called tools, show a
            // condensed `[tool calls: Read(canon/city.md), ...]` line so the
            // player can see what the agent is doing. Previously these turns
            // were dropped entirely, which hid agent activity the player
            // wanted to see (especially during multi-step commands like
            // /new-city). Visibility is "for now" — a future pane could
            // separate tool chatter from prose without the prefix hack.
            string text = turn.TextContent;
            if (string.IsNullOrWhiteSpace(text) && turn.ToolCalls != null && turn.ToolCalls.Count > 0)
            {
                text = FormatToolCallsLine(turn.ToolCalls);
            }
            if (!string.IsNullOrWhiteSpace(text))
            {
                _pendingMessages.Enqueue(new ChatMessage
                {
                    role = "assistant",
                    text = text,
                });
            }
            _liveUsage = _liveUsage + turn.Usage;
            _pendingUsageUpdate = true;
        }

        // Tool results now surface into the chat (used to be dropped). Each
        // result becomes its own assistant-role message, prefixed with
        // `[tool result]` or `[tool error]` and truncated past a generous
        // cap so multi-KB file reads don't blow up the conversation view.
        // The full content is still in the mod log.
        void OnToolResults(IReadOnlyList<ToolResult> results)
        {
            if (results == null) return;
            foreach (ToolResult r in results)
            {
                string content = r.Content ?? "";
                const int MAX = 1500;
                if (content.Length > MAX)
                {
                    content = content.Substring(0, MAX) + $"\n… [+{content.Length - MAX:N0} chars truncated]";
                }
                string prefix = r.IsError ? "[tool error]" : "[tool result]";
                _pendingMessages.Enqueue(new ChatMessage
                {
                    role = "assistant",
                    text = $"{prefix} {content}",
                });
            }
        }

        // Builds a one-line summary of a tool-use-only assistant turn —
        // e.g. "[tool calls: Read(canon/city.md), Read(snapshots/snapshot-…)]".
        // Picks the most identifying input field per tool (file_path, path,
        // command, pattern, url) and truncates anything long.
        static string FormatToolCallsLine(IReadOnlyList<ToolCall> calls)
        {
            var sb = new StringBuilder();
            sb.Append("[tool calls: ");
            for (int i = 0; i < calls.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                ToolCall c = calls[i];
                sb.Append(c.Name ?? "?");
                string summary = SummarizeToolInput(c.Input);
                if (!string.IsNullOrEmpty(summary))
                {
                    sb.Append('(');
                    sb.Append(summary);
                    sb.Append(')');
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        static string SummarizeToolInput(JObject input)
        {
            if (input == null) return "";
            foreach (string key in _toolInputIdentifyingKeys)
            {
                JToken v = input[key];
                if (v == null || v.Type == JTokenType.Null) continue;
                string s = v.ToString();
                if (s.Length > 80) s = s.Substring(0, 77) + "…";
                return s;
            }
            return "";
        }

        static readonly string[] _toolInputIdentifyingKeys =
        {
            "file_path", "path", "command", "pattern", "url",
        };

        void OnRunFinished(RunResult result)
        {
            _pendingRunFinish = true;
            _pendingFinishResult = result;
            // The agent may have written files that change which commands
            // apply (e.g. /new-city writing canon/playthrough-premise.md
            // should hide its own button) or added new canon entries.
            // Invalidate both scan caches so the next OnUpdate tick re-walks.
            _commandsScannedCityDir = null;
            _canonScannedCityDir = null;
        }

        // ---- Drain (main thread) ----

        protected override void OnUpdate()
        {
            bool messagesDirty = false;
            while (_pendingMessages.TryDequeue(out ChatMessage msg))
            {
                _messages.Add(msg);
                messagesDirty = true;
            }
            if (messagesDirty)
            {
                _messagesBinding.Update(JsonConvert.SerializeObject(_messages));
            }

            // Refresh available commands + canon tree once per city, and
            // again whenever a run finishes (the cache is invalidated in
            // OnRunFinished). Cheap when the city hasn't changed (one string
            // compare).
            string cityDir = Mod.LastExportedCityDir;
            if (cityDir != _commandsScannedCityDir)
            {
                _commandsScannedCityDir = cityDir;
                _availableCommandsBinding.Update(ScanAvailableCommands(cityDir));
            }
            if (cityDir != _canonScannedCityDir)
            {
                _canonScannedCityDir = cityDir;
                _canonTreeBinding.Update(ScanCanonTree(cityDir));
            }

            // Reflect Carto availability into the UI. CartoBridge.IsAvailable
            // resolves lazily on first access, so we let the bridge handle the
            // resolve and just mirror its state. Cheap — one property read.
            bool cartoAvail = CartoBridge.IsAvailable;
            if (cartoAvail != _cartoAvailableBinding.value)
            {
                _cartoAvailableBinding.Update(cartoAvail);
            }

            if (_pendingRunStart)
            {
                _pendingRunStart = false;
                _isRunningBinding.Update(true);
                _lastErrorBinding.Update("");
                _tokenSummaryBinding.Update("");
            }

            if (_pendingUsageUpdate)
            {
                _pendingUsageUpdate = false;
                _tokenSummaryBinding.Update(FormatTokens(_liveUsage));
            }

            if (_pendingRunFinish)
            {
                _pendingRunFinish = false;
                _isRunningBinding.Update(false);
                if (_pendingFinishResult.TotalUsage.InputTokens + _pendingFinishResult.TotalUsage.OutputTokens > 0)
                {
                    _liveUsage = _pendingFinishResult.TotalUsage;
                    _tokenSummaryBinding.Update(FormatTokens(_liveUsage));
                }
                _lastErrorBinding.Update(_pendingFinishResult.Success ? "" : (_pendingFinishResult.Message ?? "Run failed"));
            }
        }

        // ---- Trigger handlers (called from JS, main thread) ----

        // Slash commands that benefit from fresh spatial data. When the user
        // submits one of these, we kick off a Carto export in parallel with the
        // agent run — Carto's ~hundreds-of-ms write completes well before the
        // agent's first LLM round-trip returns, so by the time the agent reaches
        // for the carto/ files via a tool call they're guaranteed to be fresh.
        static readonly HashSet<string> _cartoRefreshingCommands = new HashSet<string>
        {
            "new-city",
            "session-end",
        };

        void OnSubmitPrompt(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return;
            _pendingMessages.Enqueue(new ChatMessage { role = "user", text = prompt });

            StorytellerDispatcher dispatcher = Mod.Storyteller;
            if (dispatcher == null)
            {
                _log.Warn("PromptUISystem: dispatcher not initialized — prompt dropped.");
                return;
            }

            string command = ExtractSlashCommand(prompt);
            if (command != null && _cartoRefreshingCommands.Contains(command))
            {
                _log.Info($"OnSubmitPrompt: /{command} triggers a Carto refresh.");
                World.GetExistingSystemManaged<ExportSystem>()?.RequestCartoExport();
            }
            // /new-city also captures a map screenshot. Better trigger than
            // the save-load edge — at /new-city time the framebuffer is
            // fully rendered (the player has been looking at the city) and
            // the player chose this moment deliberately. Queued with a tiny
            // delay so the storyteller window's submit-handler UI flash
            // doesn't dominate the shot.
            if (command == "new-city")
            {
                _log.Info("OnSubmitPrompt: /new-city triggers a map screenshot capture.");
                World.GetExistingSystemManaged<ExportSystem>()?.RequestScreenshotCaptureForCurrentCity(ticksToDelay: 2);
            }

            StorytellerDispatcher.RunFunc runFunc = StorytellerRun.BuildFreeForm(prompt, _log);
            dispatcher.Start("ui-prompt", runFunc);
        }

        // Returns the command name (without leading "/") if the prompt starts
        // with a slash command, otherwise null. Case-insensitive — matches the
        // lookup against the registered set.
        static string ExtractSlashCommand(string prompt)
        {
            string trimmed = prompt.TrimStart();
            if (trimmed.Length < 2 || trimmed[0] != '/') return null;
            int end = 1;
            while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end])) end++;
            if (end <= 1) return null;
            return trimmed.Substring(1, end - 1).ToLowerInvariant();
        }

        void OnCancelRun()
        {
            Mod.Storyteller?.Cancel();
        }

        void OnClearMessages()
        {
            _messages.Clear();
            while (_pendingMessages.TryDequeue(out _)) { }
            _messagesBinding.Update("[]");
            _liveUsage = default;
            _tokenSummaryBinding.Update("");
            _lastErrorBinding.Update("");
        }

        // Public entry point for outside-the-UI callers. Cancels any
        // in-flight run before wiping history so we don't leave a pending
        // response that would land into the cleared transcript. Used by
        // ExportSystem on the save-load edge: a fresh save = a different
        // city's worth of context, so the chat history should reset.
        public void ClearChatHistory(string reason)
        {
            try { Mod.Storyteller?.Cancel(); } catch { /* cancel best-effort */ }
            OnClearMessages();
            _log.Info($"Cleared Ghostwriter chat history ({reason}).");
        }

        // Surface a non-fatal warning in the storyteller window's error slot.
        // Used by ExportSystem to flag conflicts (e.g. city rename collision)
        // that the player should know about but that don't stop the mod from
        // continuing. Cleared by the player's next "clear chat" action.
        public void ShowAlert(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            _lastErrorBinding.Update(message);
            _log.Warn($"UI alert: {message}");
        }

        void OnRefreshGeography()
        {
            _log.Info("OnRefreshGeography trigger received from UI.");
            World.GetExistingSystemManaged<ExportSystem>()?.RequestCartoExport();
        }

        // JS-side diagnostic pipe. The UI calls trigger("CityStoryMod",
        // "uiLog", message); we relay each message to the mod log so
        // browser-style console output from Coherent (which has no
        // user-accessible devtools) lands in Logs/CityStoryMod.log next
        // to everything else.
        void OnUILog(string message)
        {
            _log.Info("[UI] " + (message ?? ""));
        }

        static string FormatTokens(TokenUsage u)
        {
            int total = u.InputTokens + u.OutputTokens;
            if (total == 0) return "";
            string s = $"in {u.InputTokens} • out {u.OutputTokens}";
            if (u.CacheReadTokens > 0 || u.CacheWriteTokens > 0)
                s += $" • cache r{u.CacheReadTokens}/w{u.CacheWriteTokens}";
            return s;
        }

        // Walks <cityDir>/.claude/commands/*.md, extracts (name, description)
        // for each, applies the per-command applicability rule, returns a
        // JSON array sorted alphabetically by name. Returns "[]" when the
        // commands dir doesn't exist (no city exported yet).
        //
        // Re-scans on every dispatcher RunFinished so flags set by the run
        // (e.g. /new-city flipping settings.bootstrapped) take effect on
        // the next tick.
        static string ScanAvailableCommands(string cityDir)
        {
            if (string.IsNullOrEmpty(cityDir)) return "[]";
            string commandsDir = Path.Combine(cityDir, ".claude", "commands");
            if (!Directory.Exists(commandsDir)) return "[]";

            JObject settings = ReadCitySettings(cityDir);

            var commands = new List<SlashCommand>();
            foreach (string path in Directory.GetFiles(commandsDir, "*.md"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(name)) continue;
                if (!IsCommandApplicable(name, settings)) continue;

                string description = null;
                int order = int.MaxValue;
                try
                {
                    string content = File.ReadAllText(path);
                    description = TextUtils.GetFrontmatterField(content, "description");
                    string orderRaw = TextUtils.GetFrontmatterField(content, "order");
                    if (!string.IsNullOrEmpty(orderRaw) && int.TryParse(orderRaw, out int parsed))
                        order = parsed;
                }
                catch (Exception ex)
                {
                    _log.Warn($"PromptUISystem: failed to read command file {path}: {ex.Message}");
                }
                commands.Add(new SlashCommand { name = name, description = description ?? "", order = order });
            }
            // Primary sort: `order:` frontmatter ascending (commands declare
            // their own position). Fallback: alphabetical name for commands
            // that omit the field, so they don't bunch arbitrarily at the
            // tail when order ties.
            commands.Sort((a, b) =>
            {
                int c = a.order.CompareTo(b.order);
                return c != 0 ? c : string.CompareOrdinal(a.name, b.name);
            });
            return JsonConvert.SerializeObject(commands);
        }

        // Per-command applicability rules driven by per-city settings.json
        // flags (see template/CLAUDE.md → City settings). Hardcoded because
        // the ruleset is small; revisit if it grows beyond a few commands.
        //
        //   /new-city  → hide when settings.bootstrapped is true. /new-city
        //                flips that flag to true at the end of its own run,
        //                so it disappears from the toolbar after running.
        static bool IsCommandApplicable(string name, JObject settings)
        {
            switch (name)
            {
                case "new-city":
                    return !(settings?["bootstrapped"]?.Value<bool?>() ?? false);
                default:
                    return true;
            }
        }

        // Canon-browser scan order. Foundational `canon/` first, then the
        // entity types in roughly the order a player builds up a city. The
        // `secrets/` dir is gated by settings.secrets_visibility — see
        // ScanCanonTree below.
        static readonly string[] s_CanonSubdirs =
        {
            "canon", "characters", "companies", "places", "factions",
            "events", "stories", "sessions", "secrets",
        };

        // Walks each canon subdir under cityDir, lists *.md files, returns
        // a JSON object of {subdir → [{name, path, content}, ...]} for the
        // React sidebar. File contents are inlined (eager-load) so the JS
        // side can open multiple file modals at once without a per-modal
        // async fetch. Per-file content is capped at 20KB; larger files
        // get a truncated tail marker. Total JSON for a typical city is
        // <300KB — well within what a ValueBinding handles per update.
        //
        // Skips secrets/ entirely when settings.json's secrets_visibility
        // is anything other than "shown" (default "hidden" — see
        // template/CLAUDE.md → Secrets). Empty subdirs are dropped so the
        // sidebar doesn't show headers with no entries.
        static string ScanCanonTree(string cityDir)
        {
            if (string.IsNullOrEmpty(cityDir)) return "{}";
            JObject settings = ReadCitySettings(cityDir);
            bool showSecrets = string.Equals(
                (string)settings?["secrets_visibility"], "shown", StringComparison.Ordinal);

            var tree = new System.Collections.Specialized.OrderedDictionary();
            foreach (string sub in s_CanonSubdirs)
            {
                if (sub == "secrets" && !showSecrets) continue;
                string dir = Path.Combine(cityDir, sub);
                if (!Directory.Exists(dir)) continue;
                var entries = new List<object>();
                string[] files = Directory.GetFiles(dir, "*.md");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string path in files)
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    string rel = sub + "/" + Path.GetFileName(path);
                    string content = SafeReadFile(path);
                    entries.Add(new { name, path = rel, content });
                }
                if (entries.Count > 0) tree[sub] = entries;
            }
            return JsonConvert.SerializeObject(tree);
        }

        // 20KB per-file cap with a truncation tail. Errors return a one-line
        // placeholder so the JSON stays well-formed and the React side sees
        // something useful in the modal instead of an empty body.
        static string SafeReadFile(string path)
        {
            try
            {
                string content = File.ReadAllText(path);
                const int maxLen = 20000;
                if (content.Length > maxLen)
                    content = content.Substring(0, maxLen) + "\n\n*[truncated — file is larger than 20KB]*";
                return content;
            }
            catch (Exception ex)
            {
                _log.Warn($"PromptUISystem: failed to read canon file {path}: {ex.Message}");
                return $"*[error reading file: {ex.Message}]*";
            }
        }

        // Reads <cityDir>/settings.json into a JObject. Returns null when
        // the file is missing or unparseable — callers treat that as "no
        // flags set" (commands fall through to default-applicable).
        static JObject ReadCitySettings(string cityDir)
        {
            if (string.IsNullOrEmpty(cityDir)) return null;
            string path = Path.Combine(cityDir, "settings.json");
            if (!File.Exists(path)) return null;
            try { return JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                _log.Warn($"PromptUISystem: failed to parse settings.json at {path}: {ex.Message}");
                return null;
            }
        }

        // Called by ExportSystem to drive the storyteller window's "spatial
        // export in progress" indicator. Main-thread only; the caller flips
        // the binding to true one tick before invoking Carto's synchronous
        // export, then back to false after it returns.
        public void SetCartoExporting(bool exporting)
        {
            _cartoExportingBinding?.Update(exporting);
        }
    }

    // Wire-format for a chat message — serialized to JSON for the React UI.
    // Lowercase field names match the React component's TypeScript interface.
    [Serializable]
    public class ChatMessage
    {
        public string role;   // "user" | "assistant"
        public string text;
    }

    [Serializable]
    public class SlashCommand
    {
        public string name;        // filename stem, e.g. "story-driven"
        public string description; // `description:` frontmatter field, or ""
        public int order;          // `order:` frontmatter field, or int.MaxValue when missing
    }
}
