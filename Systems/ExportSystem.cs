using System;
using System.IO;
using Colossal.Logging;
using Colossal.PSI.Environment;
using Game;
using Game.Citizens;
using Unity.Entities;
using UnityEngine;

namespace CityStoryMod.Systems
{
    public partial class ExportSystem : GameSystemBase
    {
        static readonly ILog _log = Mod.Log;

        EntityQuery _citizenQuery;
        DateTime _lastExportUtc;
        bool _firstTickLogged;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citizenQuery = GetEntityQuery(ComponentType.ReadOnly<Citizen>());
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

        void Export(string triggeredBy)
        {
            int population = _citizenQuery.CalculateEntityCount();

            string dir = Path.Combine(EnvPath.kUserDataPath, "ModsData", nameof(CityStoryMod));
            Directory.CreateDirectory(dir);

            long unixTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string file = Path.Combine(dir, $"snapshot-{unixTs}.json");
            File.WriteAllText(file, $"{{\"population\": {population}}}");

            _log.Info($"Exported snapshot ({triggeredBy}): population={population} -> {file}");
        }
    }
}
