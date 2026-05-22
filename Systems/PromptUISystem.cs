using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CityStoryMod.Storyteller;
using Colossal.Logging;
using Colossal.UI.Binding;
using Game;
using Game.UI;
using Newtonsoft.Json;

namespace CityStoryMod.Systems
{
    // Bridges the in-game React panel (UI/src/mods/promptWindow/*) to the C#
    // storyteller dispatcher. Registers four ValueBindings (read by React via
    // `useValue`) and three TriggerBindings (called by React via `trigger`):
    //
    //   messages       — JSON array of ChatMessage rows (user / assistant / tool)
    //   isRunning      — true while a run is in flight
    //   tokenSummary   — short formatted token-usage line, updated per turn
    //   lastError      — non-empty when the most recent run failed
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
            AddBinding(_messagesBinding);
            AddBinding(_isRunningBinding);
            AddBinding(_tokenSummaryBinding);
            AddBinding(_lastErrorBinding);

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

    }

    // Wire-format for a chat message — serialized to JSON for the React UI.
    // Lowercase field names match the React component's TypeScript interface.
    [Serializable]
    public class ChatMessage
    {
        public string role;   // "user" | "assistant"
        public string text;
    }
}
