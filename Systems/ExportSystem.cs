using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
        EntityQuery _allBuildingsQuery;       // shared by zone-count bulk pass + district zones
        EntityQuery _customNameQuery;         // backs outside_connections + water_sources
        EntityQuery _districtQuery;           // per-district zone tracking
        EntityQuery _namedBuildingQuery;      // CustomName-tagged buildings for churn diff
        bool _cityComponentsLogged;
        bool _citizenFirstDiagged;
        readonly HashSet<Type> _fieldDumpsSeen = new HashSet<Type>();

        // Previous-snapshot state for the diff block. Reset on mod reload
        // (CS2 launch). Cross-session diffs need a future enhancement to
        // read the last on-disk snapshot on first export.
        Dictionary<string, int> _prevZoneCounts;
        Dictionary<string, NameRef> _prevOutsideConnections;
        Dictionary<string, NameRef> _prevWaterSources;
        Dictionary<string, Dictionary<string, int>> _prevDistrictZones;   // districtName → {zoneType → count}
        Dictionary<string, NamedBuilding> _prevNamedBuildings;            // entityId → fingerprint
        Dictionary<string, BuildingFingerprint> _prevAllBuildings;        // entityId → slim fingerprint for churn diff
        DateTime? _prevIngameDate;
        string _prevSnapshotId;

        // Slim fingerprint for every building, kept between snapshots so the
        // diff can surface per-district demolitions and constructions
        // separately from the existing net `district_zone_deltas`. The agent
        // needs the *churn* (e.g. "6 down, 4 up") to write displacement
        // stories, not just the net change.
        struct BuildingFingerprint
        {
            public string Type;             // "residential" / "industrial" / ...
            public string DistrictName;     // null if outside any district
        }

        // Slim per-export record of a CustomName-tagged building. Backs the
        // churn diff that surfaces "Inger Brevik Elementary opened in March".
        // Type comes from BuildingTypeFromMarkers so the agent can react
        // differently to a new school vs a new low-density house being named
        // by the player.
        struct NamedBuilding
        {
            public string Id;
            public string Name;
            public string Type;
            public string DistrictName;   // null if not in any district
        }

        // Lightweight id+name pair used for renaming/add/remove detection on
        // the simple entity classes that stay in the snapshot.
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

        // Game.UI.MapMetadataSystem — held reflectively so we don't pull in
        // Game.UI as a compile-time reference for one type. Resolved lazily
        // on first export. The [diag] dump on first run surfaced these
        // additional fields beyond the original mapName.
        object _mapMetadataSystem;
        PropertyInfo _p_mapName;
        PropertyInfo _p_mapTheme;
        PropertyInfo _p_mapLatitude;
        PropertyInfo _p_mapLongitude;
        PropertyInfo _p_mapCloudiness;
        PropertyInfo _p_mapPrecipitation;
        PropertyInfo _p_mapGroundWater;
        PropertyInfo _p_mapSurfaceWater;
        PropertyInfo _p_mapTemperatureRange;     // returns Bounds1 { float min, max }
        bool _mapMetadataLogged;

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

        // Save-load transition detection. Flips true the first tick OnUpdate sees
        // inGame+cityReady; flips back to false on any tick where the gate fails
        // (main menu, loading screen, editor). The false→true edge is what we
        // treat as "save was just loaded" — forces an export, and (if the setting
        // is on) writes an open sessions/ stub.
        bool _inGameLastTick;

        // One-tick deferral for Carto's export call. Set at the end of a snapshot
        // export; consumed at the start of the next OnUpdate. The deferral lets
        // PromptUISystem flush the cartoExporting=true binding to Coherent UI on
        // frame N so the indicator paints before the synchronous Carto pipeline
        // blocks the main thread on frame N+1. Carto's ECS queries (EntityQuery,
        // EntityManager) are main-thread-only, so true async isn't possible.
        string _pendingCartoDir;

        // Pending screenshot capture, queued by the first-Carto-on-new-city
        // auto-trigger. We defer by a few ticks so the storyteller window's
        // map-button click handler (if it was the trigger) has time to close
        // its UI flash, and so the snapshot+Carto pipeline runs first.
        string _pendingScreenshotPath;
        int _pendingScreenshotTicksRemaining;

        PromptUISystem _promptUI;

        // Pollution systems. Each holds a NativeArray<T> cell-grid map of
        // Int16 pollution values, plus a GetPollution(float3, map) lookup.
        // Resolved in OnCreate so per-building sampling stays cheap. The
        // m_Map field is non-public, so we grab it reflectively each export.
        AirPollutionSystem _airPollutionSystem;
        GroundPollutionSystem _groundPollutionSystem;
        NoisePollutionSystem _noisePollutionSystem;
        FieldInfo _f_airPollutionMap;
        FieldInfo _f_groundPollutionMap;
        FieldInfo _f_noisePollutionMap;

        // Statistics system. Concrete class fails the GetTypes() load
        // (depends on Burst-generated types), so we resolve it reflectively
        // and cast to the loadable interface for population churn reads.
        ICityStatisticsSystem _cityStatistics;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citizenQuery = GetEntityQuery(ComponentType.ReadOnly<Citizen>());
            _districtQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<District>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            // Story-worthy buildings: anything player-renamed (CustomName),
            // OR a city-paid civic facility (CityServiceUpkeep — catches
            // schools, fire stations, police, hospitals, transformers, water
            // pumping, etc.), OR a specialized extractor (private but
            // landscape-significant: oil fields, mines, farms, forestries).
            // CS2 sometimes leaves CustomName unattached on freshly-placed
            // civic buildings for a while after construction completes;
            // the service-marker filter catches them anyway.
            _namedBuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>() },
                Any = new[]
                {
                    ComponentType.ReadOnly<CustomName>(),
                    ComponentType.ReadOnly<Game.City.CityServiceUpkeep>(),
                    ComponentType.ReadOnly<ExtractorProperty>(),
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
            _promptUI = World.GetOrCreateSystemManaged<PromptUISystem>();

            // Pollution systems — same World.GetOrCreate pattern as any other
            // ECS system. Field-resolving m_Map at OnCreate time is cheap and
            // keeps the per-export sampling loop free of reflection cost.
            try
            {
                _airPollutionSystem    = World.GetOrCreateSystemManaged<AirPollutionSystem>();
                _groundPollutionSystem = World.GetOrCreateSystemManaged<GroundPollutionSystem>();
                _noisePollutionSystem  = World.GetOrCreateSystemManaged<NoisePollutionSystem>();
                const BindingFlags pollutionFlags = BindingFlags.NonPublic | BindingFlags.Instance;
                _f_airPollutionMap    = typeof(AirPollutionSystem).GetField("m_Map", pollutionFlags);
                _f_groundPollutionMap = typeof(GroundPollutionSystem).GetField("m_Map", pollutionFlags);
                _f_noisePollutionMap  = typeof(NoisePollutionSystem).GetField("m_Map", pollutionFlags);
                if (_f_airPollutionMap == null || _f_groundPollutionMap == null || _f_noisePollutionMap == null)
                    _log.Warn("Pollution m_Map field not resolved; pollution block will stay null.");
            }
            catch (Exception ex)
            {
                _log.Warn($"Pollution system resolution failed: {ex.Message}; pollution block will stay null.");
            }

            // CityStatisticsSystem (concrete type isn't loadable here — its
            // Burst-generated dependencies fail GetTypes — but the interface
            // is). Resolve reflectively via NameSystem's assembly, then cast
            // to ICityStatisticsSystem for the readable API. Failure leaves
            // city.churn null and is non-fatal.
            try
            {
                Type statsType = typeof(NameSystem).Assembly.GetType("Game.Simulation.CityStatisticsSystem");
                if (statsType != null)
                {
                    var getOrCreate = typeof(World).GetMethod("GetOrCreateSystemManaged", Type.EmptyTypes);
                    if (getOrCreate != null)
                    {
                        object sys = getOrCreate.MakeGenericMethod(statsType).Invoke(World, null);
                        _cityStatistics = sys as ICityStatisticsSystem;
                    }
                }
                if (_cityStatistics == null)
                    _log.Warn("CityStatisticsSystem not resolved; city.churn will stay null.");
            }
            catch (Exception ex)
            {
                _log.Warn($"CityStatisticsSystem resolution failed: {ex.Message}; city.churn will stay null.");
            }

            // MapMetadataSystem lives in Game.UI; resolve reflectively from
            // NameSystem's assembly (also Game.UI, already referenced) to
            // avoid pulling more of Game.UI as a compile reference. Carto
            // accesses the same system via the standard ECS GetOrCreate
            // pattern (see Carto's Instance.cs:61). Failure is non-fatal —
            // map.* fields stay null and a warning lands in the log.
            try
            {
                Type mapMetaType = typeof(NameSystem).Assembly.GetType("Game.UI.MapMetadataSystem");
                if (mapMetaType != null)
                {
                    var getOrCreate = typeof(World).GetMethod("GetOrCreateSystemManaged", Type.EmptyTypes);
                    if (getOrCreate != null)
                    {
                        _mapMetadataSystem = getOrCreate.MakeGenericMethod(mapMetaType).Invoke(World, null);
                        const BindingFlags pf = BindingFlags.Public | BindingFlags.Instance;
                        _p_mapName             = mapMetaType.GetProperty("mapName", pf);
                        _p_mapTheme            = mapMetaType.GetProperty("theme", pf);
                        _p_mapLatitude         = mapMetaType.GetProperty("latitude", pf);
                        _p_mapLongitude        = mapMetaType.GetProperty("longitude", pf);
                        _p_mapCloudiness       = mapMetaType.GetProperty("cloudiness", pf);
                        _p_mapPrecipitation    = mapMetaType.GetProperty("precipitation", pf);
                        _p_mapGroundWater      = mapMetaType.GetProperty("groundWaterAvailability", pf);
                        _p_mapSurfaceWater     = mapMetaType.GetProperty("surfaceWaterAvailability", pf);
                        _p_mapTemperatureRange = mapMetaType.GetProperty("temperatureRange", pf);
                    }
                }
                if (_mapMetadataSystem == null || _p_mapName == null)
                    _log.Warn("MapMetadataSystem or mapName property not found; map.name will stay null.");
            }
            catch (Exception ex)
            {
                _log.Warn($"MapMetadataSystem resolution failed: {ex.Message}; map.* will stay null.");
            }

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

            // Drain a pending screenshot capture. Counts down a few ticks so
            // any UI flash from a button-click trigger has time to settle
            // before we grab the framebuffer. Failure is non-fatal — we just
            // log and move on; the spatial data is the primary anchor.
            if (_pendingScreenshotPath != null)
            {
                if (_pendingScreenshotTicksRemaining > 0)
                {
                    _pendingScreenshotTicksRemaining--;
                }
                else
                {
                    string path = _pendingScreenshotPath;
                    _pendingScreenshotPath = null;
                    ScreenshotCapture.TryCaptureToFile(path, _log);
                }
            }

            // Drain any Carto export deferred from the previous tick. The deferral
            // gives Coherent UI one frame to paint the cartoExporting indicator
            // before this synchronous call blocks the main thread.
            if (_pendingCartoDir != null)
            {
                string dir = _pendingCartoDir;
                _pendingCartoDir = null;
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var carto = CartoBridge.TryExport(dir, _log);
                    sw.Stop();
                    if (carto == null)
                    {
                        _log.Info("Carto bridge unavailable; skipping spatial export.");
                    }
                    else if (carto.Success)
                    {
                        // Carto's FilesWritten counts NEW files only (it diffs the
                        // output directory before/after). Overwrites of existing
                        // files don't appear in the count, so "0 files" is the
                        // common case once the directory has been populated once.
                        // Report it as "completed" + duration instead — much
                        // less misleading when reviewing logs.
                        int n = carto.FilesWritten.Length;
                        string detail = n > 0 ? $"{n} new file(s)" : "overwrote existing";
                        _log.Info($"Carto export completed in {sw.ElapsedMilliseconds}ms ({detail}) → {dir}.");

                        // Carto wrote raw GeoJSON; now produce the
                        // storyteller-facing markdown chunks the agent
                        // actually reads (carto/processed/index.md +
                        // carto/processed/districts/<slug>.md).
                        var procSw = System.Diagnostics.Stopwatch.StartNew();
                        var procResult = CartoProcessor.Process(dir);
                        procSw.Stop();
                        if (procResult.Success)
                        {
                            _log.Info($"CartoProcessor wrote {procResult.DistrictsWritten} district chunk(s), {procResult.NamedBuildingsAssigned} buildings assigned, in {procSw.ElapsedMilliseconds}ms → {procResult.IndexPath}");
                        }
                        else
                        {
                            _log.Warn($"CartoProcessor failed: {procResult.ErrorMessage}");
                        }
                    }
                    else
                    {
                        _log.Warn($"Carto export failed: {carto.ErrorMessage}");
                    }
                }
                finally
                {
                    _promptUI?.SetCartoExporting(false);
                }
            }

            var settings = Mod.Settings;
            if (settings == null) return;

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
                _inGameLastTick = false;
                return;
            }

            // false → true edge on the in-game gate. Treated as a save-load (also
            // covers a fresh new city or returning from the main menu). Forces an
            // export this tick so the snapshot reflects freshly-loaded state.
            bool saveLoadTransition = !_inGameLastTick;
            _inGameLastTick = true;

            // Clear the Ghostwriter chat history on the save-load edge. A
            // fresh save (or a new city) is a different city's context — the
            // previous conversation would reference state that no longer
            // applies. Wipe once per edge.
            if (saveLoadTransition)
            {
                _promptUI?.ClearChatHistory("save-load edge");
            }

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool hotkey = Input.GetKeyDown(KeyCode.E) && ctrl && shift;

            // Ctrl+Shift+M — manual map screenshot. Captures whatever's on
            // screen right now (so the player should set up the map view
            // first, hide UI, then press). Independent of the export
            // pipeline so it doesn't force a snapshot.
            if (Input.GetKeyDown(KeyCode.M) && ctrl && shift)
            {
                RequestScreenshotCaptureForCurrentCity(ticksToDelay: 1);
            }
            bool intervalElapsed = settings.IntervalMinutes > 0
                && (DateTime.UtcNow - _lastExportUtc).TotalMinutes >= settings.IntervalMinutes;

            if (!hotkey && !intervalElapsed && !saveLoadTransition) return;

            try
            {
                string trigger = saveLoadTransition ? "save-load"
                    : (hotkey ? "hotkey" : "interval");
                Export(triggeredBy: trigger);
                _lastExportUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Export failed.");
            }
        }

        // 0.4 — Added top-level `pollution` block (per-district + city-wide
        // air/ground/noise averages), `city.churn` (births/deaths/move-ins/
        // move-aways via CityStatisticsSystem), and `diff.building_churn`
        // (per-district demolition/construction counts that separate gross
        // churn from the existing net `district_zone_deltas`). See
        // docs/snapshot-schema.md.
        const string SchemaVersion = "0.4";

        void Export(string triggeredBy)
        {
            // OnUpdate's gate guarantees inGame + cityReady when we get here.
            if (!_cityComponentsLogged)
            {
                _cityComponentsLogged = true;
                LogCityComponentsOnce();
            }
            if (!_citizenFirstDiagged) DiagFirstCitizenOnce();
            if (!_mapMetadataLogged)
            {
                _mapMetadataLogged = true;
                LogMapMetadataOnce();
            }

            int citizensTotal = _citizenQuery.CalculateEntityCount();
            string cityName = string.IsNullOrEmpty(_cityConfig.cityName) ? null : _cityConfig.cityName;
            string ingameDate = _timeSystem.GetCurrentDateTime().ToString("yyyy-MM-dd");

            // Map identity. Pulled in a small helper-light style so a single
            // bad property doesn't drop the whole block.
            string mapName        = ReadMapString(_p_mapName);
            string mapTheme       = ReadMapString(_p_mapTheme);
            float? mapLatitude    = ReadMapFloat(_p_mapLatitude);
            float? mapLongitude   = ReadMapFloat(_p_mapLongitude);
            float? mapCloudiness  = ReadMapFloat(_p_mapCloudiness);
            float? mapPrecip      = ReadMapFloat(_p_mapPrecipitation);
            float? mapGroundWater = ReadMapFloat(_p_mapGroundWater);
            float? mapSurfaceWater = ReadMapFloat(_p_mapSurfaceWater);
            float? mapTempMin = null, mapTempMax = null;
            ReadMapBounds1(_p_mapTemperatureRange, out mapTempMin, out mapTempMax);

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
            // Bulk geometry (district polygons, building polygons) lives in
            // carto/processed/. Snapshot collects only the temporal-signal
            // data that has no Carto counterpart: city stats, demographics,
            // zone counts, outside_connections, water_sources, per-district
            // zone counts (for subdivision detection), and CustomName-tagged
            // building churn (for "Inger Brevik Elementary opened" signals).
            object demographics = CollectDemographics();
            Dictionary<string, int> zoneCounts = CollectZoneCounts();
            var named = CollectOtherNamedEntities();
            Dictionary<string, Dictionary<string, int>> districtZones = CollectDistrictZones();
            Dictionary<Entity, string> districtNameByEntity = CollectDistrictNamesByEntity();
            Dictionary<string, NamedBuilding> namedBuildings = CollectNamedBuildings(districtNameByEntity);
            Dictionary<string, BuildingFingerprint> allBuildings = CollectAllBuildings(districtNameByEntity);
            object pollution = CollectPollution(districtNameByEntity);
            object churn = ReadChurnStats();
            DateTime currentIngameDate = _timeSystem.GetCurrentDateTime();

            long unixTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string snapshotId = $"snapshot-{unixTs}";

            // Diff is null on the first snapshot of a session (no prior to
            // compare against); populated thereafter.
            object diff = _prevZoneCounts != null
                ? ComputeDiff(zoneCounts, currentIngameDate,
                    named.outsideConnectionsFingerprints,
                    named.waterSourcesFingerprints,
                    districtZones, namedBuildings, allBuildings)
                : null;

            // Advance the previous-snapshot pointers for the next export.
            _prevZoneCounts = zoneCounts;
            _prevOutsideConnections = named.outsideConnectionsFingerprints;
            _prevWaterSources = named.waterSourcesFingerprints;
            _prevDistrictZones = districtZones;
            _prevNamedBuildings = namedBuildings;
            _prevAllBuildings = allBuildings;
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

                // v0.3 — world identity. The city is the player's; the map is
                // the world the city sits inside. At founding time the agent
                // wants both. Climate fields (lat/lng/temperature/cloudiness/
                // precipitation) plus surface/ground water availability give
                // the storyteller a real geography to anchor canon to.
                map = new
                {
                    name = mapName,
                    theme = mapTheme,
                    latitude = mapLatitude,
                    longitude = mapLongitude,
                    temperature_min_c = mapTempMin,
                    temperature_max_c = mapTempMax,
                    cloudiness = mapCloudiness,
                    precipitation = mapPrecip,
                    ground_water_availability = mapGroundWater,
                    surface_water_availability = mapSurfaceWater,
                },

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
                    churn = churn,
                },

                // v0.4 — per-district + city-wide air/ground/noise averages,
                // sampled at building positions and binned by CurrentDistrict.
                // The direct cell-grid is in CS2's coordinate system; building
                // positions are the simplest place-where-people-live proxy.
                pollution = pollution,

                // v0.2: districts[], buildings[], roads[], other_named[] are
                // no longer emitted — they live in carto/processed/ as
                // storyteller-facing markdown. outside_connections and
                // water_sources stay (Carto doesn't surface those).
                outside_connections = named.outsideConnections,
                water_sources = named.waterSources,
                citizens_sample = new object[0],

                // Per-district zone counts. Same shape as city.zones but
                // keyed by district name (matching Carto chunks). Backs the
                // diff.district_zone_deltas signal for subdivision detection.
                district_zones = districtZones,

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

            // One city = one folder, even across multiple saves of that city. The
            // canon evolves with the city's lived history; different .cok save files
            // are checkpoints of the same world, not parallel timelines. Per-save
            // story branching is tracked as a future feature (see GitHub issues).
            string citySlug = TextUtils.Slugify(cityName) ?? "_unnamed";
            string dir = Path.Combine(EnvPath.kUserDataPath, "ModsData", nameof(CityStoryMod), citySlug);

            // City-rename detection. If the last exported folder for this
            // session is a different slug than the current one — and the
            // current cityName isn't an empty placeholder — the player just
            // renamed their save in-game. Migrate the previous folder
            // forward so all their canon, snapshots, and Carto state follow
            // the rename.
            //
            // Gate on `triggeredBy != "save-load"`: a save-load edge with a
            // different city name means the player loaded a DIFFERENT save
            // (started a new city, switched playthroughs, etc.), not that
            // they renamed the current one. Migrating in that case would
            // wrongly move the previous city's folder to the new save's slug.
            // In-game renames only happen mid-session, where no save-load
            // edge fires between exports.
            //
            // Conflict policy: only Directory.Move when the target doesn't
            // exist. If it does, surface an alert and leave both folders
            // alone — better to surprise the player with a warning than
            // silently destroy a folder's worth of canon.
            string previousDir = Mod.LastExportedCityDir;
            bool sameDir = !string.IsNullOrEmpty(previousDir)
                           && string.Equals(previousDir, dir, StringComparison.OrdinalIgnoreCase);
            bool isSaveLoadEdge = triggeredBy == "save-load";
            if (!isSaveLoadEdge
                && !sameDir
                && !string.IsNullOrEmpty(previousDir)
                && Directory.Exists(previousDir)
                && !string.IsNullOrWhiteSpace(cityName))
            {
                if (!Directory.Exists(dir))
                {
                    try
                    {
                        Directory.Move(previousDir, dir);
                        _log.Info($"City renamed: migrated {previousDir} → {dir}");
                        _promptUI?.ClearChatHistory($"city renamed to '{cityName}'");
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, $"City rename migration {previousDir} → {dir} failed.");
                        _promptUI?.ShowAlert(
                            $"Could not migrate '{Path.GetFileName(previousDir)}' to '{citySlug}' after rename: {ex.Message}. "
                            + "Previous content remains in the old folder; new exports will land in the new one.");
                    }
                }
                else
                {
                    string previousName = Path.GetFileName(previousDir);
                    _log.Warn(
                        $"City renamed to '{cityName}' but target folder '{dir}' already exists. "
                        + "Skipping migration to avoid clobbering existing content. "
                        + $"Previous folder: {previousDir}");
                    _promptUI?.ShowAlert(
                        $"City renamed to '{cityName}', but a folder for that name already exists. "
                        + $"Previous content stays at '{previousName}/'; new exports will land in '{citySlug}/'. "
                        + "Merge or delete one of the folders manually to consolidate.");
                }
            }

            // New-city detection: latched BEFORE we create the city dir. The
            // first Carto export will create <dir>/carto/ as a side-effect, so
            // checking the carto subdir's absence here gives "first export
            // for this city" semantics that survive across CS2 launches.
            // Latch is consumed below to gate the auto-Carto trigger so a
            // mid-session interval export of an already-scaffolded city
            // doesn't re-trigger Carto. Evaluated AFTER the rename migration
            // so a migrated city (which has a populated carto/ from before)
            // doesn't trigger the new-city flow.
            bool isNewCity = !Directory.Exists(Path.Combine(dir, "carto"));

            Directory.CreateDirectory(dir);
            EnsureCityScaffolded(dir);

            // On the save-load edge, if the player opted into auto-start, drop an
            // open sessions/ stub before writing the snapshot. The agent's
            // open-session "pid" rule picks it up — opening Claude after this
            // lands in a live session without needing /session-start. Skipped if
            // a prior session is still open (the agent will prompt /session-end).
            if (triggeredBy == "save-load" && Mod.Settings != null && Mod.Settings.AutoSessionStartOnSaveLoad)
            {
                EnsureOpenSessionStub(dir);
            }

            string snapshotsDir = Path.Combine(dir, "snapshots");
            Directory.CreateDirectory(snapshotsDir);
            string file = Path.Combine(snapshotsDir, $"{snapshotId}.json");
            File.WriteAllText(file, json);

            Mod.LastExportedCityDir = dir;

            _log.Info($"Exported snapshot ({triggeredBy}): citizens_total={citizensTotal}, outside_connections={named.outsideConnections.Count}, water_sources={named.waterSources.Count} -> {file}");

            // First-Carto-on-new-city auto-trigger. The agent's founding
            // prompt needs spatial context (map footprint, starting roads,
            // outside connections, eventually elevation) to ask the player
            // a meaningful question about the story to be told. Without
            // this, the storyteller's first run sees only the snapshot's
            // city stats — which are essentially zero at t=0.
            //
            // Gated on the save-load edge so we don't fire mid-session if a
            // hypothetical future flow surfaces a fresh city dir during play.
            // Gated on isNewCity so re-loading an established city doesn't
            // re-run Carto (the storyteller-window button covers that case).
            if (triggeredBy == "save-load" && isNewCity && CartoBridge.IsAvailable)
            {
                _log.Info("New city detected (no carto/ dir yet); auto-triggering first Carto export for storytelling context.");
                RequestCartoExport();

                // Note: screenshot capture is NOT auto-triggered on the
                // save-load edge — the framebuffer isn't reliably rendered
                // yet during CS2's loading-screen-to-game transition
                // (EncodeToPNG throws "texture is invalid"). The screenshot
                // is instead captured when the player submits /new-city in
                // the storyteller window (see PromptUISystem.OnSubmitPrompt),
                // by which point the game is fully rendered and the player
                // has deliberately framed the moment.
            }

            // Otherwise: Carto exports are NOT on the snapshot cadence —
            // the pipeline is synchronous, main-thread-only (ECS), and grows
            // linearly with city size. The player triggers refreshes manually
            // via the Refresh map button in the storyteller window. See
            // RequestCartoExport.
        }

        // Queue a screenshot capture for a near-future tick. Used both by
        // the new-city auto-trigger and the manual "Capture map" UI button /
        // hotkey. Overwrites any pending capture; only one queued at a time.
        // ticksToDelay: how many OnUpdate ticks to wait before grabbing the
        // framebuffer — gives any UI flash from the trigger source time to
        // settle. 0 = capture next tick; 4 = capture five ticks from now.
        public void RequestScreenshotCapture(string cityDir, string citySlug, int ticksToDelay)
        {
            string path = ScreenshotCapture.GetOverviewPath(cityDir, citySlug);
            if (path == null)
            {
                _log.Warn("RequestScreenshotCapture: missing cityDir or citySlug; skipping.");
                return;
            }
            _pendingScreenshotPath = path;
            _pendingScreenshotTicksRemaining = Math.Max(0, ticksToDelay);
            _log.Info($"RequestScreenshotCapture: queued for {_pendingScreenshotTicksRemaining + 1} tick(s) → {path}");
        }

        // Convenience overload for triggers that don't have the city slug
        // handy — derives both from the last-exported directory. Returns
        // false if no city has been exported yet this session.
        public bool RequestScreenshotCaptureForCurrentCity(int ticksToDelay = 4)
        {
            string cityDir = Mod.LastExportedCityDir;
            if (string.IsNullOrEmpty(cityDir))
            {
                _log.Info("RequestScreenshotCaptureForCurrentCity: no city dir known yet; skipping.");
                return false;
            }
            string slug = Path.GetFileName(cityDir);
            RequestScreenshotCapture(cityDir, slug, ticksToDelay);
            return true;
        }

        // User-triggered Carto export. Called from PromptUISystem when the
        // player clicks Refresh map in the storyteller window. Sets up the
        // same deferred-tick pattern Export() used to drive automatically —
        // flip the cartoExporting binding now so Coherent UI paints the
        // indicator on this frame, then run the synchronous Carto pipeline
        // on the next OnUpdate tick.
        public void RequestCartoExport()
        {
            if (!CartoBridge.IsAvailable)
            {
                _log.Info("RequestCartoExport: Carto unavailable; ignoring.");
                return;
            }
            string cityDir = Mod.LastExportedCityDir;
            if (string.IsNullOrEmpty(cityDir))
            {
                _log.Info("RequestCartoExport: no city dir known yet; ignoring.");
                return;
            }
            if (_pendingCartoDir != null)
            {
                _log.Info("RequestCartoExport: export already pending; ignoring (deferred slot full).");
                return;
            }
            _pendingCartoDir = Path.Combine(cityDir, "carto");
            _promptUI?.SetCartoExporting(true);
            _log.Info($"RequestCartoExport: queued; will run on next tick → {_pendingCartoDir}");
        }

        // Syncs the city dir with the embedded template/ tree. Unlike the old
        // one-shot scaffolder, this runs on every export and migrates
        // unmodified template files forward when the template evolves. Files
        // the player has edited are left alone — see TemplateScaffolder for
        // the full per-file decision tree.
        const string ResourcePrefix = "template/";

        void EnsureCityScaffolded(string cityDir)
        {
            var asm = typeof(Mod).Assembly;
            var files = new List<TemplateScaffolder.TemplateFile>();
            foreach (string resourceName in asm.GetManifestResourceNames())
            {
                if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                string relative = resourceName.Substring(ResourcePrefix.Length);
                byte[] content;
                using (Stream src = asm.GetManifestResourceStream(resourceName))
                using (var ms = new MemoryStream())
                {
                    src.CopyTo(ms);
                    content = ms.ToArray();
                }
                files.Add(new TemplateScaffolder.TemplateFile
                {
                    RelativePath = relative,
                    Content = content,
                });
            }

            var result = TemplateScaffolder.Sync(cityDir, files);
            int touched = result.Added + result.Updated;
            if (touched > 0 || result.Divergent.Count > 0)
            {
                _log.Info(
                    $"Template sync: added={result.Added} updated={result.Updated} "
                    + $"unchanged={result.Unchanged} divergent={result.Divergent.Count} "
                    + (result.Divergent.Count > 0 ? $"[divergent: {string.Join(", ", result.Divergent)}]" : "")
                );
            }
        }

        // Writes an open sessions/SXX-YYYY-MM-DD-open.md stub when the auto-start
        // setting is on and no prior session is still open. Lack of an
        // `ended_real_date:` line in a session file's frontmatter means it's open;
        // the /session-end command adds that field on close.
        void EnsureOpenSessionStub(string cityDir)
        {
            string sessionsDir = Path.Combine(cityDir, "sessions");
            Directory.CreateDirectory(sessionsDir);

            int maxN = 0;
            bool anyOpen = false;
            foreach (string path in Directory.GetFiles(sessionsDir, "S*-*.md"))
            {
                string name = Path.GetFileName(path);
                int dash = name.IndexOf('-');
                if (dash > 1 && name.StartsWith("S", StringComparison.Ordinal)
                    && int.TryParse(name.Substring(1, dash - 1), out int n)
                    && n > maxN)
                {
                    maxN = n;
                }

                try
                {
                    if (!TextUtils.FrontmatterHasEndedRealDate(File.ReadAllText(path))) anyOpen = true;
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not read session file {name} for open-session check: {ex.Message}");
                }
            }

            if (anyOpen)
            {
                _log.Info($"Auto session-start skipped: open session already present in {sessionsDir}");
                return;
            }

            int nextN = maxN + 1;
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            string filename = $"S{nextN:D2}-{today}-open.md";
            string filepath = Path.Combine(sessionsDir, filename);
            string body =
                "---\n" +
                $"session: {nextN}\n" +
                $"real_date: {today}\n" +
                "in_world_window: TBD\n" +
                "---\n" +
                "\n" +
                "(Session in progress — populated by /session-end.)\n";
            File.WriteAllText(filepath, body);
            _log.Info($"Auto session-start wrote {filename}");
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

        // Pull a string-typed MapMetadataSystem property. Returns null on
        // missing property, missing system, exception, or empty/whitespace.
        string ReadMapString(PropertyInfo p)
        {
            if (_mapMetadataSystem == null || p == null) return null;
            try
            {
                string v = (string)p.GetValue(_mapMetadataSystem);
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
            catch (Exception ex)
            {
                _log.Warn($"Reading map.{p.Name} failed: {ex.Message}");
                return null;
            }
        }

        // Pull a float-typed MapMetadataSystem property.
        float? ReadMapFloat(PropertyInfo p)
        {
            if (_mapMetadataSystem == null || p == null) return null;
            try { return Convert.ToSingle(p.GetValue(_mapMetadataSystem)); }
            catch (Exception ex)
            {
                _log.Warn($"Reading map.{p.Name} failed: {ex.Message}");
                return null;
            }
        }

        // Pull min/max out of a Colossal.Mathematics.Bounds1 value-typed
        // property. Read reflectively to avoid the Colossal.Mathematics
        // compile-time dependency — same pattern as the rest of this file.
        void ReadMapBounds1(PropertyInfo p, out float? min, out float? max)
        {
            min = null; max = null;
            if (_mapMetadataSystem == null || p == null) return;
            try
            {
                object bounds = p.GetValue(_mapMetadataSystem);
                if (bounds == null) return;
                Type t = bounds.GetType();
                var fMin = t.GetField("min") ?? t.GetField("Min") ?? t.GetField("m_Min");
                var fMax = t.GetField("max") ?? t.GetField("Max") ?? t.GetField("m_Max");
                if (fMin != null) min = Convert.ToSingle(fMin.GetValue(bounds));
                if (fMax != null) max = Convert.ToSingle(fMax.GetValue(bounds));
            }
            catch (Exception ex)
            {
                _log.Warn($"Reading map.{p.Name} bounds failed: {ex.Message}");
            }
        }

        // One-time dump of MapMetadataSystem's full property surface. We only
        // wire `mapName` into the snapshot now; this log surfaces everything
        // else the system exposes (theme, size, climate, image, etc.) so
        // future cycles can add fields by name without re-spelunking.
        void LogMapMetadataOnce()
        {
            if (_mapMetadataSystem == null)
            {
                _log.Info("[diag] MapMetadataSystem: not resolved; map.* will stay null.");
                return;
            }
            try
            {
                Type t = _mapMetadataSystem.GetType();
                var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0)
                    .OrderBy(p => p.Name)
                    .Select(p =>
                    {
                        object value;
                        try { value = p.GetValue(_mapMetadataSystem); }
                        catch (Exception ex) { value = $"<error: {ex.GetType().Name}>"; }
                        // Truncate big strings / collection ToStrings to keep the log readable.
                        string display = value?.ToString() ?? "null";
                        if (display.Length > 120) display = display.Substring(0, 117) + "...";
                        return $"{p.Name}:{p.PropertyType.Name}={display}";
                    })
                    .ToArray();
                _log.Info($"[diag] {t.FullName} properties: {string.Join(", ", props)}");
            }
            catch (Exception ex)
            {
                _log.Warn($"[diag] LogMapMetadataOnce failed: {ex.Message}");
            }
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

        // Diff block: bulk signals that surface temporal change between
        // snapshots. Spatial geometry stays in carto/processed/; this is
        // where "what just happened in the city" lives.
        object ComputeDiff(
            Dictionary<string, int> currentZoneCounts,
            DateTime currentIngameDate,
            Dictionary<string, NameRef> currentOutsideConnections,
            Dictionary<string, NameRef> currentWaterSources,
            Dictionary<string, Dictionary<string, int>> currentDistrictZones,
            Dictionary<string, NamedBuilding> currentNamedBuildings,
            Dictionary<string, BuildingFingerprint> currentAllBuildings)
        {
            int? ingameDaysElapsed = _prevIngameDate.HasValue
                ? (int)(currentIngameDate - _prevIngameDate.Value).TotalDays
                : (int?)null;

            // City-wide zone changes. Only emit zones that actually changed.
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

            // Per-district zone changes. Surfaces subdivision-opening signals
            // ("Pine Quarter +45 residential"). Only emits districts where at
            // least one zone type changed; within a district, only the changed
            // zone types appear.
            var districtZoneDeltas = new Dictionary<string, object>();
            if (_prevDistrictZones != null)
            {
                foreach (var kv in currentDistrictZones)
                {
                    var perZone = new Dictionary<string, object>();
                    _prevDistrictZones.TryGetValue(kv.Key, out var prevZones);
                    foreach (var zk in kv.Value)
                    {
                        int prev = 0;
                        prevZones?.TryGetValue(zk.Key, out prev);
                        if (prev != zk.Value)
                            perZone[zk.Key] = new { from = prev, to = zk.Value, delta = zk.Value - prev };
                    }
                    // Zone types that existed before and dropped to 0 / missing now.
                    if (prevZones != null)
                    {
                        foreach (var pz in prevZones)
                        {
                            if (!kv.Value.ContainsKey(pz.Key) && pz.Value != 0)
                                perZone[pz.Key] = new { from = pz.Value, to = 0, delta = -pz.Value };
                        }
                    }
                    if (perZone.Count > 0) districtZoneDeltas[kv.Key] = perZone;
                }
                // Districts that existed before but are gone now (renamed or removed).
                foreach (var pv in _prevDistrictZones)
                {
                    if (currentDistrictZones.ContainsKey(pv.Key)) continue;
                    var dropped = new Dictionary<string, object>();
                    foreach (var pz in pv.Value)
                    {
                        if (pz.Value != 0)
                            dropped[pz.Key] = new { from = pz.Value, to = 0, delta = -pz.Value };
                    }
                    if (dropped.Count > 0) districtZoneDeltas[pv.Key] = dropped;
                }
            }

            // Named-building churn. Catches CS2's auto-named civic buildings
            // ("Halverson Crossing High School" appearing) AND player-renamed
            // buildings (intentional canon links). Type comes from
            // BuildingTypeFromMarkers so the agent can react differently to a
            // new school vs a new low-density house.
            var nbAdded = new List<object>();
            var nbRemoved = new List<object>();
            var nbRenamed = new List<object>();
            if (_prevNamedBuildings != null)
            {
                foreach (var kv in currentNamedBuildings)
                {
                    if (!_prevNamedBuildings.TryGetValue(kv.Key, out var prev))
                    {
                        nbAdded.Add(new { id = kv.Value.Id, name = kv.Value.Name, type = kv.Value.Type, district = kv.Value.DistrictName });
                        continue;
                    }
                    if (prev.Name != kv.Value.Name)
                    {
                        nbRenamed.Add(new { id = kv.Value.Id, from = prev.Name, to = kv.Value.Name, type = kv.Value.Type, district = kv.Value.DistrictName });
                    }
                }
                foreach (var pv in _prevNamedBuildings)
                {
                    if (!currentNamedBuildings.ContainsKey(pv.Key))
                        nbRemoved.Add(new { id = pv.Value.Id, name = pv.Value.Name, type = pv.Value.Type, district = pv.Value.DistrictName });
                }
            }

            var ocDiff = DiffNameRefs(_prevOutsideConnections, currentOutsideConnections);
            var wsDiff = DiffNameRefs(_prevWaterSources, currentWaterSources);

            // v0.4 — per-district demolition/construction churn. Sits next to
            // (not replacing) district_zone_deltas: the latter is net change,
            // this is gross movement on both sides. Stories about
            // displacement, gentrification, or industrial cleanup need to
            // know both buildings appeared AND others disappeared, not just
            // the net.
            object buildingChurn = _prevAllBuildings != null
                ? ComputeBuildingChurn(_prevAllBuildings, currentAllBuildings)
                : null;

            return new
            {
                since_snapshot_id = _prevSnapshotId,
                since_captured_at_ingame = _prevIngameDate?.ToString("yyyy-MM-dd"),
                ingame_days_elapsed = ingameDaysElapsed,
                zones_delta = zonesDelta,
                district_zone_deltas = districtZoneDeltas,
                building_churn = buildingChurn,
                named_buildings = new { added = nbAdded, removed = nbRemoved, renamed = nbRenamed },
                outside_connections = new { added = ocDiff.added, removed = ocDiff.removed, changed = ocDiff.changed },
                water_sources = new { added = wsDiff.added, removed = wsDiff.removed, changed = wsDiff.changed },
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

        // Samples pollution at every building position and bins by district.
        // Returns null if pollution systems weren't resolved or their grids
        // haven't been allocated yet (fresh save before sim has run any
        // pollution updates). City-wide averages are computed across every
        // sampled building (including ones with no district). Per-district
        // averages cover only buildings whose CurrentDistrict resolved.
        //
        // Why sample at buildings: pollution lives on an Nx N cell grid in
        // CS2's coordinate system. Mapping cells to districts directly would
        // need spatial polygon containment against district shapes — the
        // building-position proxy is much cheaper and aligns sampling weight
        // with where people actually live and work.
        object CollectPollution(Dictionary<Entity, string> districtNameByEntity)
        {
            if (_airPollutionSystem == null || _groundPollutionSystem == null || _noisePollutionSystem == null
                || _f_airPollutionMap == null || _f_groundPollutionMap == null || _f_noisePollutionMap == null)
                return null;

            NativeArray<AirPollution>    airMap;
            NativeArray<GroundPollution> groundMap;
            NativeArray<NoisePollution>  noiseMap;
            try
            {
                airMap    = (NativeArray<AirPollution>)_f_airPollutionMap.GetValue(_airPollutionSystem);
                groundMap = (NativeArray<GroundPollution>)_f_groundPollutionMap.GetValue(_groundPollutionSystem);
                noiseMap  = (NativeArray<NoisePollution>)_f_noisePollutionMap.GetValue(_noisePollutionSystem);
            }
            catch (Exception ex)
            {
                _log.Warn($"Pollution map fetch failed: {ex.Message}");
                return null;
            }
            if (!airMap.IsCreated || !groundMap.IsCreated || !noiseMap.IsCreated) return null;
            if (airMap.Length == 0 || groundMap.Length == 0 || noiseMap.Length == 0) return null;

            long airCitySum = 0, groundCitySum = 0, noiseCitySum = 0;
            int  citySamples = 0;
            var districtSums = new Dictionary<string, (long air, long ground, long noise, int samples)>();

            using var entities = _allBuildingsQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                var b = entities[i];
                if (!EntityManager.HasComponent<Game.Objects.Transform>(b)) continue;
                var pos = EntityManager.GetComponentData<Game.Objects.Transform>(b).m_Position;

                int air    = AirPollutionSystem.GetPollution(pos, airMap).m_Pollution;
                int ground = GroundPollutionSystem.GetPollution(pos, groundMap).m_Pollution;
                int noise  = NoisePollutionSystem.GetPollution(pos, noiseMap).m_Pollution;

                airCitySum += air;
                groundCitySum += ground;
                noiseCitySum += noise;
                citySamples++;

                string districtName = null;
                if (EntityManager.HasComponent<CurrentDistrict>(b))
                {
                    var d = EntityManager.GetComponentData<CurrentDistrict>(b).m_District;
                    if (d != Entity.Null) districtNameByEntity.TryGetValue(d, out districtName);
                }
                if (districtName == null) continue;

                districtSums.TryGetValue(districtName, out var s);
                s.air += air; s.ground += ground; s.noise += noise; s.samples++;
                districtSums[districtName] = s;
            }

            if (citySamples == 0) return null;

            var byDistrict = new Dictionary<string, object>();
            foreach (var kv in districtSums)
            {
                var s = kv.Value;
                byDistrict[kv.Key] = new
                {
                    air = Math.Round((double)s.air / s.samples, 2),
                    ground = Math.Round((double)s.ground / s.samples, 2),
                    noise = Math.Round((double)s.noise / s.samples, 2),
                    samples = s.samples,
                };
            }

            return new
            {
                city = new
                {
                    air = Math.Round((double)airCitySum / citySamples, 2),
                    ground = Math.Round((double)groundCitySum / citySamples, 2),
                    noise = Math.Round((double)noiseCitySum / citySamples, 2),
                },
                samples = citySamples,
                by_district = byDistrict,
            };
        }

        // City-wide churn read via CityStatisticsSystem. The four statistic
        // types are all CollectionType.Daily, so the returned int is the
        // most-recent-day value. The agent treats these as "current rate"
        // signals — diffing across snapshots gives the period total.
        // Returns null if the statistics system didn't resolve.
        object ReadChurnStats()
        {
            if (_cityStatistics == null) return null;
            try
            {
                return new
                {
                    births_daily      = _cityStatistics.GetStatisticValue(StatisticType.BirthRate, 0),
                    deaths_daily      = _cityStatistics.GetStatisticValue(StatisticType.DeathRate, 0),
                    moved_in_daily    = _cityStatistics.GetStatisticValue(StatisticType.CitizensMovedIn, 0),
                    moved_away_daily  = _cityStatistics.GetStatisticValue(StatisticType.CitizensMovedAway, 0),
                };
            }
            catch (Exception ex)
            {
                _log.Warn($"ReadChurnStats failed: {ex.Message}");
                return null;
            }
        }

        // Walks every building and returns a slim fingerprint (zone-type +
        // district) per entity id. Used solely to compute the per-district
        // demolition/construction churn diff — never serialized in full.
        // Lighter than CollectNamedBuildings (no name resolution) so this
        // can cover the whole city without doubling export cost.
        Dictionary<string, BuildingFingerprint> CollectAllBuildings(Dictionary<Entity, string> districtNameByEntity)
        {
            var result = new Dictionary<string, BuildingFingerprint>();
            using var entities = _allBuildingsQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                var b = entities[i];
                string districtName = null;
                if (EntityManager.HasComponent<CurrentDistrict>(b))
                {
                    var d = EntityManager.GetComponentData<CurrentDistrict>(b).m_District;
                    if (d != Entity.Null) districtNameByEntity.TryGetValue(d, out districtName);
                }
                result[EntityId(b)] = new BuildingFingerprint
                {
                    Type = BuildingTypeFromMarkers(b) ?? "other",
                    DistrictName = districtName,
                };
            }
            return result;
        }

        // Gross churn: ids present last time but gone now = demolition;
        // present now but not last time = construction. Aggregated by
        // (district, zone-type) so the agent gets a story-sized signal,
        // not a per-id event stream. Buildings without a resolved district
        // get binned under "_unassigned" so the totals stay honest.
        object ComputeBuildingChurn(
            Dictionary<string, BuildingFingerprint> prev,
            Dictionary<string, BuildingFingerprint> current)
        {
            var demolitions  = new Dictionary<string, Dictionary<string, int>>();
            var constructions = new Dictionary<string, Dictionary<string, int>>();
            int totalDemo = 0, totalCons = 0;

            foreach (var kv in prev)
            {
                if (current.ContainsKey(kv.Key)) continue;
                string district = kv.Value.DistrictName ?? "_unassigned";
                if (!demolitions.TryGetValue(district, out var byType))
                {
                    byType = new Dictionary<string, int>();
                    demolitions[district] = byType;
                }
                byType.TryGetValue(kv.Value.Type, out int n);
                byType[kv.Value.Type] = n + 1;
                totalDemo++;
            }
            foreach (var kv in current)
            {
                if (prev.ContainsKey(kv.Key)) continue;
                string district = kv.Value.DistrictName ?? "_unassigned";
                if (!constructions.TryGetValue(district, out var byType))
                {
                    byType = new Dictionary<string, int>();
                    constructions[district] = byType;
                }
                byType.TryGetValue(kv.Value.Type, out int n);
                byType[kv.Value.Type] = n + 1;
                totalCons++;
            }

            return new
            {
                total_demolished = totalDemo,
                total_constructed = totalCons,
                demolitions_by_district = demolitions,
                constructions_by_district = constructions,
            };
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

        // Returns { districtName → { zoneType → count } } for all districts.
        // Backs the per-district zone delta in the diff so the agent can spot
        // subdivision openings ("Pine Quarter grew by 45 residential lots").
        // Keyed by district name (matching Carto chunks) rather than ECS id so
        // the agent can cross-reference snapshot deltas with carto/processed/
        // chunks without needing to resolve entity-id mappings.
        Dictionary<string, Dictionary<string, int>> CollectDistrictZones()
        {
            var result = new Dictionary<string, Dictionary<string, int>>();
            using var districts = _districtQuery.ToEntityArray(Allocator.Temp);
            if (districts.Length == 0) return result;

            var districtNameByEntity = new Dictionary<Entity, string>(districts.Length);
            for (int i = 0; i < districts.Length; i++)
            {
                string name = _nameSystem.GetRenderedLabelName(districts[i]);
                if (string.IsNullOrWhiteSpace(name)) continue;
                districtNameByEntity[districts[i]] = name;
                result[name] = new Dictionary<string, int>();
            }

            using var buildings = _allBuildingsQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < buildings.Length; i++)
            {
                var b = buildings[i];
                if (!EntityManager.HasComponent<CurrentDistrict>(b)) continue;
                var d = EntityManager.GetComponentData<CurrentDistrict>(b).m_District;
                if (d == Entity.Null) continue;
                if (!districtNameByEntity.TryGetValue(d, out string districtName)) continue;

                string type = BuildingTypeFromMarkers(b) ?? "other";
                var zones = result[districtName];
                zones.TryGetValue(type, out int prev);
                zones[type] = prev + 1;
            }
            return result;
        }

        // Walks CustomName-tagged buildings and produces a slim fingerprint
        // per entity. Catches both player-renamed buildings (intentional canon
        // links) and CS2's auto-named civic/service buildings ("Halverson
        // Crossing Fire & Rescue", "Selkirk Power Transformer"). Backs the
        // named-buildings churn diff — "Inger Brevik Elementary opened" type
        // signals — without re-introducing the full v0.1 buildings[] payload.
        Dictionary<string, NamedBuilding> CollectNamedBuildings(Dictionary<Entity, string> districtNameByEntity)
        {
            var result = new Dictionary<string, NamedBuilding>();
            using var entities = _namedBuildingQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                string id = EntityId(e);
                string name = _nameSystem.GetRenderedLabelName(e);
                string type = BuildingTypeFromMarkers(e) ?? "other";
                string districtName = null;
                if (EntityManager.HasComponent<CurrentDistrict>(e))
                {
                    var d = EntityManager.GetComponentData<CurrentDistrict>(e).m_District;
                    if (d != Entity.Null) districtNameByEntity.TryGetValue(d, out districtName);
                }
                result[id] = new NamedBuilding
                {
                    Id = id,
                    Name = name,
                    Type = type,
                    DistrictName = districtName,
                };
            }
            return result;
        }

        // Helper for CollectNamedBuildings — builds the entity→name map once
        // per export instead of re-resolving names while walking buildings.
        Dictionary<Entity, string> CollectDistrictNamesByEntity()
        {
            var result = new Dictionary<Entity, string>();
            using var districts = _districtQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < districts.Length; i++)
            {
                string name = _nameSystem.GetRenderedLabelName(districts[i]);
                if (!string.IsNullOrWhiteSpace(name)) result[districts[i]] = name;
            }
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
    }
}
