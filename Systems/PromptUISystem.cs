using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CityStoryMod.Storyteller;
using Colossal.Logging;
using Colossal.UI.Binding;
using Game;
using Game.UI;
using Newtonsoft.Json;

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

        // Caches the city dir we last scanned for slash commands. OnUpdate
        // rescans (cheap directory listing) when LastExportedCityDir changes,
        // so command list refreshes as soon as the first export happens for a
        // city — no separate file watcher needed.
        string _commandsScannedCityDir;

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
            AddBinding(_messagesBinding);
            AddBinding(_isRunningBinding);
            AddBinding(_tokenSummaryBinding);
            AddBinding(_lastErrorBinding);
            AddBinding(_availableCommandsBinding);

            AddBinding(new TriggerBinding<string>(Group, "submitPrompt", OnSubmitPrompt));
            AddBinding(new TriggerBinding(Group, "cancelRun", OnCancelRun));
            AddBinding(new TriggerBinding(Group, "clearMessages", OnClearMessages));

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
            // Token totals always accumulate; only enqueue a chat row when the
            // model actually said something. Pure tool-use turns (no prose)
            // would otherwise show as empty bubbles and the file-system tool
            // chatter is noise the player doesn't care about.
            if (!string.IsNullOrWhiteSpace(turn.TextContent))
            {
                _pendingMessages.Enqueue(new ChatMessage
                {
                    role = "assistant",
                    text = turn.TextContent,
                });
            }
            _liveUsage = _liveUsage + turn.Usage;
            _pendingUsageUpdate = true;
        }

        // Tool results are intentionally not surfaced into the chat — they
        // contain glob output, file contents, and other internal tool chatter
        // that clutters the conversation view. The event handler is kept (and
        // wired) so future features (e.g. a separate "activity" pane) can
        // observe them without a Conversation refactor.
        void OnToolResults(IReadOnlyList<ToolResult> results)
        {
        }

        void OnRunFinished(RunResult result)
        {
            _pendingRunFinish = true;
            _pendingFinishResult = result;
            // The agent may have written files that change which commands
            // apply (e.g. /new-city writing canon/playthrough-premise.md
            // should hide its own button). Invalidate the scan cache so the
            // next OnUpdate tick re-walks the commands dir.
            _commandsScannedCityDir = null;
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

            // Refresh available commands once per city. Cheap (one string
            // compare in the common case where the city hasn't changed).
            string cityDir = Mod.LastExportedCityDir;
            if (cityDir != _commandsScannedCityDir)
            {
                _commandsScannedCityDir = cityDir;
                _availableCommandsBinding.Update(ScanAvailableCommands(cityDir));
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
            StorytellerDispatcher.RunFunc runFunc = StorytellerRun.BuildFreeForm(prompt, _log);
            dispatcher.Start("ui-prompt", runFunc);
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
        // for each, filters out commands that don't apply to the current state
        // of the city dir per their own frontmatter rules, returns a JSON
        // array sorted alphabetically by name. Returns "[]" when the dir
        // doesn't exist (no city exported yet).
        //
        // Applicability rules live in each command file's YAML frontmatter:
        //   hide_when_exists: <comma-separated paths relative to city dir>
        //     — hide when any of those files exist
        //   hide_when_dir_nonempty: <comma-separated dirs relative to city dir>
        //     — hide when any of those dirs contains any file
        //
        // Declarative-in-the-command-file lets each command own its own
        // visibility — no central hardcoded registry. Re-scans after every
        // storyteller run so newly-written marker files take effect on the
        // next tick.
        static string ScanAvailableCommands(string cityDir)
        {
            if (string.IsNullOrEmpty(cityDir)) return "[]";
            string commandsDir = Path.Combine(cityDir, ".claude", "commands");
            if (!Directory.Exists(commandsDir)) return "[]";

            var commands = new List<SlashCommand>();
            foreach (string path in Directory.GetFiles(commandsDir, "*.md"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(name)) continue;

                string content;
                try { content = File.ReadAllText(path); }
                catch (Exception ex)
                {
                    _log.Warn($"PromptUISystem: failed to read command file {path}: {ex.Message}");
                    continue;
                }

                if (!IsCommandApplicable(content, cityDir)) continue;

                string description = TextUtils.GetFrontmatterField(content, "description") ?? "";
                commands.Add(new SlashCommand { name = name, description = description });
            }
            commands.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return JsonConvert.SerializeObject(commands);
        }

        // Evaluates a command file's hide_when_* frontmatter against the
        // current city dir filesystem state. Returns false (hide) when any
        // listed path under hide_when_exists exists or any listed dir under
        // hide_when_dir_nonempty has at least one file. Returns true (show)
        // when no rules are declared or all rules pass.
        //
        // Paths in frontmatter are relative to cityDir. Forward slashes work
        // even on Windows because Path.Combine normalizes separators.
        static bool IsCommandApplicable(string commandFileContent, string cityDir)
        {
            string[] hideWhenExists = TextUtils.GetFrontmatterFieldList(commandFileContent, "hide_when_exists");
            foreach (string rel in hideWhenExists)
            {
                string full = Path.Combine(cityDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) return false;
            }

            string[] hideWhenDirNonempty = TextUtils.GetFrontmatterFieldList(commandFileContent, "hide_when_dir_nonempty");
            foreach (string rel in hideWhenDirNonempty)
            {
                string dir = Path.Combine(cityDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(dir)) continue;
                if (Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any())
                    return false;
            }
            return true;
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
    }
}
