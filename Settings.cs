using System;
using System.Diagnostics;
using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal.PSI.Environment;
using Game.Modding;
using Game.Settings;

namespace CityStoryMod
{
    public enum LlmProvider
    {
        // Anthropic API direct — Conversation builds tool-use messages and posts
        // them to api.anthropic.com. Requires an API key from console.anthropic.com.
        // Cost lands on that key's billing.
        AnthropicAPI,

        // Anthropic via Claude Code CLI — spawns `claude -p` as a subprocess in the
        // city dir. Uses the user's existing Claude Code login (Max subscription or
        // API key configured in the CLI), so no key needs to be pasted into the mod.
        // Tool loop is Claude Code's own (Read/Write/Edit/Glob/Grep against the city
        // dir), not the AgentLoop in this mod. Requires Claude Code installed and
        // logged in (`claude --version` works from the same env CS2 was launched in).
        AnthropicCLI,

        OpenAI,
        Gemini,
        Ollama,
    }

    [FileLocation("ModsSettings/" + nameof(CityStoryMod) + "/" + nameof(CityStoryMod))]
    public class Settings : ModSetting
    {
        public Settings(IMod mod) : base(mod) { SetDefaults(); }

        [SettingsUISlider(min = 0, max = 60, step = 1, scalarMultiplier = 1, unit = "")]
        public int IntervalMinutes { get; set; }

        // Continual spatial-map refresh. The combined map.png (terrain, water,
        // roads, zoning, services) only regenerates when Carto runs — on a new
        // city's first load and on the Refresh-map button. With this on, the
        // mod also queues a Carto refresh on its own slow cadence so the map
        // tracks the city as it grows (and as the player terraforms, which the
        // terrain raster reflects). Off by default: a full Carto export is
        // synchronous, main-thread, and scales with city size, so it's a
        // deliberate opt-in. Kept on a SEPARATE, slower interval than the
        // snapshot cadence so frequent snapshots don't drag the heavy map regen
        // along with them.
        public bool MapRefreshEnabled { get; set; }

        [SettingsUISlider(min = 5, max = 120, step = 5, scalarMultiplier = 1, unit = "")]
        [SettingsUIHideByCondition(typeof(Settings), nameof(MapRefreshDisabled))]
        public int MapRefreshMinutes { get; set; }

        [SettingsUIHidden]
        public bool MapRefreshDisabled => !MapRefreshEnabled;

        // When on, the mod writes an open `sessions/SXX-YYYY-MM-DD-open.md` stub
        // into the city folder the moment a save is loaded (gate: out-of-game →
        // in-game transition with city ready). The agent's open-session "pid"
        // rule then picks it up automatically — opening Claude lands in a live
        // session without the player having to invoke /session-start. Skipped
        // if an open session already exists, so it never stacks duplicates.
        public bool AutoSessionStartOnSaveLoad { get; set; }

        // Active event generation (issue #38). When on, the mod periodically
        // invokes /story-driven on the storyteller to propose new open events
        // the player has to respond to in-game, and runs /events-resolve after
        // every snapshot export to close events the player has already acted
        // on. Off by default — continuous LLM activity has a real token cost
        // and the player should opt in. Idle detection (sim paused OR no
        // input for several minutes) suspends the cadence so the storyteller
        // doesn't burn tokens into a void. The agent enforces a 3-5 open-
        // event cap at the file level; this system also skips generation
        // when the cap is hit to save a wasted LLM round-trip.
        //
        // The on/off control lives in the Ghostwriter UI toolbar, not the
        // Options sidebar — flipping it during play shouldn't require diving
        // into game options. The value still persists via this ModSetting;
        // PromptUISystem exposes it to the React panel via a ValueBinding /
        // TriggerBinding pair.
        [SettingsUIHidden]
        public bool ActiveEventsEnabled { get; set; }

        // Wall-clock-minute floor between autonomous /story-driven invocations.
        // The actual fire time can be later if the player is idle when the
        // interval elapses — the system waits for activity before generating.
        // 1-60 minute range; defaults to 10. Lives in Options as a set-and-
        // forget power-user setting; the on/off toggle is in the Ghostwriter
        // UI for quick access during play.
        [SettingsUISlider(min = 1, max = 60, step = 1, scalarMultiplier = 1, unit = "")]
        public int ActiveEventsIntervalMinutes { get; set; }

        // LLM credentials for the in-game ghostwriter. Key/model are kept generic
        // (not Anthropic-prefixed) so they apply to whichever provider is selected
        // below — switch provider, paste a different key, paste a matching model
        // id, no schema migration. Key is stored in CS2's settings file plain; the
        // description label spells this out.
        public LlmProvider Provider { get; set; }

