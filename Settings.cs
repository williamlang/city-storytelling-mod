using System;
using System.Diagnostics;
using System.IO;
using CityStoryMod.Storyteller;
using Colossal.IO.AssetDatabase;
using Colossal.PSI.Environment;
using Game.Modding;
using Game.Settings;

namespace CityStoryMod
{
    public enum LlmProvider
    {
        Anthropic,
        OpenAI,
        Gemini,
        Ollama,
    }

    [FileLocation("ModsSettings/" + nameof(CityStoryMod) + "/" + nameof(CityStoryMod))]
    public class Settings : ModSetting
    {
        public Settings(IMod mod) : base(mod) { SetDefaults(); }

        public bool ExportEnabled { get; set; }

        [SettingsUISlider(min = 0, max = 60, step = 1, scalarMultiplier = 1, unit = "")]
        public int IntervalMinutes { get; set; }

        // LLM credentials for the in-game storyteller. Key/model are kept generic
        // (not Anthropic-prefixed) so they apply to whichever provider is selected
        // below — switch provider, paste a different key, paste a matching model
        // id, no schema migration. Key is stored in CS2's settings file plain; the
        // description label spells this out.
        public LlmProvider Provider { get; set; }

        [SettingsUITextInput]
        public string ApiKey { get; set; }

        [SettingsUITextInput]
        public string Model { get; set; }

        // Ollama runs against a local (or LAN) HTTP endpoint instead of a hosted
        // API — only relevant when Provider == Ollama, hidden otherwise to keep
        // the panel uncluttered for the other three providers.
        [SettingsUITextInput]
        [SettingsUIHideByCondition(typeof(Settings), nameof(IsOllamaProvider), invert: true)]
        public string OllamaBaseUrl { get; set; }

        [SettingsUIHidden]
        public bool IsOllamaProvider => Provider == LlmProvider.Ollama;

        // Read-only status surface for the in-game storyteller dispatcher. CS2's
        // settings UI has no dedicated read-only display widget, so this is a text
        // input whose setter discards writes — the getter recomputes on each render.
        // Caveat: the panel doesn't tick; "Running… 12s elapsed" reflects state at
        // panel-open time, not live progress. Acceptable for the MVP flow (player
        // triggers via hotkey in-game, opens Options to check status).
        [SettingsUITextInput]
        public string StorytellerStatus
        {
            get => ComposeStorytellerStatus();
            set { /* discarded — read-only surface */ }
        }

        [SettingsUIButton]
        public bool OpenStoryFolder
        {
            set { RevealStoryFolder(); }
        }

        public override void SetDefaults()
        {
            ExportEnabled = true;
            IntervalMinutes = 5;
            Provider = LlmProvider.Anthropic;
            ApiKey = "";
            Model = "claude-opus-4-7";
            OllamaBaseUrl = "http://localhost:11434";
        }

        static string ComposeStorytellerStatus()
        {
            StorytellerDispatcher d = Mod.Storyteller;
            if (d == null) return "(not initialized)";

            if (d.IsRunning)
            {
                int secs = (int)(d.RunDuration?.TotalSeconds ?? 0);
                return $"Running… {secs}s elapsed";
            }

            if (d.LastResultAtUtc == null) return "Idle";

            RunResult r = d.LastResult;
            string ago = FormatAgo(DateTime.UtcNow - d.LastResultAtUtc.Value);
            string took = $"{(int)r.Duration.TotalSeconds}s";

            if (!r.Success)
            {
                string err = (r.Message ?? "unknown error").Split('\n')[0];
                if (err.Length > 80) err = err.Substring(0, 77) + "…";
                return $"Last run {ago} — failed ({took}): {err}";
            }
            return $"Last run {ago} — wrote {r.FilesWritten} file(s) in {took}";
        }

        static string FormatAgo(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return "just now";
            if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m ago";
            if (t.TotalHours < 24) return $"{(int)t.TotalHours}h ago";
            return $"{(int)t.TotalDays}d ago";
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
