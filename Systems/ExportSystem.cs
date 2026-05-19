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
        EntityQuery _renamedBuildingQuery;
        bool _cityComponentsLogged;
        bool _buildingFirstDiagged;
        bool _citizenFirstDiagged;
        readonly HashSet<Type> _fieldDumpsSeen = new HashSet<Type>();
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
            List<object> districts = CollectDistricts();
            List<object> buildings = CollectRenamedBuildings();
            object demographics = CollectDemographics();

            long unixTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string snapshotId = $"snapshot-{unixTs}";

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
                },

                districts = districts,
                buildings = buildings,
                citizens_sample = new object[0],

                demographics = demographics,

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

            _log.Info($"Exported snapshot ({triggeredBy}): citizens_total={citizensTotal}, districts={districts.Count}, named_buildings={buildings.Count} -> {file}");
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

        List<object> CollectRenamedBuildings()
        {
            var result = new List<object>();
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

                result.Add(new
                {
                    id = EntityId(e),
                    name = _nameSystem.GetRenderedLabelName(e),
                    custom_named = true,
                    prefab_name = prefabName,
                    type = type,
                    efficiency = (float?)null,
                    condition = condition,
                    citizens_present = citizensPresent,
                    renter_count = renterCount,
                    company = company,
                    district_id = DistrictIdOf(e),
                });
            }
            if (!_buildingFirstDiagged && entities.Length > 0) _buildingFirstDiagged = true;
            return result;
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
            if (!EntityManager.HasComponent<CurrentDistrict>(building)) return null;
            var d = EntityManager.GetComponentData<CurrentDistrict>(building).m_District;
            return d != Entity.Null ? EntityId(d) : null;
        }
    }
}
