using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
            // PropertyRenter is required: it links the company to a real building.
            // Without it the entity is a per-type singleton anchor (one per Commercial_Bar,
            // Industrial_OreExtractor, etc.) that exists even on empty maps and is not a
            // real operating business.
            _companyQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CompanyData>(),
                    ComponentType.ReadOnly<Employee>(),
                    ComponentType.ReadOnly<PropertyRenter>(),
                },
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

            // Gate everything below on the game being in an active session with a
            // ready City singleton. Outside that window (main menu, loading screen,
            // editor mode), ECS state isn't safe to read and can crash the game.
            bool inGame = GameManager.instance != null && GameManager.instance.gameMode == GameMode.Game;
            bool cityReady = _citySystem != null && _citySystem.City != Entity.Null;
            if (!inGame || !cityReady)
            {
                // Hold the interval timer at "now" so the first auto-export after
                // load fires ~IntervalMinutes later instead of immediately.
                _lastExportUtc = DateTime.UtcNow;
                return;
            }

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
            // OnUpdate's gate guarantees inGame + cityReady when we get here.
            int citizensTotal = _citizenQuery.CalculateEntityCount();
            string cityName = string.IsNullOrEmpty(_cityConfig.cityName) ? null : _cityConfig.cityName;
            string ingameDate = _timeSystem.GetCurrentDateTime().ToString("yyyy-MM-dd");
            long? money = _playerMoneyField != null
                ? Convert.ToInt64(_playerMoneyField.GetValue(EntityManager.GetComponentData<PlayerMoney>(_citySystem.City)))
                : (long?)null;
            List<object> districts = CollectDistricts();
            List<object> companies = CollectCompanies();
            List<object> buildings = CollectRenamedBuildings();

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

            string citySlug = Slugify(cityName) ?? "_unnamed";
            string dir = Path.Combine(EnvPath.kUserDataPath, "ModsData", nameof(CityStoryMod), citySlug);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{snapshotId}.json");
            File.WriteAllText(file, json);

            _log.Info($"Exported snapshot ({triggeredBy}): citizens_total={citizensTotal}, districts={districts.Count}, companies={companies.Count}, named_buildings={buildings.Count} -> {file}");
        }

        static string Slugify(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var sb = new StringBuilder(name.Length);
            bool lastDash = true;
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                    lastDash = false;
                }
                else if (!lastDash)
                {
                    sb.Append('-');
                    lastDash = true;
                }
            }
            string result = sb.ToString().TrimEnd('-');
            return result.Length > 0 ? result : null;
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

                // Query guarantees PropertyRenter; m_Property still defensively null-checked.
                var building = EntityManager.GetComponentData<PropertyRenter>(e).m_Property;
                string buildingId = building != Entity.Null ? EntityId(building) : null;
                string districtId = building != Entity.Null ? DistrictIdOf(building) : null;

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
