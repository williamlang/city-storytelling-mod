using System;
using System.IO;
using System.Reflection;
using CityStoryMod.Storyteller;
using Colossal.Logging;
using Game;
using Game.SceneFlow;
using Game.Simulation;
using Unity.Entities;
using UnityEngine;

namespace CityStoryMod.Systems
{
    // Drives the autonomous storyteller loop (issue #38). Two responsibilities:
    //
    //   1. Cadence-fired /story-driven generation. When Settings.ActiveEvents-
    //      Enabled is on, every Settings.ActiveEventsIntervalMinutes of wall
    //      clock the system queues a /story-driven run — provided the player
    //      isn't idle (sim paused OR no input for IdleThresholdMinutes), the
    //      dispatcher is free, and the open-event queue is below cap.
    //
    //   2. Post-export /events-resolve. ExportSystem flags QueueResolveRun()
    //      at the end of each successful Export(); this system drains the
    //      flag and fires /events-resolve on the next tick the dispatcher is
    //      free. Skipped if there are no open events to scan against.
    //
    // The two runs share the dispatcher (one run at a time, in-process). When
    // both want to fire on the same tick, resolution wins — the queue check
    // for generation might otherwise be stale.
    //
    // Idle detection is composite: sim paused (SimulationSystem.selectedSpeed
    // resolved reflectively, gracefully degrades to "not paused" if the field
    // is renamed) OR no keyboard/mouse activity for IdleThresholdMinutes.
    // Mouse position deltas and Input.anyKey cover both surfaces. Window
    // focus is intentionally not checked — the issue calls out alt-tab-AFK
    // as a case the no-input signal should already cover.
    public partial class ActiveEventsSystem : GameSystemBase
    {
        static readonly ILog _log = Mod.Log;

        // No-input threshold for idle detection. Hardcoded for v1 — the issue
        // calls out 5 min as the default and there's no reason to surface this
        // as a setting yet. Bump to a Setting if it turns out to need tuning.
        const double IdleThresholdMinutes = 5.0;

        // Open-event cap. The agent enforces this on the file side (see
        // template/.claude/commands/story-driven.md step 1), but we also
        // gate at the mod level to avoid a wasted LLM round-trip when the
        // queue is already full. Matches the cap documented in CLAUDE.md
        // "Active events — Open-event cap".
        const int OpenEventCap = 3;

        SimulationSystem _simulationSystem;
        FieldInfo _f_selectedSpeed;       // SimulationSystem.selectedSpeed-like field, resolved reflectively

        // Throttle the sim-paused reflection probe — pause / unpause edges
        // don't need sub-second responsiveness, and the per-frame cost
        // (reflective field read on SimulationSystem) was wasted overhead.
        const double PauseCheckIntervalMs = 250.0;

        DateTime _lastGenerationUtc;
        DateTime _lastInputUtc;
        DateTime _lastPauseCheckUtc;
        // null when running normally; the UTC of the most recent pause-edge
        // transition while currently frozen. On unpause edge we advance
        // _lastGenerationUtc by (now - this) so the cadence picks up where
        // it was when pause started, instead of having drained during pause.
        DateTime? _pauseStartedAtUtc;
        Vector3 _lastMousePosition;
        bool _resolveQueued;
        bool _firstTickLogged;

