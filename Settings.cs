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
        public bool ActiveEventsEnabled { get; set; }

        // Wall-clock-minute floor between autonomous /story-driven invocations.
        // The actual fire time can be later if the player is idle when the
        // interval elapses — the system waits for activity before generating.
        // 1-60 minute range; defaults to 10.
        [SettingsUISlider(min = 1, max = 60, step = 1, scalarMultiplier = 1, unit = "")]
        [SettingsUIHideByCondition(typeof(Settings), nameof(IsActiveEventsDisabled))]
        public int ActiveEventsIntervalMinutes { get; set; }

        [SettingsUIHidden]
        public bool IsActiveEventsDisabled => !ActiveEventsEnabled;

        // LLM credentials for the in-game ghostwriter. Key/model are kept generic
        // (not Anthropic-prefixed) so they apply to whichever provider is selected
        // below — switch provider, paste a different key, paste a matching model
        // id, no schema migration. Key is stored in CS2's settings file plain; the
        // description label spells this out.
        public LlmProvider Provider { get; set; }

        [SettingsUITextInput]
        [SettingsUIHideByCondition(typeof(Settings), nameof(IsCliProvider))]
        public string ApiKey { get; set; }

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
            AutoSessionStartOnSaveLoad = false;
            ActiveEventsEnabled = false;
            ActiveEventsIntervalMinutes = 10;
            Provider = LlmProvider.AnthropicAPI;
            ApiKey = "";
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
