using System;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using CityStoryMod.Storyteller;
using CityStoryMod.Systems;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace CityStoryMod
{
    public class Mod : IMod
    {
        public static readonly ILog Log = LogManager
            .GetLogger(nameof(CityStoryMod))
            .SetShowsErrorsInUI(true);

        public static Settings Settings { get; private set; }
        public static StorytellerDispatcher Storyteller { get; private set; }

        // Most recent successfully-exported city dir. Settings' Open Story Folder
        // button uses this so the player lands in the right per-city folder when
        // they click it mid-game. Null until the first export of a session.
        public static string LastExportedCityDir { get; set; }

        // Set once when the mod loads (CS2 launch). Every snapshot in this play session
        // carries this id so the storytelling agent can bucket snapshots without inferring
        // session boundaries from time gaps.
        public static string SessionId { get; private set; }
        public static DateTime SessionStartedAtUtc { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            SessionStartedAtUtc = DateTime.UtcNow;
            SessionId = $"session-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            Settings = new Settings(this);
            Settings.RegisterInOptionsUI();
            AssetDatabase.global.LoadSettings(nameof(CityStoryMod), Settings, new Settings(this));

            Storyteller = new StorytellerDispatcher(Log);

            GameManager.instance.localizationManager.AddSource("en-US", new Locale(Locale.EnglishEntries()));

            updateSystem.UpdateBefore<ExportSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateBefore<PromptUISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateBefore<ActiveEventsSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateBefore<CameraNavSystem>(SystemUpdatePhase.UIUpdate);

            Log.Info($"CityStoryMod loaded. session_id={SessionId}");
        }

        public void OnDispose()
        {
            Storyteller?.Cancel();
            Storyteller = null;

            Settings?.UnregisterInOptionsUI();
            Settings = null;

            Log.Info("CityStoryMod disposed.");
        }
    }
}
