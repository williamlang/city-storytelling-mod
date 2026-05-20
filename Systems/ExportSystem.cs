using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using CityStoryMod.Storyteller;
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
        EntityQuery _renamedBuildingQuery;
        EntityQuery _allBuildingsQuery;
        EntityQuery _customNameQuery;
        bool _cityComponentsLogged;
        bool _buildingFirstDiagged;
        bool _citizenFirstDiagged;
        readonly HashSet<Type> _fieldDumpsSeen = new HashSet<Type>();

        // Previous-snapshot state for the embedded diff block. Reset on mod reload
        // (CS2 launch), so cross-session diffs need a future enhancement to read the
        // last on-disk snapshot on first export.
        Dictionary<string, BuildingFingerprint> _prevBuildings;
        Dictionary<string, int> _prevZoneCounts;
        Dictionary<string, NameRef> _prevRoads;
        Dictionary<string, NameRef> _prevOutsideConnections;
        Dictionary<string, NameRef> _prevWaterSources;
        Dictionary<string, NameRef> _prevDistricts;
        DateTime? _prevIngameDate;
        string _prevSnapshotId;

        struct BuildingFingerprint
        {
            public string Id;
            public string Name;
            public string Type;
            public string CompanySubtype;  // null when no company renter
            public string DistrictId;
            public bool HasCompany;
        }

        // Lightweight id+name pair used for renaming/add/remove detection on the
        // simpler entity classes (roads, outside connections, water sources, districts).
        struct NameRef
        {
            public string Id;
            public string Name;
        }
        CityConfigurationSystem _cityConfig;
        CitySystem _citySystem;
        TimeSystem _timeSystem;
        NameSystem _nameSystem;
        PrefabSystem _prefabSystem;
        FieldInfo _playerMoneyField;

        // City singleton fields, identified by name (see [diag] log dumps in the OnCreate path
        // for the source of truth). Reflection by name is brittle to CS2 updates but explicit;
        // if a future patch renames a field, the diag log will surface the new name.
        FieldInfo _f_pop_total;       // Population.m_Population
        FieldInfo _f_pop_withMoveIn;  // Population.m_PopulationWithMoveIn
        FieldInfo _f_pop_happiness;   // Population.m_AverageHappiness
        FieldInfo _f_pop_health;      // Population.m_AverageHealth
        FieldInfo _f_tour_current;    // Tourism.m_CurrentTourists
        FieldInfo _f_tour_average;    // Tourism.m_AverageTourists
        FieldInfo _f_tour_attract;    // Tourism.m_Attractiveness
        FieldInfo _f_danger_level;    // DangerLevel.m_DangerLevel (float)
        FieldInfo _f_milestone;       // MilestoneLevel.m_AchievedMilestone
        FieldInfo _f_xp;              // XP.m_XP
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
            _allBuildingsQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<PrefabData>(),
                },
            });
            _customNameQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CustomName>() },
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
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            _playerMoneyField = typeof(PlayerMoney)
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType == typeof(long) || f.FieldType == typeof(int));
            if (_playerMoneyField == null)
            {
                _log.Warn("PlayerMoney has no long/int field; city.money will stay null. Field names: "
                    + string.Join(",", typeof(PlayerMoney).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Select(f => $"{f.Name}:{f.FieldType.Name}")));
            }

            const BindingFlags fieldFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _f_pop_total      = typeof(Population).GetField("m_Population", fieldFlags);
            _f_pop_withMoveIn = typeof(Population).GetField("m_PopulationWithMoveIn", fieldFlags);
            _f_pop_happiness  = typeof(Population).GetField("m_AverageHappiness", fieldFlags);
            _f_pop_health     = typeof(Population).GetField("m_AverageHealth", fieldFlags);
            _f_tour_current   = typeof(Tourism).GetField("m_CurrentTourists", fieldFlags);
            _f_tour_average   = typeof(Tourism).GetField("m_AverageTourists", fieldFlags);
            _f_tour_attract   = typeof(Tourism).GetField("m_Attractiveness", fieldFlags);
            _f_danger_level   = typeof(DangerLevel).GetField("m_DangerLevel", fieldFlags);
            _f_milestone      = typeof(MilestoneLevel).GetField("m_AchievedMilestone", fieldFlags);
            _f_xp             = typeof(XP).GetField("m_XP", fieldFlags);

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

            // Drain completed storyteller runs every tick — cheap when idle (one
            // null check) and decoupled from the export gates below.
            Mod.Storyteller?.Tick();

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

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool hotkey = Input.GetKeyDown(KeyCode.E) && ctrl && shift;
            bool storytellerHotkey = Input.GetKeyDown(KeyCode.S) && ctrl && shift;
            bool intervalElapsed = settings.IntervalMinutes > 0
                && (DateTime.UtcNow - _lastExportUtc).TotalMinutes >= settings.IntervalMinutes;

            if (storytellerHotkey) TriggerStorytellerStubRun();

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

        // Stub run used to exercise the dispatcher before the real Anthropic
        // client lands (issue #4 will replace this with a RunFunc that calls
        // the API). The 10-second delay matches a realistic LLM call so the UI
        // status surface (#5) can be designed against representative timing.
        void TriggerStorytellerStubRun()
        {
            Mod.Storyteller?.Start("stub", async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return RunResult.Ok(filesWritten: 0);
            });
        }

        const string SchemaVersion = "0.1";

        void Export(string triggeredBy)
        {
            // OnUpdate's gate guarantees inGame + cityReady when we get here.
            if (!_cityComponentsLogged)
            {
                _cityComponentsLogged = true;
                LogCityComponentsOnce();
            }
            if (!_citizenFirstDiagged) DiagFirstCitizenOnce();

            int citizensTotal = _citizenQuery.CalculateEntityCount();
            string cityName = string.IsNullOrEmpty(_cityConfig.cityName) ? null : _cityConfig.cityName;
            string ingameDate = _timeSystem.GetCurrentDateTime().ToString("yyyy-MM-dd");
            long? money = _playerMoneyField != null
                ? Convert.ToInt64(_playerMoneyField.GetValue(EntityManager.GetComponentData<PlayerMoney>(_citySystem.City)))
                : (long?)null;

            // Read the four Population fields in a single boxed access.
            int? popTotal = null, popWithMoveIn = null, happiness = null, health = null;
            if (EntityManager.HasComponent<Population>(_citySystem.City))
            {
                object pop = EntityManager.GetComponentData<Population>(_citySystem.City);
                if (_f_pop_total != null)      popTotal      = Convert.ToInt32(_f_pop_total.GetValue(pop));
                if (_f_pop_withMoveIn != null) popWithMoveIn = Convert.ToInt32(_f_pop_withMoveIn.GetValue(pop));
                if (_f_pop_happiness != null)  happiness     = Convert.ToInt32(_f_pop_happiness.GetValue(pop));
                if (_f_pop_health != null)     health        = Convert.ToInt32(_f_pop_health.GetValue(pop));
            }

            int? touristsCurrent = null, touristsAverage = null, attractiveness = null;
            if (EntityManager.HasComponent<Tourism>(_citySystem.City))
            {
                object t = EntityManager.GetComponentData<Tourism>(_citySystem.City);
                if (_f_tour_current != null)  touristsCurrent  = Convert.ToInt32(_f_tour_current.GetValue(t));
                if (_f_tour_average != null)  touristsAverage  = Convert.ToInt32(_f_tour_average.GetValue(t));
                if (_f_tour_attract != null)  attractiveness   = Convert.ToInt32(_f_tour_attract.GetValue(t));
            }

            float? dangerLevel = null;
            if (_f_danger_level != null && EntityManager.HasComponent<DangerLevel>(_citySystem.City))
                dangerLevel = Convert.ToSingle(_f_danger_level.GetValue(EntityManager.GetComponentData<DangerLevel>(_citySystem.City)));

            int? milestoneLevel = null;
            if (_f_milestone != null && EntityManager.HasComponent<MilestoneLevel>(_citySystem.City))
                milestoneLevel = Convert.ToInt32(_f_milestone.GetValue(EntityManager.GetComponentData<MilestoneLevel>(_citySystem.City)));

            int? xp = null;
            if (_f_xp != null && EntityManager.HasComponent<XP>(_citySystem.City))
                xp = Convert.ToInt32(_f_xp.GetValue(EntityManager.GetComponentData<XP>(_citySystem.City)));
            var (districts, districtPrints) = CollectDistricts();
            var (buildings, buildingPrints) = CollectRenamedBuildings();
            object demographics = CollectDemographics();
            Dictionary<string, int> zoneCounts = CollectZoneCounts();
            var named = CollectOtherNamedEntities();
            DateTime currentIngameDate = _timeSystem.GetCurrentDateTime();

            long unixTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string snapshotId = $"snapshot-{unixTs}";

            object diff = _prevBuildings != null
                ? ComputeDiff(buildingPrints, zoneCounts, currentIngameDate,
                    named.roadsFingerprints, named.outsideConnectionsFingerprints,
                    named.waterSourcesFingerprints, districtPrints)
                : null;

            // Advance the previous-snapshot pointers for the next export.
            _prevBuildings = buildingPrints;
            _prevZoneCounts = zoneCounts;
            _prevRoads = named.roadsFingerprints;
            _prevOutsideConnections = named.outsideConnectionsFingerprints;
            _prevWaterSources = named.waterSourcesFingerprints;
            _prevDistricts = districtPrints;
            _prevIngameDate = currentIngameDate;
            _prevSnapshotId = snapshotId;

            var snapshot = new
            {
                schema_version = SchemaVersion,
                mod_version = typeof(Mod).Assembly.GetName().Version.ToString(),
                snapshot_id = snapshotId,
                session_id = Mod.SessionId,
                session_started_at_utc = Mod.SessionStartedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                captured_at_utc = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                captured_at_ingame = ingameDate,

                city = new
                {
                    name = cityName,
                    population_hud = popTotal,
                    population_with_move_in = popWithMoveIn,
                    citizens_total = citizensTotal,
                    money = money,
                    happiness = happiness,
                    health = health,
                    tourists_current = touristsCurrent,
                    tourists_average = touristsAverage,
                    attractiveness = attractiveness,
                    danger_level = dangerLevel,
                    milestone_level = milestoneLevel,
                    xp = xp,
                    zones = zoneCounts,
                },

                districts = districts,
                buildings = buildings,
                roads = named.roads,
                outside_connections = named.outsideConnections,
                water_sources = named.waterSources,
                other_named = named.other,
                citizens_sample = new object[0],

                demographics = demographics,
                diff = diff,

                trade = new
                {
                    imports = new object[0],
                    exports = new object[0],
                },

                services = new { },
            };

            string json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);

            // TODO: prefer a stable save GUID (Game.Assets.SaveGameMetadata / SaveInfo)
            // as the slug source once verified on Windows with ILSpy. City-name slug
            // assumes "1 save per city" — collisions across multiple Springfield saves
            // need the GUID upgrade. Data layout (per-city dir under ModsData) is
            // unchanged when that swap happens.
            string citySlug = Slugify(cityName) ?? "_unnamed";
            string dir = Path.Combine(EnvPath.kUserDataPath, "ModsData", nameof(CityStoryMod), citySlug);
            Directory.CreateDirectory(dir);
            EnsureCityScaffolded(dir);
            string snapshotsDir = Path.Combine(dir, "snapshots");
            Directory.CreateDirectory(snapshotsDir);
            string file = Path.Combine(snapshotsDir, $"{snapshotId}.json");
            File.WriteAllText(file, json);

            Mod.LastExportedCityDir = dir;

            _log.Info($"Exported snapshot ({triggeredBy}): citizens_total={citizensTotal}, districts={districts.Count}, named_buildings={buildings.Count} -> {file}");
        }

        // Marker file used to detect that the template has already been written into
        // the city dir. CLAUDE.md is part of every template and must be present for
        // the in-game agent to have its playbook; if it's missing we (re)extract.
        const string ScaffoldMarker = "CLAUDE.md";
        const string ResourcePrefix = "template/";

        void EnsureCityScaffolded(string cityDir)
        {
            if (File.Exists(Path.Combine(cityDir, ScaffoldMarker))) return;

            var asm = typeof(Mod).Assembly;
            int written = 0;
            foreach (string resourceName in asm.GetManifestResourceNames())
            {
                if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                string relative = resourceName.Substring(ResourcePrefix.Length);
                // Resource names use '/' regardless of host OS; rebuild the on-disk
                // path with the host separator via Path.Combine on the segments.
                string[] segments = relative.Split('/');
                string outPath = cityDir;
                for (int i = 0; i < segments.Length; i++) outPath = Path.Combine(outPath, segments[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                using (Stream src = asm.GetManifestResourceStream(resourceName))
                using (FileStream dst = File.Create(outPath))
                {
                    src.CopyTo(dst);
                }
                written++;
            }
            _log.Info($"Scaffolded city dir from template: {cityDir} ({written} files)");
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

        class DistrictAgg
        {
            public string Id;
            public string Name;
            public int Population;
            public int Jobs;
            public Dictionary<string, int> Zones = new Dictionary<string, int>();
            public List<string> NamedBuildingIds = new List<string>();
        }

        (List<object>, Dictionary<string, NameRef>) CollectDistricts()
        {
            var prints = new Dictionary<string, NameRef>();
            using var districts = _districtQuery.ToEntityArray(Allocator.Temp);
            if (districts.Length == 0) return (new List<object>(), prints);

            var aggs = new Dictionary<Entity, DistrictAgg>(districts.Length);
            for (int i = 0; i < districts.Length; i++)
            {
                var d = districts[i];
                string id = EntityId(d);
                string name = _nameSystem.GetRenderedLabelName(d);
                aggs[d] = new DistrictAgg { Id = id, Name = name };
                prints[id] = new NameRef { Id = id, Name = name };
            }

            // Pass 1: every building, attribute to its district.
            using (var buildings = _allBuildingsQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < buildings.Length; i++)
                {
                    var b = buildings[i];
                    var d = DistrictEntityOf(b);
                    if (d == Entity.Null || !aggs.TryGetValue(d, out var agg)) continue;

                    string type = BuildingTypeFromMarkers(b) ?? "other";
                    agg.Zones.TryGetValue(type, out int prev);
                    agg.Zones[type] = prev + 1;

                    if (EntityManager.HasComponent<CustomName>(b))
                        agg.NamedBuildingIds.Add(EntityId(b));
                }
            }

            // Pass 2: every citizen, attribute home + work to their district(s).
            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var hhField = typeof(HouseholdMember).GetField("m_Household", bf)
                ?? typeof(HouseholdMember).GetFields(bf).FirstOrDefault(f => f.FieldType == typeof(Entity));
            var workplaceField = typeof(Worker).GetField("m_Workplace", bf);

            using (var citizens = _citizenQuery.ToEntityArray(Allocator.Temp))
            {
                bool diaggedHH = false;
                for (int i = 0; i < citizens.Length; i++)
                {
                    var c = citizens[i];

                    // Home district via HouseholdMember -> Household -> PropertyRenter -> Building -> CurrentDistrict
                    if (hhField != null && EntityManager.HasComponent<HouseholdMember>(c))
                    {
                        if (!diaggedHH) { diaggedHH = true; DumpFieldsOnce<HouseholdMember>(c, "citizen"); }
                        var hhm = EntityManager.GetComponentData<HouseholdMember>(c);
                        var hh = (Entity)hhField.GetValue(hhm);
                        if (hh != Entity.Null && EntityManager.HasComponent<PropertyRenter>(hh))
                        {
                            var b = EntityManager.GetComponentData<PropertyRenter>(hh).m_Property;
                            if (b != Entity.Null)
                            {
                                var d = DistrictEntityOf(b);
                                if (d != Entity.Null && aggs.TryGetValue(d, out var agg))
                                    agg.Population++;
                            }
                        }
                    }

                    // Work district via Worker.m_Workplace (may be a company or a building directly)
                    if (workplaceField != null && EntityManager.HasComponent<Worker>(c))
                    {
                        var w = EntityManager.GetComponentData<Worker>(c);
                        var workplace = (Entity)workplaceField.GetValue(w);
                        if (workplace != Entity.Null)
                        {
                            Entity workBuilding = workplace;
                            if (EntityManager.HasComponent<PropertyRenter>(workplace))
                                workBuilding = EntityManager.GetComponentData<PropertyRenter>(workplace).m_Property;
                            if (workBuilding != Entity.Null)
                            {
                                var d = DistrictEntityOf(workBuilding);
                                if (d != Entity.Null && aggs.TryGetValue(d, out var agg))
                                    agg.Jobs++;
                            }
                        }
                    }
                }
            }

            var result = new List<object>(aggs.Count);
            foreach (var agg in aggs.Values)
            {
                result.Add(new
                {
                    id = agg.Id,
                    name = agg.Name,
                    population = agg.Population,
                    jobs = agg.Jobs,
                    zones = agg.Zones,
                    named_buildings = agg.NamedBuildingIds,
                });
            }
            return (result, prints);
        }

        Entity DistrictEntityOf(Entity building)
        {
            if (!EntityManager.HasComponent<CurrentDistrict>(building)) return Entity.Null;
            return EntityManager.GetComponentData<CurrentDistrict>(building).m_District;
        }

        struct NamedEntitiesResult
        {
            public List<object> roads;
            public List<object> outsideConnections;
            public List<object> waterSources;
            public List<object> other;
            public Dictionary<string, NameRef> roadsFingerprints;
            public Dictionary<string, NameRef> outsideConnectionsFingerprints;
            public Dictionary<string, NameRef> waterSourcesFingerprints;
        }

        // Sweeps every entity with a CustomName component and bins it. Things already
        // surfaced via buildings[] / districts[] are skipped to avoid duplication.
        // Classification by instance markers (more reliable than name parsing).
        NamedEntitiesResult CollectOtherNamedEntities()
        {
            var result = new NamedEntitiesResult
            {
                roads = new List<object>(),
                outsideConnections = new List<object>(),
                waterSources = new List<object>(),
                other = new List<object>(),
                roadsFingerprints = new Dictionary<string, NameRef>(),
                outsideConnectionsFingerprints = new Dictionary<string, NameRef>(),
                waterSourcesFingerprints = new Dictionary<string, NameRef>(),
            };
            using var entities = _customNameQuery.ToEntityArray(Allocator.Temp);
            int otherDiagBudget = 5;
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (EntityManager.HasComponent<Building>(e)) continue;
                if (EntityManager.HasComponent<District>(e)) continue;

                string id = EntityId(e);
                string name = _nameSystem.GetRenderedLabelName(e);
                var entry = new { id = id, name = name };
                var refp = new NameRef { Id = id, Name = name };

                if (EntityManager.HasComponent<Game.Net.Aggregate>(e))
                {
                    result.roads.Add(entry);
                    result.roadsFingerprints[id] = refp;
                }
                else if (EntityManager.HasComponent<Game.Objects.OutsideConnection>(e))
                {
                    result.outsideConnections.Add(entry);
                    result.outsideConnectionsFingerprints[id] = refp;
                }
                else if (EntityManager.HasComponent<Game.Simulation.WaterSourceData>(e))
                {
                    result.waterSources.Add(entry);
                    result.waterSourcesFingerprints[id] = refp;
                }
                else
                {
                    if (otherDiagBudget > 0)
                    {
                        otherDiagBudget--;
                        DumpComponentsOnce($"OtherNamed({name})", e);
                    }
                    result.other.Add(entry);
                }
            }
            return result;
        }

        // "Assets.NAME[Commercial_LiquorStore]" -> ("commercial", "LiquorStore")
        // Names that don't follow the prefab-key shape return (null, null).
        static (string sector, string subtype) ParseCompanyType(string name)
        {
            if (string.IsNullOrEmpty(name)) return (null, null);
            const string prefix = "Assets.NAME[";
            if (!name.StartsWith(prefix) || !name.EndsWith("]")) return (null, null);
            string inner = name.Substring(prefix.Length, name.Length - prefix.Length - 1);
            int underscore = inner.IndexOf('_');
            if (underscore <= 0 || underscore >= inner.Length - 1) return (null, null);
            return (inner.Substring(0, underscore).ToLowerInvariant(), inner.Substring(underscore + 1));
        }

        void LogCityComponentsOnce()
        {
            try
            {
                using var types = EntityManager.GetComponentTypes(_citySystem.City, Allocator.Temp);
                var names = new List<string>(types.Length);
                for (int i = 0; i < types.Length; i++)
                {
                    var managed = types[i].GetManagedType();
                    names.Add(managed != null ? managed.FullName : types[i].ToString());
                }
                names.Sort();
                _log.Info($"[diag] City singleton {EntityId(_citySystem.City)} has {names.Count} components: {string.Join(", ", names)}");
            }
            catch (Exception ex)
            {
                _log.Warn($"[diag] LogCityComponentsOnce failed: {ex.Message}");
            }

            // Field-level dump of selected components so we can map fields -> snapshot keys
            // in the next batch without guessing.
            LogComponentFieldsOnce<Population>();
            LogComponentFieldsOnce<Tourism>();
            LogComponentFieldsOnce<DangerLevel>();
            LogComponentFieldsOnce<MilestoneLevel>();
            LogComponentFieldsOnce<XP>();
        }

        void LogComponentFieldsOnce<T>() where T : unmanaged, IComponentData
        {
            try
            {
                if (!EntityManager.HasComponent<T>(_citySystem.City))
                {
                    _log.Info($"[diag] {typeof(T).FullName}: NOT on City singleton");
                    return;
                }
                var data = EntityManager.GetComponentData<T>(_citySystem.City);
                var fields = typeof(T).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var pairs = fields.Select(f => $"{f.Name}:{f.FieldType.Name}={f.GetValue(data)}").ToArray();
                _log.Info($"[diag] {typeof(T).FullName} fields: {string.Join(", ", pairs)}");
            }
            catch (Exception ex)
            {
                _log.Warn($"[diag] Field dump of {typeof(T).FullName} failed: {ex.Message}");
            }
        }

        (List<object>, Dictionary<string, BuildingFingerprint>) CollectRenamedBuildings()
        {
            var result = new List<object>();
            var prints = new Dictionary<string, BuildingFingerprint>();
            using var entities = _renamedBuildingQuery.ToEntityArray(Allocator.Temp);
            // Cap the per-entity component diag at 5 buildings to avoid log spam in large cities.
            int diagBudget = _buildingFirstDiagged ? 0 : 5;
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];

                string prefabName = null;
                Entity prefabEntity = Entity.Null;
                if (EntityManager.HasComponent<PrefabRef>(e))
                {
                    prefabEntity = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    if (prefabEntity != Entity.Null && _prefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefabBase))
                    {
                        prefabName = prefabBase.name;
                    }
                }

                // Classify primarily from instance-marker components (most reliable),
                // fall back to heuristic name parsing if no marker matched.
                string type = BuildingTypeFromMarkers(e) ?? BuildingTypeFromPrefabName(prefabName);

                // Diag: dump components of up to 5 renamed buildings + their prefabs on the
                // first export so we get the full marker-taxonomy in one shot.
                if (diagBudget > 0)
                {
                    diagBudget--;
                    DumpComponentsOnce($"Building({_nameSystem.GetRenderedLabelName(e)} / prefab={prefabName})", e);
                    if (prefabEntity != Entity.Null) DumpComponentsOnce($"BuildingPrefab({prefabName})", prefabEntity);
                    // Also dump the field layout of building-stat components, once per type.
                    // Efficiency turned out to not be IComponentData (likely IBufferElementData);
                    // skipping here and investigating next batch.
                    DumpFieldsOnce<BuildingCondition>(e, "building");
                    DumpFieldsOnce<CitizenPresence>(e, "building");
                }

                int? condition = ReadFirstInt<BuildingCondition>(e);
                // CitizenPresence has m_Delta (SByte, recent change) and m_Presence (Byte, current
                // headcount-ish). We want the absolute presence value.
                int? citizensPresent = ReadIntField<CitizenPresence>(e, "m_Presence");
                int? renterCount = EntityManager.HasBuffer<Renter>(e)
                    ? EntityManager.GetBuffer<Renter>(e, isReadOnly: true).Length
                    : (int?)null;

                // Walk the Renter buffer and find the first CompanyData entry, folding its
                // info inline. Most renamed buildings are 1:1 with a company (signature
                // industrials, named extractors, custom-named shops); we surface that one.
                object company = null;
                string companySubtype = null;
                if (EntityManager.HasBuffer<Renter>(e))
                {
                    var renters = EntityManager.GetBuffer<Renter>(e, isReadOnly: true);
                    for (int j = 0; j < renters.Length; j++)
                    {
                        var renter = renters[j].m_Renter;
                        if (renter == Entity.Null || !EntityManager.HasComponent<CompanyData>(renter)) continue;
                        int headcount = EntityManager.HasBuffer<Employee>(renter)
                            ? EntityManager.GetBuffer<Employee>(renter, isReadOnly: true).Length
                            : 0;
                        string rawCompanyName = _nameSystem.GetRenderedLabelName(renter);
                        var (cSector, cSubtype) = ParseCompanyType(rawCompanyName);
                        companySubtype = cSubtype;
                        company = new
                        {
                            id = EntityId(renter),
                            name = rawCompanyName,
                            custom_named = EntityManager.HasComponent<CustomName>(renter),
                            sector = cSector,
                            subtype = cSubtype,
                            headcount = headcount,
                        };
                        break;
                    }
                }

                string id = EntityId(e);
                string renderedName = _nameSystem.GetRenderedLabelName(e);
                string districtId = DistrictIdOf(e);

                result.Add(new
                {
                    id = id,
                    name = renderedName,
                    custom_named = true,
                    prefab_name = prefabName,
                    type = type,
                    efficiency = (float?)null,
                    condition = condition,
                    citizens_present = citizensPresent,
                    renter_count = renterCount,
                    company = company,
                    district_id = districtId,
                });
                prints[id] = new BuildingFingerprint
                {
                    Id = id,
                    Name = renderedName,
                    Type = type,
                    CompanySubtype = companySubtype,
                    DistrictId = districtId,
                    HasCompany = company != null,
                };
            }
            if (!_buildingFirstDiagged && entities.Length > 0) _buildingFirstDiagged = true;
            return (result, prints);
        }

        object ComputeDiff(
            Dictionary<string, BuildingFingerprint> current,
            Dictionary<string, int> currentZoneCounts,
            DateTime currentIngameDate,
            Dictionary<string, NameRef> currentRoads,
            Dictionary<string, NameRef> currentOutsideConnections,
            Dictionary<string, NameRef> currentWaterSources,
            Dictionary<string, NameRef> currentDistricts)
        {
            var added = new List<object>();
            var removed = new List<object>();
            var changed = new List<object>();

            foreach (var kv in current)
            {
                if (!_prevBuildings.TryGetValue(kv.Key, out var prev))
                {
                    added.Add(new { id = kv.Value.Id, name = kv.Value.Name, type = kv.Value.Type });
                    continue;
                }
                var changes = new Dictionary<string, object>();
                if (kv.Value.Name != prev.Name)
                    changes["name"] = new { from = prev.Name, to = kv.Value.Name };
                if (kv.Value.Type != prev.Type)
                    changes["type"] = new { from = prev.Type, to = kv.Value.Type };
                if (kv.Value.DistrictId != prev.DistrictId)
                    changes["district_id"] = new { from = prev.DistrictId, to = kv.Value.DistrictId };
                if (kv.Value.CompanySubtype != prev.CompanySubtype)
                    changes["company_subtype"] = new { from = prev.CompanySubtype, to = kv.Value.CompanySubtype };
                if (kv.Value.HasCompany != prev.HasCompany)
                    changes["has_company"] = new { from = prev.HasCompany, to = kv.Value.HasCompany };
                if (changes.Count > 0)
                    changed.Add(new { id = kv.Value.Id, name = kv.Value.Name, changes });
            }

            foreach (var kv in _prevBuildings)
            {
                if (!current.ContainsKey(kv.Key))
                {
                    removed.Add(new { id = kv.Value.Id, name = kv.Value.Name, type = kv.Value.Type });
                }
            }

            int? ingameDaysElapsed = _prevIngameDate.HasValue
                ? (int)(currentIngameDate - _prevIngameDate.Value).TotalDays
                : (int?)null;

            // Only emit zones that actually changed.
            var zonesDelta = new Dictionary<string, object>();
            if (_prevZoneCounts != null)
            {
                foreach (var kv in currentZoneCounts)
                {
                    _prevZoneCounts.TryGetValue(kv.Key, out int prev);
                    if (prev != kv.Value)
                        zonesDelta[kv.Key] = new { from = prev, to = kv.Value, delta = kv.Value - prev };
                }
                foreach (var kv in _prevZoneCounts)
                {
                    if (!currentZoneCounts.ContainsKey(kv.Key) && kv.Value != 0)
                        zonesDelta[kv.Key] = new { from = kv.Value, to = 0, delta = -kv.Value };
                }
            }

            var roadsDiff = DiffNameRefs(_prevRoads, currentRoads);
            var ocDiff = DiffNameRefs(_prevOutsideConnections, currentOutsideConnections);
            var wsDiff = DiffNameRefs(_prevWaterSources, currentWaterSources);
            var districtsDiff = DiffNameRefs(_prevDistricts, currentDistricts);

            return new
            {
                since_snapshot_id = _prevSnapshotId,
                since_captured_at_ingame = _prevIngameDate?.ToString("yyyy-MM-dd"),
                ingame_days_elapsed = ingameDaysElapsed,
                buildings = new { added, removed, changed },
                zones_delta = zonesDelta,
                roads = new { added = roadsDiff.added, removed = roadsDiff.removed, changed = roadsDiff.changed },
                outside_connections = new { added = ocDiff.added, removed = ocDiff.removed, changed = ocDiff.changed },
                water_sources = new { added = wsDiff.added, removed = wsDiff.removed, changed = wsDiff.changed },
                districts = new { added = districtsDiff.added, removed = districtsDiff.removed, changed = districtsDiff.changed },
            };
        }

        // Generic added/removed/renamed diff for the simple id+name entity classes.
        (List<object> added, List<object> removed, List<object> changed) DiffNameRefs(
            Dictionary<string, NameRef> prev,
            Dictionary<string, NameRef> current)
        {
            var added = new List<object>();
            var removed = new List<object>();
            var changed = new List<object>();
            if (prev == null) prev = new Dictionary<string, NameRef>();

            foreach (var kv in current)
            {
                if (!prev.TryGetValue(kv.Key, out var p))
                    added.Add(new { id = kv.Value.Id, name = kv.Value.Name });
                else if (p.Name != kv.Value.Name)
                    changed.Add(new { id = kv.Value.Id, name = kv.Value.Name, changes = new { name = new { from = p.Name, to = kv.Value.Name } } });
            }
            foreach (var kv in prev)
            {
                if (!current.ContainsKey(kv.Key))
                    removed.Add(new { id = kv.Value.Id, name = kv.Value.Name });
            }
            return (added, removed, changed);
        }

        // Walks every Building entity (excluding prefabs/deleted/temp) and classifies
        // by the existing instance-marker check. Cheap relative to the 5-min export
        // throttle, even on cities with thousands of buildings.
        Dictionary<string, int> CollectZoneCounts()
        {
            var counts = new Dictionary<string, int>
            {
                ["residential"] = 0,
                ["commercial"] = 0,
                ["industrial"] = 0,
                ["office"] = 0,
                ["extractor"] = 0,
                ["service"] = 0,
                ["transformer"] = 0,
                ["water_pumping"] = 0,
                ["other"] = 0,
            };
            using var entities = _allBuildingsQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                string type = BuildingTypeFromMarkers(entities[i]) ?? "other";
                counts.TryGetValue(type, out int prev);
                counts[type] = prev + 1;
            }
            return counts;
        }

        int? ReadFirstInt<T>(Entity e) where T : unmanaged, IComponentData
        {
            if (!EntityManager.HasComponent<T>(e)) return null;
            var field = typeof(T).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType == typeof(int) || f.FieldType == typeof(short)
                                  || f.FieldType == typeof(byte) || f.FieldType == typeof(sbyte));
            if (field == null) return null;
            return Convert.ToInt32(field.GetValue(EntityManager.GetComponentData<T>(e)));
        }

        int? ReadIntField<T>(Entity e, string fieldName) where T : unmanaged, IComponentData
        {
            if (!EntityManager.HasComponent<T>(e)) return null;
            var field = typeof(T).GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) return null;
            return Convert.ToInt32(field.GetValue(EntityManager.GetComponentData<T>(e)));
        }

        const int MaxCitizensSampled = 5000;

        // Aggregate citizen-level signals into a snapshot demographics block.
        // CitizenFlags (m_State) is treated opaquely: we split its ToString into named
        // flags (AgeBit1, Male, EducationBit2, ...) and count each. The agent can
        // interpret the flag names without us needing to decode the bit layout.
        object CollectDemographics()
        {
            var flagCounts = new Dictionary<string, int>();
            long wellbeingSum = 0, healthSum = 0;
            int wellbeingN = 0, healthN = 0;
            int employed = 0;
            int birthdayMin = int.MaxValue, birthdayMax = int.MinValue;
            bool sawBirthday = false;

            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var stateField     = typeof(Citizen).GetField("m_State", bf);
            var wellbeingField = typeof(Citizen).GetField("m_WellBeing", bf);
            var healthField    = typeof(Citizen).GetField("m_Health", bf);
            var birthdayField  = typeof(Citizen).GetField("m_BirthDay", bf);
            var workerWorkplaceField = typeof(Worker).GetField("m_Workplace", bf);

            using var entities = _citizenQuery.ToEntityArray(Allocator.Temp);
            int totalEntities = entities.Length;
            int cap = Math.Min(totalEntities, MaxCitizensSampled);

            bool diagged = false;
            for (int i = 0; i < cap; i++)
            {
                var e = entities[i];
                if (!EntityManager.HasComponent<Citizen>(e)) continue;
                var c = EntityManager.GetComponentData<Citizen>(e);

                if (stateField != null)
                {
                    string flags = stateField.GetValue(c)?.ToString() ?? "";
                    foreach (var part in flags.Split(','))
                    {
                        var trimmed = part.Trim();
                        if (trimmed.Length == 0) continue;
                        flagCounts.TryGetValue(trimmed, out int prev);
                        flagCounts[trimmed] = prev + 1;
                    }
                }
                if (wellbeingField != null) { wellbeingSum += Convert.ToInt64(wellbeingField.GetValue(c)); wellbeingN++; }
                if (healthField != null)    { healthSum    += Convert.ToInt64(healthField.GetValue(c));    healthN++; }
                if (birthdayField != null)
                {
                    int bday = Convert.ToInt32(birthdayField.GetValue(c));
                    if (bday < birthdayMin) birthdayMin = bday;
                    if (bday > birthdayMax) birthdayMax = bday;
                    sawBirthday = true;
                }

                // Worker / HasJobSeeker / CarKeeper / CrimeVictim etc. are passive marker
                // components on (nearly) every citizen, not state flags. The real "is
                // employed" signal is Worker.m_Workplace pointing at a non-null entity.
                if (workerWorkplaceField != null && EntityManager.HasComponent<Worker>(e))
                {
                    if (!diagged) { diagged = true; DumpFieldsOnce<Worker>(e, "citizen"); }
                    var w = EntityManager.GetComponentData<Worker>(e);
                    var workplace = (Entity)workerWorkplaceField.GetValue(w);
                    if (workplace != Entity.Null) employed++;
                }
            }

            return new
            {
                citizens_total = totalEntities,
                sampled = cap,
                truncated = totalEntities > cap,

                flag_counts = flagCounts,
                avg_wellbeing = wellbeingN > 0 ? (double?)((double)wellbeingSum / wellbeingN) : null,
                avg_health    = healthN > 0    ? (double?)((double)healthSum    / healthN)    : null,
                birthday_min = sawBirthday ? (int?)birthdayMin : null,
                birthday_max = sawBirthday ? (int?)birthdayMax : null,

                // Citizens with Worker.m_Workplace pointing at a non-null entity.
                // Worker is added on employment, so this equals "currently employed".
                // Non-employed = citizens_total - employed (includes kids, retirees, students,
                // job-seekers; can't be split further without parsing m_State age bits).
                employed = employed,
            };
        }

        // One-time dump of an arbitrary Citizen's components and the field layout of
        // the Citizen component itself. Goal: figure out where age / education /
        // wealth / household linkage live so the next batch can aggregate demographics.
        void DiagFirstCitizenOnce()
        {
            using var entities = _citizenQuery.ToEntityArray(Allocator.Temp);
            if (entities.Length == 0) return;  // try again next export
            _citizenFirstDiagged = true;
            var e = entities[0];
            DumpComponentsOnce($"Citizen first sample", e);
            DumpFieldsOnce<Citizen>(e, "citizen");
        }

        void DumpFieldsOnce<T>(Entity sample, string label) where T : unmanaged, IComponentData
        {
            if (_fieldDumpsSeen.Contains(typeof(T))) return;
            // Don't mark as seen if the component isn't on THIS sample - the next
            // building processed might be a better candidate (e.g. BuildingCondition
            // is only present on properties, not on civic services).
            if (!EntityManager.HasComponent<T>(sample)) return;
            _fieldDumpsSeen.Add(typeof(T));
            try
            {
                var data = EntityManager.GetComponentData<T>(sample);
                var fields = typeof(T).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var pairs = fields.Select(f => $"{f.Name}:{f.FieldType.Name}={f.GetValue(data)}").ToArray();
                _log.Info($"[diag] {typeof(T).FullName} ({label}) fields: {string.Join(", ", pairs)}");
            }
            catch (Exception ex)
            {
                _log.Warn($"[diag] DumpFieldsOnce<{typeof(T).Name}> failed: {ex.Message}");
            }
        }

        // Instance-marker-based type classification. Adds entries as new markers
        // are discovered via the [diag] Building dumps.
        string BuildingTypeFromMarkers(Entity b)
        {
            // Specific service markers (more specific than just "service")
            if (EntityManager.HasComponent<Game.Buildings.Transformer>(b)) return "transformer";
            if (EntityManager.HasComponent<Game.Buildings.WaterPumpingStation>(b)) return "water_pumping";
            // Property class markers (extractor is a refinement of industrial; check first)
            if (EntityManager.HasComponent<ExtractorProperty>(b)) return "extractor";
            if (EntityManager.HasComponent<IndustrialProperty>(b)) return "industrial";
            if (EntityManager.HasComponent<CommercialProperty>(b)) return "commercial";
            if (EntityManager.HasComponent<OfficeProperty>(b)) return "office";
            if (EntityManager.HasComponent<ResidentialProperty>(b)) return "residential";
            // Generic service fallback: city pays upkeep and it's not above
            if (EntityManager.HasComponent<Game.City.CityServiceUpkeep>(b)) return "service";
            return null;
        }

        // PrefabSystem.GetPrefabName returns raw asset names like "ElectricityTransformer01",
        // "WaterPumpingStation01", "LowResidentialRowhouse02". Best-effort classification
        // by substring; if no rule matches we return null and the agent can categorize
        // from prefab_name + the diag log's component dump.
        static string BuildingTypeFromPrefabName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            string n = prefabName.ToLowerInvariant();
            if (n.Contains("residential") || n.Contains("rowhouse") || n.Contains("apartment") || n.Contains("housing")) return "residential";
            if (n.Contains("commercial")) return "commercial";
            if (n.Contains("industrial")) return "industrial";
            if (n.Contains("office")) return "office";
            if (n.Contains("extractor") || n.Contains("farm") || n.Contains("forestry") || n.Contains("mine")) return "extractor";
            if (n.Contains("park")) return "park";
            if (n.Contains("landmark") || n.Contains("signature")) return "landmark";
            // Common service prefab name fragments
            if (n.Contains("transformer") || n.Contains("powerline") || n.Contains("powerplant") || n.Contains("substation") || n.Contains("solarpower") || n.Contains("windturbine")) return "service";
            if (n.Contains("water") || n.Contains("sewage") || n.Contains("pumpingstation")) return "service";
            if (n.Contains("police") || n.Contains("fire") || n.Contains("hospital") || n.Contains("school") || n.Contains("university") || n.Contains("clinic")) return "service";
            if (n.Contains("garbage") || n.Contains("landfill") || n.Contains("incinerator") || n.Contains("recycling")) return "service";
            return null;
        }

        void DumpComponentsOnce(string label, Entity e)
        {
            try
            {
                using var types = EntityManager.GetComponentTypes(e, Allocator.Temp);
                var names = new List<string>(types.Length);
                for (int i = 0; i < types.Length; i++)
                {
                    var managed = types[i].GetManagedType();
                    names.Add(managed != null ? managed.FullName : types[i].ToString());
                }
                names.Sort();
                _log.Info($"[diag] {label} {EntityId(e)} has {names.Count} components: {string.Join(", ", names)}");
            }
            catch (Exception ex)
            {
                _log.Warn($"[diag] DumpComponentsOnce({label}) failed: {ex.Message}");
            }
        }

        static string EntityId(Entity e) => $"{e.Index}-{e.Version}";

        string DistrictIdOf(Entity building)
        {
            var d = DistrictEntityOf(building);
            return d != Entity.Null ? EntityId(d) : null;
        }
    }
}
