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
    [FileLocation("ModsSettings/" + nameof(CityStoryMod) + "/" + nameof(CityStoryMod))]
    public class Settings : ModSetting
    {
        public Settings(IMod mod) : base(mod) { SetDefaults(); }

        public bool ExportEnabled { get; set; }

        [SettingsUISlider(min = 0, max = 60, step = 1, scalarMultiplier = 1, unit = "")]
        public int IntervalMinutes { get; set; }

        // Anthropic API credentials for the in-game storyteller. The key is stored
        // in CS2's settings file unencrypted — the description label spells this out
        // so the player can decide whether they're comfortable with that. Model
        // defaults to current best Claude (claude-opus-4-7); user can paste a newer
        // id without a mod update when Anthropic ships successors.
        [SettingsUITextInput]
        public string AnthropicApiKey { get; set; }

        [SettingsUITextInput]
        public string AnthropicModel { get; set; }

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
            AnthropicApiKey = "";
            AnthropicModel = "claude-opus-4-7";
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