        // Streamer / screenshot safety. The key is masked by default — the panel
        // shows ApiKeyStatus (last 4 chars only) instead of the full secret, so
        // it can't be caught on a stream or screen-share. Flip this to swap in
        // the editable field. Force-reset to false on every mod load (see
        // Mod.OnLoad) so a revealed key never survives into the next session:
        // reopen Options and it's masked again. CS2's declarative Options UI has
        // no focus/blur hook, so this per-session reset is the closest we get to
        // "re-hide on losing focus."
        [SettingsUIHideByCondition(typeof(Settings), nameof(IsCliProvider))]
        public bool RevealApiKey { get; set; }

        // Read-only masked preview shown in place of the real field while it's
        // hidden. Lets the player confirm a key is set (and which one, by its
        // last 4 chars — the industry-standard safe reveal) without the full
        // secret on screen. Disabled so it's display-only; the setter ignores
        // write-back from the UI.
        [SettingsUITextInput]
        [SettingsUIDisableByCondition(typeof(Settings), nameof(AlwaysTrue))]
        [SettingsUIHideByCondition(typeof(Settings), nameof(ApiKeyStatusHidden))]
        public string ApiKeyStatus
        {
            get
            {
                string k = ApiKey?.Trim() ?? "";
                if (k.Length == 0) return "(no key set)";
                string last4 = k.Length <= 4 ? k : k.Substring(k.Length - 4);
                return new string('*', 8) + " " + last4;
            }
            set { /* display-only; ignore UI write-back */ }
        }

        [SettingsUITextInput]
        [SettingsUIHideByCondition(typeof(Settings), nameof(ApiKeyEditorHidden))]
        public string ApiKey { get; set; }

        // CLI providers never show the key field at all (they carry their own
        // credentials). Otherwise the editable field shows only when revealed,
        // and the masked preview shows only when it isn't — the two are mutually
        // exclusive so exactly one occupies the slot.
        [SettingsUIHidden]
        public bool ApiKeyEditorHidden => IsCliProvider || !RevealApiKey;

        [SettingsUIHidden]
        public bool ApiKeyStatusHidden => IsCliProvider || RevealApiKey;

        [SettingsUIHidden]
        public bool AlwaysTrue => true;

        [SettingsUITextInput]
        public string Model { get; set; }

        // Ollama runs against a local (or LAN) HTTP endpoint instead of a hosted
        // API — only relevant when Provider == Ollama, hidden otherwise to keep
        // the panel uncluttered for the other providers.
        [SettingsUITextInput]
        [SettingsUIHideByCondition(typeof(Settings), nameof(IsOllamaProvider), invert: true)]
        public string OllamaBaseUrl { get; set; }

        [SettingsUIHidden]
        public bool IsOllamaProvider => Provider == LlmProvider.Ollama;

        // CLI providers don't read ApiKey — they use the credentials configured in
        // the external CLI itself (e.g. `claude /login`). Used to hide the API key
        // field from the panel when the CLI path is selected.
        [SettingsUIHidden]
        public bool IsCliProvider => Provider == LlmProvider.AnthropicCLI;

        // When on, the storyteller chat surfaces each tool call the model
        // makes and each tool result that comes back — read/write/grep/etc.
        // — as their own chat rows. Useful for debugging an agent that
        // isn't behaving, noisy for normal play. Default off.
        public bool ShowToolCalls { get; set; }

        [SettingsUIButton]
        public bool OpenStoryFolder
        {
            set { RevealStoryFolder(); }
        }

        public override void SetDefaults()
        {
            IntervalMinutes = 5;
            MapRefreshEnabled = false;
            MapRefreshMinutes = 30;
            AutoSessionStartOnSaveLoad = false;
            ActiveEventsEnabled = false;
            ActiveEventsIntervalMinutes = 10;
            Provider = LlmProvider.AnthropicAPI;
            ApiKey = "";
            RevealApiKey = false;
            Model = "claude-opus-4-7";
            OllamaBaseUrl = "http://localhost:11434";
            ShowToolCalls = false;
        }

        static void RevealStoryFolder()
        {
            // Prefer the most recently exported city dir; fall back to the parent
            // CityStoryMod dir so the player can still browse all saves from the
            // main menu before any export has happened.
            string root = Path.Combine(EnvPath.kUserDataPath, "ModsData", nameof(CityStoryMod));
            string target = Mod.LastExportedCityDir ?? root;
            try
            {
                Directory.CreateDirectory(target);
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, $"Open story folder failed: {target}");
            }
        }
    }
}
