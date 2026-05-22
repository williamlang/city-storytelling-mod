using System;
using System.Linq;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using CityStoryMod.Storyteller;
using CityStoryMod.Systems;
using Game;
using Game.Assets;
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

        // Stable per-save identifier captured from the most recent save/load event.
        // Resolved by looking up SaveGameMetadata in AssetDatabase and reading
        // SaveInfo.id (a string assigned when the save is first created). Null when
        // no save has been loaded/created yet this session (e.g. fresh new city
        // before its first save) — ExportSystem falls back to the city-name slug in
        // that case.
        public static string ActiveSaveId { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            SessionStartedAtUtc = DateTime.UtcNow;
            SessionId = $"session-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            Settings = new Settings(this);
            Settings.RegisterInOptionsUI();
            AssetDatabase.global.LoadSettings(nameof(CityStoryMod), Settings, new Settings(this));

            Storyteller = new StorytellerDispatcher(Log);

            GameManager.instance.localizationManager.AddSource("en-US", new Locale(Locale.EnglishEntries()));
            GameManager.instance.onGameSaveLoad += OnGameSaveLoad;

            updateSystem.UpdateBefore<ExportSystem>(SystemUpdatePhase.UIUpdate);

            Log.Info($"CityStoryMod loaded. session_id={SessionId}");
        }

        public void OnDispose()
        {
            Storyteller?.Cancel();
            Storyteller = null;

            if (GameManager.instance != null)
            {
                GameManager.instance.onGameSaveLoad -= OnGameSaveLoad;
            }

            Settings?.UnregisterInOptionsUI();
            Settings = null;

            Log.Info("CityStoryMod disposed.");
        }

        // Fires for both save-to-disk and load-from-disk operations. We only care about
        // the completed case (success && !start), and only to refresh ActiveSaveId so
        // the next export writes to a per-save directory keyed by SaveInfo.id rather
        // than the city-name slug (which collides across multiple saves of the same city).
        static void OnGameSaveLoad(string saveName, string previewUri, bool start, bool success)
        {
            if (start || !success) return;
            try
            {
                ActiveSaveId = ResolveSaveId(saveName) ?? ActiveSaveId;
                Log.Info($"onGameSaveLoad: saveName='{saveName}' active_save_id='{ActiveSaveId ?? "(unresolved)"}'");
            }
            catch (Exception ex)
            {
                Log.Warn($"onGameSaveLoad: failed to resolve save id for '{saveName}': {ex.Message}");
            }
        }

        // Walks the global asset database for a SaveGameMetadata whose name matches
        // the just-loaded save and returns its underlying SaveInfo.id. Falls back to
        // sessionGuid if id is empty (defensive — id should be populated on every save).
        static string ResolveSaveId(string saveName)
        {
            if (string.IsNullOrEmpty(saveName)) return null;
            foreach (var asset in AssetDatabase.global.AllAssets().OfType<SaveGameMetadata>())
            {
                if (!string.Equals(asset.name, saveName, StringComparison.Ordinal)) continue;
                SaveInfo info = asset.target;
                if (info == null) continue;
                if (!string.IsNullOrEmpty(info.id)) return info.id;
                if (info.sessionGuid != Guid.Empty) return info.sessionGuid.ToString("N");
                return null;
            }
            return null;
        }
    }
}
