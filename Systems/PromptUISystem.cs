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
        ValueBinding<bool> _activeEventsEnabledBinding;
        ValueBinding<bool> _activeEventsPausedBinding;
        ValueBinding<int> _nextEventAtUtcSecBinding;
        int _lastNextEventAtUtcSec;
        bool _lastActiveEventsPaused;
        ValueBinding<string> _openEventsBinding;
        string _openEventsScannedCityDir;

        // Caches the city dir we last scanned for slash commands / canon
        // tree. OnUpdate rescans when LastExportedCityDir changes, when a
        // run finishes, or when the canon-dir FileSystemWatcher reports a
        // change (out-of-band edits via file system / editor / another tool).
        string _commandsScannedCityDir;
        string _canonScannedCityDir;

        // FileSystemWatcher for live canon-tree refresh (issue #23). Watches
        // the city dir's canon-managed subtrees and flips a UTC-tick stamp
        // whenever something changes. OnUpdate debounces — a single re-scan
        // fires once the burst has been quiet for at least DebounceMs, so
        // the agent writing 10 files in one tool call produces one refresh,
        // not ten. The watcher is recreated when LastExportedCityDir
        // changes (city rename, save switch); disposed on system destroy.
        //
        // The watcher event runs on a thread-pool thread; only the
        // Interlocked-protected stamp is touched there. All ECS / binding
        // work stays on the main thread inside OnUpdate.
        FileSystemWatcher _canonWatcher;
        string _canonWatcherCityDir;
        long _canonChangeTicksUtc;
        const int CanonRescanDebounceMs = 250;

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
            // Initial value reflects the persisted ModSetting. PromptUISystem
            // also re-emits this binding each time setActiveEventsEnabled
            // fires so the React panel sees the new state immediately rather
            // than after a roundtrip via the AssetDatabase.
            _activeEventsEnabledBinding = new ValueBinding<bool>(Group, "activeEventsEnabled",
                Mod.Settings != null && Mod.Settings.ActiveEventsEnabled);
            // Unix-seconds timestamp of the next eligible /story-driven fire.
            // Re-emitted in OnUpdate only when the value drifts, so UI can
            // run a local 1Hz setInterval to render the countdown without
            // the mod thrashing the binding every frame. Int (not long)
            // because CS2's ValueWriters layer has no writer for Int64.
            _nextEventAtUtcSecBinding = new ValueBinding<int>(Group, "nextEventAtUtcSec", 0);
            // True while the autonomous loop is frozen (sim paused / out of
            // game). UI uses this to freeze the displayed countdown and skip
            // its 1Hz tick — no need for the C# side to advance the deadline
            // per frame and trigger the second-boundary "18:59 ↔ 19:00"
            // bounce that comes from int-second binding lag vs continuous
            // Date.now() in the UI.
            _activeEventsPausedBinding = new ValueBinding<bool>(Group, "activeEventsPaused", false);
            // Compact JSON list of open events (status: open in frontmatter)
            // for the in-panel inbox above the chat/canon body. Each entry is
            // {path, title, date, in_world_deadline}. Updated on the same
            // canon-watcher debounce as canonTree — /events-resolve closing
            // events triggers a rescan automatically.
            _openEventsBinding = new ValueBinding<string>(Group, "openEvents", "[]");
            AddBinding(_messagesBinding);
            AddBinding(_isRunningBinding);
            AddBinding(_tokenSummaryBinding);
            AddBinding(_lastErrorBinding);
            AddBinding(_availableCommandsBinding);
            AddBinding(_canonTreeBinding);
            AddBinding(_cartoExportingBinding);
            AddBinding(_cartoAvailableBinding);
            AddBinding(_activeEventsEnabledBinding);
            AddBinding(_nextEventAtUtcSecBinding);
            AddBinding(_activeEventsPausedBinding);
            AddBinding(_openEventsBinding);

            AddBinding(new TriggerBinding<string>(Group, "submitPrompt", OnSubmitPrompt));
            AddBinding(new TriggerBinding(Group, "cancelRun", OnCancelRun));
            AddBinding(new TriggerBinding(Group, "clearMessages", OnClearMessages));
            AddBinding(new TriggerBinding(Group, "refreshGeography", OnRefreshGeography));
            AddBinding(new TriggerBinding<string>(Group, "uiLog", OnUILog));
            AddBinding(new TriggerBinding<bool>(Group, "setActiveEventsEnabled", OnSetActiveEventsEnabled));
            AddBinding(new TriggerBinding<string>(Group, "mapGoto", OnMapGoto));

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
            // Token totals always accumulate. Prose turns always go to chat.
            // Tool-use-only turns (no TextContent, just tool calls) only
            // surface when the ShowToolCalls debug setting is on — they're
            // valuable for debugging an agent that isn't behaving, but
            // noise during normal play.
            string text = turn.TextContent;
            bool showToolCalls = Mod.Settings != null && Mod.Settings.ShowToolCalls;
            if (string.IsNullOrWhiteSpace(text)
                && showToolCalls
                && turn.ToolCalls != null
                && turn.ToolCalls.Count > 0)
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

        // Tool results route to the chat only when ShowToolCalls is on.
        // Off by default — file-read output and grep dumps are debugging
        // noise for the normal player. When on, each result becomes its own
        // chat row prefixed with `[tool result]` (or `[tool error]`) and
        // truncated past a generous cap. Full content is in the mod log
        // either way.
        void OnToolResults(IReadOnlyList<ToolResult> results)
        {
            if (results == null) return;
            if (Mod.Settings == null || !Mod.Settings.ShowToolCalls) return;
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

        // Subdirs of the city dir whose contents drive the canon browser.
        // Used by the FileSystemWatcher filter and by ScanCanonTree.
        static readonly string[] _canonManagedSubdirs =
        {
            "canon", "characters", "companies", "places",
            "factions", "events", "sessions", "stories", "secrets",
        };

        // Spins up a fresh FileSystemWatcher pointed at the city dir's
        // canon-managed subtrees, replacing any prior watcher. Called when
        // LastExportedCityDir changes — including on dispose-with-null to
        // tear down cleanly. Safe to call from main thread only.
        void RebuildCanonWatcher(string cityDir)
        {
            // Tear down the old watcher first; its events fire on a thread
            // pool worker so disposal cleanly cancels pending dispatches.
            if (_canonWatcher != null)
            {
                try
                {
                    _canonWatcher.EnableRaisingEvents = false;
                    _canonWatcher.Dispose();
                }
                catch (Exception ex)
                {
                    _log.Warn($"Disposing previous canon watcher failed: {ex.Message}");
                }
                _canonWatcher = null;
            }

            if (string.IsNullOrEmpty(cityDir) || !Directory.Exists(cityDir)) return;

            try
            {
                var w = new FileSystemWatcher(cityDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                    // 64 KB internal buffer (max allowed); default 8 KB
                    // overflows during legit bursts (e.g. an agent tool call
                    // writing a dozen files in a few ms, or a backup tool
                    // syncing the city dir). On overflow, Windows drops
                    // intermediate events and we'd miss the refresh.
                    InternalBufferSize = 65536,
                    EnableRaisingEvents = false,
                };
                w.Changed += OnCanonFsChange;
                w.Created += OnCanonFsChange;
                w.Deleted += OnCanonFsChange;
                w.Renamed += (s, e) => OnCanonFsChange(s, e);
                w.Error += (s, e) => _log.Warn($"Canon watcher error: {e.GetException().Message}");
                w.EnableRaisingEvents = true;
                _canonWatcher = w;
                _log.Info($"Canon watcher armed on {cityDir}");
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to arm canon watcher on {cityDir}: {ex.Message}. Canon tree will only refresh on run-finish.");
            }
        }

        // Watcher event — fires on a thread pool worker. Filter to the
        // canon-managed subdirs (the watcher itself is rooted at the city
        // dir with IncludeSubdirectories, so we get noise from snapshots/
        // and carto/ too) and stamp the change-time. OnUpdate handles the
        // debounce + rescan on the main thread.
        void OnCanonFsChange(object sender, FileSystemEventArgs e)
        {
            string rel = TryGetRelative(e.FullPath, _canonWatcherCityDir);
            if (rel == null) return;
            // First path segment tells us which subdir the change is in.
            int slash = rel.IndexOfAny(new[] { '/', '\\' });
            string top = slash >= 0 ? rel.Substring(0, slash) : rel;
            for (int i = 0; i < _canonManagedSubdirs.Length; i++)
            {
                if (string.Equals(top, _canonManagedSubdirs[i], StringComparison.OrdinalIgnoreCase))
                {
                    System.Threading.Interlocked.Exchange(ref _canonChangeTicksUtc, DateTime.UtcNow.Ticks);
                    return;
                }
            }
        }

        static string TryGetRelative(string fullPath, string baseDir)
        {
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(baseDir)) return null;
            try
            {
                string full = Path.GetFullPath(fullPath);
                string baseFull = Path.GetFullPath(baseDir).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
                if (full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(baseFull.Length);
            }
            catch { /* path errors are non-fatal */ }
            return null;
        }

        protected override void OnDestroy()
        {
            // Dispose the watcher cleanly — its background event thread
            // would otherwise outlive the system and try to mutate state
            // that's been torn down.
            RebuildCanonWatcher(null);
            base.OnDestroy();
        }

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
            // OnRunFinished) or the FileSystemWatcher reports a debounced
            // settle. Cheap when nothing has changed (one string compare).
            string cityDir = Mod.LastExportedCityDir;
            if (cityDir != _commandsScannedCityDir)
            {
                _commandsScannedCityDir = cityDir;
                _availableCommandsBinding.Update(ScanAvailableCommands(cityDir));
            }

            // Manage the canon watcher's lifecycle — rebuild whenever the
            // city dir changes (rename migration, save switch, first export).
            if (cityDir != _canonWatcherCityDir)
            {
                _canonWatcherCityDir = cityDir;
                RebuildCanonWatcher(cityDir);
            }

            // If the watcher reported a change, hold off rescanning until
            // the burst has been quiet for DebounceMs — the agent often
            // writes several files per tool call within a few ms, and a
            // single rescan after the burst is enough.
            long changeTicks = System.Threading.Interlocked.Read(ref _canonChangeTicksUtc);
            if (changeTicks != 0)
            {
                long now = DateTime.UtcNow.Ticks;
                double sinceMs = TimeSpan.FromTicks(now - changeTicks).TotalMilliseconds;
                if (sinceMs >= CanonRescanDebounceMs)
                {
                    System.Threading.Interlocked.Exchange(ref _canonChangeTicksUtc, 0);
                    _canonScannedCityDir = null;
                    _openEventsScannedCityDir = null;
                }
            }

            if (cityDir != _canonScannedCityDir)
            {
                _canonScannedCityDir = cityDir;
                _canonTreeBinding.Update(ScanCanonTree(cityDir));
            }

            if (cityDir != _openEventsScannedCityDir)
            {
                _openEventsScannedCityDir = cityDir;
                _openEventsBinding.Update(ScanOpenEvents(cityDir));
            }

            // Reflect Carto availability into the UI. CartoBridge.IsAvailable
            // resolves lazily on first access, so we let the bridge handle the
            // resolve and just mirror its state. Cheap — one property read.
            bool cartoAvail = CartoBridge.IsAvailable;
            if (cartoAvail != _cartoAvailableBinding.value)
            {
                _cartoAvailableBinding.Update(cartoAvail);
            }

            // Active-events countdown anchor + paused flag. ActiveEventsSystem
            // exposes both; we re-emit only when each value shifts so the
            // bindings aren't thrashed every frame. UI counts down locally
            // when not paused, freezes the display when paused.
            ActiveEventsSystem activeEvents = World.GetExistingSystemManaged<ActiveEventsSystem>();
            int nextFire = activeEvents != null ? activeEvents.NextFireUtcSec : 0;
            if (nextFire != _lastNextEventAtUtcSec)
            {
                _lastNextEventAtUtcSec = nextFire;
                _nextEventAtUtcSecBinding.Update(nextFire);
            }
            bool paused = activeEvents != null && activeEvents.IsActiveEventsPaused;
            if (paused != _lastActiveEventsPaused)
            {
                _lastActiveEventsPaused = paused;
                _activeEventsPausedBinding.Update(paused);
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
            // Reset CLI session continuity too — clearing the chat means the
            // visible history is wiped, and we don't want the next CLI run
            // to `--continue` into context the player no longer sees.
            try { Mod.Storyteller?.ResetCliSession(); } catch { /* best-effort */ }
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

        // Camera fly-to from a clicked coordinate sigil in the chat. The UI
        // sends the agent's recentered-meters pair as "x,y" (a single string —
        // mirrors submitPrompt and dodges multi-arg / Int64 binding quirks).
        // We translate that frame to CS2 world meters and hand off to
        // CameraNavSystem, which eases the camera over. See MapCoords for the
        // frame math and CameraNavSystem for the animation.
        void OnMapGoto(string coords)
        {
            if (string.IsNullOrWhiteSpace(coords)) return;
            int comma = coords.IndexOf(',');
            if (comma <= 0)
            {
                _log.Warn($"OnMapGoto: malformed coordinate payload '{coords}'.");
                return;
            }
            if (!double.TryParse(coords.Substring(0, comma).Trim(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double rx)
                || !double.TryParse(coords.Substring(comma + 1).Trim(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ry))
            {
                _log.Warn($"OnMapGoto: could not parse coordinate payload '{coords}'.");
                return;
            }

            MapCoords.RecenteredToWorld(rx, ry, out double worldX, out double worldZ);
            CameraNavSystem nav = World.GetExistingSystemManaged<CameraNavSystem>();
            if (nav == null)
            {
                _log.Warn("OnMapGoto: CameraNavSystem unavailable; cannot fly camera.");
                return;
            }
            nav.FlyTo(worldX, worldZ);
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

        // Toggle handler for the Ghostwriter toolbar's active-events button.
        // Writes the new value into the ModSetting (which AssetDatabase will
        // persist on its own cadence) and re-emits the value binding so the
        // React panel sees the new state on the next frame.
        void OnSetActiveEventsEnabled(bool enabled)
        {
            if (Mod.Settings == null) return;
            if (Mod.Settings.ActiveEventsEnabled == enabled) return;
            Mod.Settings.ActiveEventsEnabled = enabled;
            _activeEventsEnabledBinding.Update(enabled);
            _log.Info($"Active events toggled {(enabled ? "on" : "off")} via UI.");
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
        // Lite-mode fallback for pathological canon trees: if the total
        // file count across all canon subdirs exceeds CanonLiteModeThreshold,
        // we skip the per-file content read and ship names + paths only.
        // The FileModal will render empty content (close + reopen the panel
        // re-scans), but the game doesn't hitch trying to read megabytes of
        // markdown into a single ValueBinding update. Protects against
        // sync-bombs, runaway agent writes, and accidental file dumps into
        // the canon dirs.
        //
        // Skips secrets/ entirely when settings.json's secrets_visibility
        // is anything other than "shown" (default "hidden" — see
        // template/CLAUDE.md → Secrets). Empty subdirs are dropped so the
        // sidebar doesn't show headers with no entries.
        const int CanonLiteModeThreshold = 500;

        // Walks <cityDir>/events/*.md, parses minimal frontmatter, returns a
        // JSON array of entries with `status: open`. Output is sorted by
        // in_world_deadline ascending (most urgent first). Reads only the
        // first 4 KB of each file — one frontmatter block fits comfortably
        // and a cap bounds the cost when there are many events.
        //
        // No YAML parser dependency — we walk top-level lines looking for
        // `key:` prefixes. Nested fields (like the options list's per-item
        // attributes) are intentionally skipped; the inbox card only needs
        // title + date + deadline. Players opening an event from the inbox
        // see the full body via the existing FileModal.
        static string ScanOpenEvents(string cityDir)
        {
            if (string.IsNullOrEmpty(cityDir)) return "[]";
            string eventsDir = Path.Combine(cityDir, "events");
            if (!Directory.Exists(eventsDir)) return "[]";

            var entries = new List<JObject>();
            string[] files;
            try { files = Directory.GetFiles(eventsDir, "*.md"); }
            catch (Exception ex)
            {
                _log.Warn($"Open-events scan: listing {eventsDir} failed: {ex.Message}");
                return "[]";
            }

            foreach (string path in files)
            {
                string head;
                try
                {
                    using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using StreamReader sr = new StreamReader(fs);
                    char[] buf = new char[4096];
                    int n = sr.Read(buf, 0, buf.Length);
                    if (n <= 0) continue;
                    head = new string(buf, 0, n);
                }
                catch { continue; }

                int first = head.IndexOf("---", StringComparison.Ordinal);
                if (first < 0) continue;
                int second = head.IndexOf("---", first + 3, StringComparison.Ordinal);
                string fm = second > 0
                    ? head.Substring(first + 3, second - first - 3)
                    : head.Substring(first + 3);

                string status = ExtractTopLevelField(fm, "status");
                if (!string.Equals(status, "open", StringComparison.Ordinal)) continue;

                string title = ExtractTopLevelField(fm, "title");
                if (string.IsNullOrEmpty(title)) title = Path.GetFileNameWithoutExtension(path);
                string date = ExtractTopLevelField(fm, "date") ?? "";
                string deadline = ExtractTopLevelField(fm, "in_world_deadline") ?? "";

                entries.Add(new JObject
                {
                    ["path"] = "events/" + Path.GetFileName(path),
                    ["title"] = title,
                    ["date"] = date,
                    ["in_world_deadline"] = deadline,
                });
            }

            // Sort by deadline ascending (empty deadline sorts last). Same-
            // deadline ties: keep file order, stable.
            entries.Sort((a, b) =>
            {
                string da = (string)a["in_world_deadline"];
                string db = (string)b["in_world_deadline"];
                bool ea = string.IsNullOrEmpty(da);
                bool eb = string.IsNullOrEmpty(db);
                if (ea && eb) return 0;
                if (ea) return 1;
                if (eb) return -1;
                return string.CompareOrdinal(da, db);
            });

            return JsonConvert.SerializeObject(entries);
        }

        // Reads the first top-level YAML field with the given key name from
        // a frontmatter body (no enclosing --- markers). Top-level means
        // unindented and not a comment line. Strips an inline `# comment`
        // suffix from the returned value. Returns null when not found.
        static string ExtractTopLevelField(string frontmatter, string key)
        {
            string prefix = key + ":";
            foreach (string raw in frontmatter.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == ' ' || line[0] == '\t' || line[0] == '#') continue;
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string val = line.Substring(prefix.Length).Trim();
                int hash = val.IndexOf('#');
                if (hash >= 0) val = val.Substring(0, hash).Trim();
                return val;
            }
            return null;
        }

        static string ScanCanonTree(string cityDir)
        {
            if (string.IsNullOrEmpty(cityDir)) return "{}";
            JObject settings = ReadCitySettings(cityDir);
            bool showSecrets = string.Equals(
                (string)settings?["secrets_visibility"], "shown", StringComparison.Ordinal);

            // First pass: collect file lists per subdir and count globally.
            // Cheap — directory enumeration only, no reads. Lets us decide
            // lite mode before paying any read cost.
            var perSubdir = new List<KeyValuePair<string, string[]>>();
            int totalFiles = 0;
            foreach (string sub in s_CanonSubdirs)
            {
                if (sub == "secrets" && !showSecrets) continue;
                string dir = Path.Combine(cityDir, sub);
                if (!Directory.Exists(dir)) continue;
                string[] files;
                try { files = Directory.GetFiles(dir, "*.md"); }
                catch (Exception ex) { _log.Warn($"Canon scan: listing {dir} failed: {ex.Message}"); continue; }
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                perSubdir.Add(new KeyValuePair<string, string[]>(sub, files));
                totalFiles += files.Length;
            }

            bool liteMode = totalFiles > CanonLiteModeThreshold;
            if (liteMode)
            {
                _log.Warn($"Canon scan: {totalFiles} files exceeds lite-mode threshold ({CanonLiteModeThreshold}). Tree will list names without content; close + reopen the panel after pruning to restore full content.");
            }

            var tree = new System.Collections.Specialized.OrderedDictionary();
            foreach (var kv in perSubdir)
            {
                string sub = kv.Key;
                string[] files = kv.Value;
                var entries = new List<object>();
                foreach (string path in files)
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    string rel = sub + "/" + Path.GetFileName(path);
                    string content = liteMode ? "" : SafeReadFile(path);
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
