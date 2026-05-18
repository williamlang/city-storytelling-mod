using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Colossal.Logging;
using Colossal.PSI.Environment;
using Game;
using Game.Areas;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Companies;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace CityStoryMod.Systems
{
    public partial class ExportSystem : GameSystemBase
    {
        static readonly ILog _log = Mod.Log;

        EntityQuery _citizenQuery;
        EntityQuery _districtQuery;
        EntityQuery _companyQuery;
        EntityQuery _renamedBuildingQuery;
        CityConfigurationSystem _cityConfig;
        CitySystem _citySystem;
        TimeSystem _timeSystem;
        NameSystem _nameSystem;
        FieldInfo _playerMoneyField;
        DateTime _lastExportUtc;
        bool _firstTickLogged;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citizenQuery = GetEntityQuery(ComponentType.ReadOnly<Citizen>());
            _districtQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<District>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            _companyQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CompanyData>(), ComponentType.ReadOnly<Employee>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<PrefabData>(),
                },
            });
            _renamedBuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<CustomName>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<PrefabData>(),
                },
            });
            _cityConfig = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            _timeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            _nameSystem = World.GetOrCreateSystemManaged<NameSystem>();

            _playerMoneyField = typeof(PlayerMoney)
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType == typeof(long) || f.FieldType == typeof(int));
            if (_playerMoneyField == null)
            {
                _log.Warn("PlayerMoney has no long/int field; city.money will stay null. Field names: "
                    + string.Join(",", typeof(PlayerMoney).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Select(f => $"{f.Name}:{f.FieldType.Name}")));
            }

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
            long? money = (inGame && _playerMoneyField != null && _citySystem.City != Entity.Null)
                ? Convert.ToInt64(_playerMoneyField.GetValue(EntityManager.GetComponentData<PlayerMoney>(_citySystem.City)))
                : (long?)null;
            List<object> districts = inGame ? CollectDistricts() : new List<object>();
            List<object> companies = inGame ? CollectCompanies() : new List<object>();
            List<object> buildings = inGame ? CollectRenamedBuildings() : new List<object>();

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
                    money = money,
                    happiness = (int?)null,
                },

                districts = districts,
                buildings = buildings,
                companies = companies,
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

            _log.Info($"Exported snapshot ({triggeredBy}): citizens_total={citizensTotal}, districts={districts.Count}, companies={companies.Count}, named_buildings={buildings.Count} -> {file}");
        }

        List<object> CollectDistricts()
        {
            var result = new List<object>();
            using var entities = _districtQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                result.Add(new
                {
                    id = $"{e.Index}-{e.Version}",
                    name = _nameSystem.GetRenderedLabelName(e),
                    population = (int?)null,
                    area_hectares = (double?)null,
                    dominant_zone = (string)null,
                });
            }
            return result;
        }

        List<object> CollectCompanies()
        {
            var result = new List<object>();
            using var entities = _companyQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                var employees = EntityManager.GetBuffer<Employee>(e, isReadOnly: true);

                string buildingId = null;
                string districtId = null;
                if (EntityManager.HasComponent<PropertyRenter>(e))
                {
                    var building = EntityManager.GetComponentData<PropertyRenter>(e).m_Property;
                    if (building != Entity.Null)
                    {
                        buildingId = EntityId(building);
                        districtId = DistrictIdOf(building);
                    }
                }

                result.Add(new
                {
                    id = EntityId(e),
                    name = _nameSystem.GetRenderedLabelName(e),
                    custom_named = EntityManager.HasComponent<CustomName>(e),
                    sector = (string)null,
                    headcount = employees.Length,
                    building_id = buildingId,
                    district_id = districtId,
                });
            }
            return result;
        }

        List<object> CollectRenamedBuildings()
        {
            var result = new List<object>();
            using var entities = _renamedBuildingQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                result.Add(new
                {
                    id = EntityId(e),
                    name = _nameSystem.GetRenderedLabelName(e),
                    custom_named = true,
                    district_id = DistrictIdOf(e),
                });
            }
            return result;
        }

        static string EntityId(Entity e) => $"{e.Index}-{e.Version}";

        string DistrictIdOf(Entity building)
        {
            if (!EntityManager.HasComponent<CurrentDistrict>(building)) return null;
            var d = EntityManager.GetComponentData<CurrentDistrict>(building).m_District;
            return d != Entity.Null ? EntityId(d) : null;
        }
    }
}
