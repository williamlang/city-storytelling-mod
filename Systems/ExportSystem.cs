using System;
using System.IO;
using Colossal.Logging;
using Colossal.PSI.Environment;
using Game;
using Game.Citizens;
using Game.City;
using Game.SceneFlow;
using Game.Simulation;
using Newtonsoft.Json;
using Unity.Entities;
using UnityEngine;

namespace CityStoryMod.Systems
{
    public partial class ExportSystem : GameSystemBase
    {
        static readonly ILog _log = Mod.Log;

        EntityQuery _citizenQuery;
        CityConfigurationSystem _cityConfig;
        TimeSystem _timeSystem;
        DateTime _lastExportUtc;
        bool _firstTickLogged;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citizenQuery = GetEntityQuery(ComponentType.ReadOnly<Citizen>());
            _cityConfig = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            _timeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            _lastExportUtc = DateTime.UtcNow;
            _log.Info("ExportSystem created.");
        }

        protected override void OnUpdate()
        {
            if (!_firstTickLogged)
            {
                _firstTickLogged = true;
                _log.Info("ExportSystem OnUpdate firing.");
            }

            var settings = Mod.Settings;
            if (settings == null || !settings.ExportEnabled) return;

            bool hotkey = Input.GetKeyDown(KeyCode.E)
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
            bool intervalElapsed = settings.IntervalMinutes > 0
                && (DateTime.UtcNow - _lastExportUtc).TotalMinutes >= settings.IntervalMinutes;

            if (!hotkey && !intervalElapsed) return;

            try
            {
                Export(triggeredBy: hotkey ? "hotkey" : "interval");
                _lastExportUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Export failed.");
            }
        }

        const string SchemaVersion = "0.1";

        void Export(string triggeredBy)
        {
            int citizensTotal = _citizenQuery.CalculateEntityCount();

            bool inGame = GameManager.instance != null && GameManager.instance.gameMode == GameMode.Game;
            string cityName = (inGame && !string.IsNullOrEmpty(_cityConfig.cityName)) ? _cityConfig.cityName : null;
            string ingameDate = inGame ? _timeSystem.GetCurrentDateTime().ToString("yyyy-MM-dd") : null;

            long unixTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string snapshotId = $"snapshot-{unixTs}";

            var snapshot = new
            {
                schema_version = SchemaVersion,
                snapshot_id = snapshotId,
                captured_at_utc = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                captured_at_ingame = ingameDate,

                city = new
                {
                    name = cityName,
                    population_hud = (int?)null,
                    citizens_total = citizensTotal,
                    money = (long?)null,
                    happiness = (int?)null,
                },

                districts = new object[0],
                buildings = new object[0],
                companies = new object[0],
                citizens_sample = new object[0],

                demographics = new
                {
                    by_age_band = (object)null,
                    by_education = (object)null,
                    by_wealth = (object)null,
                    tourists_count = (int?)null,
                    commuters_count = (int?)null,
                },

                trade = new
                {
                    imports = new object[0],
                    exports = new object[0],
                },

                services = new { },
            };

            string json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);

            string dir = Path.Combine(EnvPath.kUserDataPath, "ModsData", nameof(CityStoryMod));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{snapshotId}.json");
            File.WriteAllText(file, json);

            _log.Info($"Exported snapshot ({triggeredBy}): citizens_total={citizensTotal} -> {file}");
        }
    }
}