        protected override void OnCreate()
        {
            base.OnCreate();

            _simulationSystem = World.GetExistingSystemManaged<SimulationSystem>();
            if (_simulationSystem == null)
            {
                _log.Warn("ActiveEventsSystem: SimulationSystem not found; sim-paused idle signal will be skipped.");
            }
            else
            {
                // Locate the selected-speed field reflectively. CS2 internals
                // shift between patches; if a future rename breaks the lookup,
                // we degrade to "not paused" rather than crash. Common
                // candidates across CS2 versions: m_SelectedSpeed, selected-
                // Speed; the field can be a float (0..3) or an enum.
                var t = _simulationSystem.GetType();
                _f_selectedSpeed = t.GetField("m_SelectedSpeed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                ?? t.GetField("selectedSpeed",  BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_f_selectedSpeed == null)
                {
                    _log.Info("ActiveEventsSystem: selectedSpeed field not found on SimulationSystem; sim-paused idle signal will be skipped (no-input signal still active).");
                }
            }

            DateTime now = DateTime.UtcNow;
            _lastGenerationUtc = now;
            _lastInputUtc = now;
            _lastPauseCheckUtc = now;
            _lastMousePosition = Input.mousePosition;

            _log.Info("ActiveEventsSystem created.");
        }

        // Called by ExportSystem at the end of each successful export. Drained
        // on the next tick when the dispatcher is free. Idempotent — multiple
        // calls before drain coalesce into a single resolve run.
        public void QueueResolveRun()
        {
            _resolveQueued = true;
        }

        // True when the autonomous loop is currently frozen (sim paused or
        // game not in an active session). PromptUISystem polls this and re-
        // emits a ValueBinding so the UI can freeze the countdown display
        // and skip its 1Hz tick — no per-frame UI churn while paused.
        public bool IsActiveEventsPaused => _pauseStartedAtUtc.HasValue;

        // Unix-seconds timestamp of the next eligible /story-driven fire.
        // 0 when the loop is disabled. PromptUISystem polls this each tick
        // and re-emits a ValueBinding when the value crosses an observation
        // threshold, so the UI can render a local countdown without the
        // mod ticking a binding every frame. The actual fire can be later
        // than this timestamp if the player is idle / sim is paused / the
        // open-event cap is hit when the deadline arrives; the UI surfaces
        // that as a "ready" label when remaining time goes negative.
        //
        // Returns int (not long): CS2's ValueWriters layer does not
        // implement a writer for System.Int64, so a ValueBinding<long>
        // throws at construction. Unix-seconds-as-int fits until 2038
        // which is far past this mod's relevant lifetime.
        public int NextFireUtcSec
        {
            get
            {
                Settings settings = Mod.Settings;
                if (settings == null || !settings.ActiveEventsEnabled) return 0;
                DateTime next = _lastGenerationUtc.AddMinutes(settings.ActiveEventsIntervalMinutes);
                // _lastGenerationUtc is created via DateTime.UtcNow so Kind=Utc;
                // the DateTime→DateTimeOffset cast preserves the offset correctly.
                long sec = ((DateTimeOffset)next).ToUnixTimeSeconds();
                return sec > int.MaxValue ? int.MaxValue : (int)sec;
            }
        }

        protected override void OnUpdate()
        {
            if (!_firstTickLogged)
            {
                _firstTickLogged = true;
                _log.Info("ActiveEventsSystem OnUpdate firing.");
            }

            UpdateInputActivity();

            Settings settings = Mod.Settings;
            if (settings == null || !settings.ActiveEventsEnabled)
            {
                // Clear any in-progress pause so the next time the loop is
                // enabled we don't carry forward a stale pause-start that
                // would over-correct _lastGenerationUtc on the next unpause.
                _pauseStartedAtUtc = null;
                return;
            }

            // Gate on the same in-game readiness the rest of the mod uses.
            bool inGame = GameManager.instance != null && GameManager.instance.gameMode == GameMode.Game;
            DateTime now = DateTime.UtcNow;

            // Pause-state edge detection. Throttled to 4Hz — pause / unpause
            // doesn't need per-frame responsiveness, and reflecting into
            // SimulationSystem 60x per second was wasted work.
            //
            // On entering paused (sim paused OR game not in active session):
            // capture the moment.
            // On leaving paused: advance _lastGenerationUtc by the paused
            // duration in one step, so the cadence picks up exactly where
            // it was when pause started instead of having drained.
            //
            // Input-idle is intentionally NOT a freeze trigger — if you AFK
            // for an hour, "ready" should be waiting when you come back.
            //
            // IsSimPaused short-circuits behind !inGame, so we don't poke
            // SimulationSystem during menu / save-load transitions.
            if ((now - _lastPauseCheckUtc).TotalMilliseconds >= PauseCheckIntervalMs)
            {
                _lastPauseCheckUtc = now;
                bool currentlyFrozen = !inGame || IsSimPaused();
                bool wasPaused = _pauseStartedAtUtc.HasValue;

                if (currentlyFrozen && !wasPaused)
                {
                    _pauseStartedAtUtc = now;
                }
                else if (!currentlyFrozen && wasPaused)
                {
                    _lastGenerationUtc += now - _pauseStartedAtUtc.Value;
                    _pauseStartedAtUtc = null;
                }
            }

            // While frozen: skip the generation cadence entirely. /events-
            // resolve via _resolveQueued only fires when inGame; we still
            // allow it during a sim-pause (the export that queued it
            // already validated state), but skip it when out of game.
            if (_pauseStartedAtUtc.HasValue)
            {
                if (inGame && _resolveQueued)
                {
                    StorytellerDispatcher pausedDispatcher = Mod.Storyteller;
                    string pausedCityDir = Mod.LastExportedCityDir;
                    if (pausedDispatcher != null && !pausedDispatcher.IsRunning && !string.IsNullOrEmpty(pausedCityDir))
                    {
                        _resolveQueued = false;
                        TryFireResolve(pausedCityDir, pausedDispatcher);
                    }
                }
                return;
            }

            if (!inGame) return;

            StorytellerDispatcher dispatcher = Mod.Storyteller;
            if (dispatcher == null || dispatcher.IsRunning) return;

            string cityDir = Mod.LastExportedCityDir;
            if (string.IsNullOrEmpty(cityDir)) return;

            // Resolution wins over generation when both are eligible the same
            // tick — a stale queue count would make the cap check wrong.
            if (_resolveQueued)
            {
                _resolveQueued = false;
                TryFireResolve(cityDir, dispatcher);
                return;
            }

            // Cadence floor + idle guard for new event generation.
            bool intervalElapsed = (DateTime.UtcNow - _lastGenerationUtc).TotalMinutes >= settings.ActiveEventsIntervalMinutes;
            if (!intervalElapsed) return;

            if (IsIdle())
            {
                // Idle: hold the floor (don't burn the interval while AFK).
                // When activity resumes and the wall-clock has elapsed, the
                // next eligible tick fires.
                return;
            }

            TryFireGeneration(cityDir, dispatcher);
        }

        void UpdateInputActivity()
        {
            Vector3 mouse = Input.mousePosition;
            bool mouseMoved = mouse != _lastMousePosition;
            _lastMousePosition = mouse;

            // Input.anyKey covers held keyboard keys and mouse buttons.
            // Scroll-wheel and pure-mouse-motion are picked up by the
            // position delta above.
            if (Input.anyKey || mouseMoved || Input.mouseScrollDelta.sqrMagnitude > 0f)
            {
                _lastInputUtc = DateTime.UtcNow;
            }
        }

        bool IsIdle()
        {
            // Either signal idles the loop.
            if (IsSimPaused()) return true;
            return (DateTime.UtcNow - _lastInputUtc).TotalMinutes >= IdleThresholdMinutes;
        }

        bool IsSimPaused()
        {
            if (_simulationSystem == null || _f_selectedSpeed == null) return false;
            try
            {
                object val = _f_selectedSpeed.GetValue(_simulationSystem);
                if (val == null) return false;
                // Field can be a float (sim speed 0..3), int, or enum. Treat
                // any zero-equivalent as paused. Convert via IConvertible
                // because direct (int) cast on an enum boxes wrong.
                if (val is IConvertible c)
                {
                    return Math.Abs(c.ToDouble(null)) < 0.0001;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        void TryFireResolve(string cityDir, StorytellerDispatcher dispatcher)
        {
            int openCount = CountOpenEvents(cityDir);
            if (openCount == 0)
            {
                // Nothing to resolve. Cheap file scan, no LLM call.
                return;
            }

            _log.Info($"ActiveEventsSystem: queuing /events-resolve ({openCount} open event(s)).");
            StorytellerDispatcher.RunFunc runFunc = StorytellerRun.Build("events-resolve", _log);
            dispatcher.Start("active-events-resolve", runFunc);
        }

        void TryFireGeneration(string cityDir, StorytellerDispatcher dispatcher)
        {
            int openCount = CountOpenEvents(cityDir);
            if (openCount >= OpenEventCap)
            {
                // Hold the cadence floor at "now" so we don't immediately
                // retry every tick once the cap is hit. The next attempt
                // waits a full interval, by which time /events-resolve may
                // have drained the queue.
                _lastGenerationUtc = DateTime.UtcNow;
                _log.Info($"ActiveEventsSystem: open-event cap reached ({openCount}/{OpenEventCap}); skipping /story-driven this interval.");
                return;
            }

            _log.Info($"ActiveEventsSystem: firing autonomous /story-driven (open={openCount}/{OpenEventCap}).");
            _lastGenerationUtc = DateTime.UtcNow;
            StorytellerDispatcher.RunFunc runFunc = StorytellerRun.Build("story-driven", _log);
            dispatcher.Start("active-events-generate", runFunc);
        }

        // Counts events/*.md files whose YAML frontmatter has `status: open`.
        // Reads only the head of each file (one frontmatter block fits in a
        // few hundred bytes; cap at 4 KB to bound the worst case). Failures
        // on individual files are swallowed and counted as "not open" —
        // better to under-count than crash the loop on a malformed file.
        static int CountOpenEvents(string cityDir)
        {
            string eventsDir = Path.Combine(cityDir, "events");
            if (!Directory.Exists(eventsDir)) return 0;

            int count = 0;
            try
            {
                foreach (string path in Directory.GetFiles(eventsDir, "*.md"))
                {
                    if (HasOpenStatus(path)) count++;
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"ActiveEventsSystem: events dir scan failed: {ex.Message}");
            }
            return count;
        }

        static bool HasOpenStatus(string path)
        {
            try
            {
                using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using StreamReader sr = new StreamReader(fs);
                char[] buf = new char[4096];
                int n = sr.Read(buf, 0, buf.Length);
                if (n <= 0) return false;
                string head = new string(buf, 0, n);

                // Find the first frontmatter block: --- ... ---
                int first = head.IndexOf("---", StringComparison.Ordinal);
                if (first < 0) return false;
                int second = head.IndexOf("---", first + 3, StringComparison.Ordinal);
                string fm = second > 0 ? head.Substring(first + 3, second - first - 3) : head.Substring(first + 3);

                // Look for a top-level `status:` line. We don't need a full
                // YAML parser — the field is one short value on one line,
                // and any nesting level we'd encounter (like inside an
                // option block) carries different indentation we ignore.
                foreach (string raw in fm.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    // Top-level keys are unindented in our schema.
                    if (line.Length == 0 || line[0] == ' ' || line[0] == '\t' || line[0] == '#') continue;
                    if (!line.StartsWith("status:", StringComparison.Ordinal)) continue;
                    string val = line.Substring("status:".Length).Trim();
                    // Strip an inline comment if present.
                    int hash = val.IndexOf('#');
                    if (hash >= 0) val = val.Substring(0, hash).Trim();
                    return string.Equals(val, "open", StringComparison.Ordinal);
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
