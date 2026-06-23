using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Colossal.Logging;
using Game.Modding;
using Game.SceneFlow;
using Unity.Entities;

namespace CityStoryMod.Storyteller
{
    // Reflective, soft-coupled reader for bruceyboy24804's InfoLoom mod
    // (https://github.com/bruceyboy24804/InfoLoom — assembly "InfoLoomTwo",
    // ModId 91433). No compile-time reference to InfoLoomTwo.dll, so our DLL
    // survives InfoLoom rebuilds across CS2 patches as long as the data surface
    // holds. Same pattern as CartoBridge / ElectionsBridge.
    //
    // InfoLoom already derives the demographic, workforce, and trade aggregates
    // we'd otherwise re-compute from raw ECS. We tap its public ECS systems
    // rather than its CSV exporter: the exporter is button-triggered from
    // InfoLoom's own Settings, gated behind per-type toggles, and prunes its
    // files — unreliable as a tail source. The systems expose their result
    // buffers (`m_Results`, `m_LifecycleDetails`, `m_Totals`) and reading
    // accessors (`GetSortedResourceTradeCosts()`) as *public* members.
    //
    // Freshness gotcha: these systems only recompute when their UI panel is
    // visible (their OnUpdate early-returns on `!IsPanelVisible`). Read cold,
    // the buffers are stale or all-zero. So we call the same public recalc
    // methods InfoLoom's own DataExporter calls — `RecalculateNow()` /
    // `UpdateDemographics()` / `UpdateAllTradeCosts()` — each of which schedules
    // its job and `.Complete()`s synchronously, so reading right after is safe.
    // We force the citywide view (SelectedDistrict = null) and restore the
    // player's prior district selection afterwards.
    //
    // First cut (#31): fills the previously-empty `trade` block and adds a new
    // top-level `labor` block (workforce by education level + age-band
    // demographics). Per-district demographics and the workplaces-by-sector
    // rollup are deferred — InfoLoom carries them on heavier NativeList<Entity>
    // structures that are riskier to read reflectively.
    //
    // Every probe is name-based and null-tolerant: a renamed or removed member
    // yields a missing sub-value, never a crash. If InfoLoomTwo isn't installed
    // or its expected systems can't be resolved, the bridge disables and both
    // `trade` (empty arrays) and `labor` (null) keep their not-present contract.
    public static class InfoLoomBridge
    {
        const string AssemblyName = "InfoLoomTwo";
        const string TradeSystemTypeName    = "InfoLoomTwo.Systems.TradeCostData.TradeCostsSystem";
        const string WorkforceSystemTypeName = "InfoLoomTwo.Systems.WorkforceData.WorkforceSystem";
        const string DemographicsTypeName   = "InfoLoomTwo.Systems.DemographicsData.Demographics";

        // Index → label for WorkforceSystem.m_Results (mirror of InfoLoom's
        // own level names in DataExporter.ExportWorkforce). Last slot is the
        // city-wide rollup, split out as `totals`.
        static readonly string[] WorkforceLevelNames =
            { "Uneducated", "PoorlyEducated", "Educated", "WellEducated", "HighlyEducated", "Totals" };

        // Index → label for Demographics.m_LifecycleDetails (Child/Teen/Adult/
        // Elderly — the vanilla four bands, same order InfoLoom uses).
        static readonly string[] LifecycleNames = { "Child", "Teen", "Adult", "Elderly" };

        static bool _resolved;
        static bool _available;
        static string _version;
        static Assembly _asm;
        static MethodInfo _getExistingDef;
        static readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();

        public static bool IsAvailable { get { EnsureResolved(); return _available; } }
        public static string Version { get { EnsureResolved(); return _version; } }

        public sealed class Reading
        {
            public object Trade;   // serialized as snapshot.trade (imports/exports)
            public object Labor;   // serialized as snapshot.labor (workforce + ages)
        }

        // Reads InfoLoom's live aggregates. Returns null when InfoLoom isn't
        // installed, or when neither sub-read produced anything (e.g. pre-first-
        // tick). `world` is the ExportSystem's World — InfoLoom registers its
        // systems into the same default world.
        public static Reading TryRead(World world, ILog log)
        {
            EnsureResolved();
            if (!_available || world == null) return null;

            object trade = null, labor = null;
            try { trade = BuildTrade(world, log); }
            catch (Exception ex) { log?.Warn($"InfoLoomBridge.BuildTrade failed: {ex.Message}"); }
            try { labor = BuildLabor(world, log); }
            catch (Exception ex) { log?.Warn($"InfoLoomBridge.BuildLabor failed: {ex.Message}"); }

            if (trade == null && labor == null) return null;
            return new Reading { Trade = trade, Labor = labor };
        }

        // ---- trade --------------------------------------------------------

        static object BuildTrade(World world, ILog log)
        {
            object sys = GetSystem(world, TradeSystemTypeName);
            if (sys == null) return null;

            // Recompute first — the panel-visibility gate means the cached dict
            // is stale otherwise. UpdateAllTradeCosts completes its job handles
            // inline (JobHandle.CompleteAll), so the read below is consistent.
            try { Invoke(sys, "UpdateAllTradeCosts"); }
            catch (Exception ex) { log?.Warn($"InfoLoomBridge: UpdateAllTradeCosts failed: {ex.Message}"); }

            MethodInfo m = sys.GetType().GetMethod("GetSortedResourceTradeCosts",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m == null) return null;

            object res = m.Invoke(sys, null);
            if (res is not IEnumerable rows) return null;

            // amount columns are floats blended from theoretical + storage
            // signals; round to whole units per day. A resource counts as an
            // import or export only when its amount clears 1/day, so trace
            // noise doesn't bloat the lists.
            var imp = new List<(string r, int amt, double cost)>();
            var exp = new List<(string r, int amt, double cost)>();
            foreach (object tc in rows)
            {
                string resource = MStr(tc, "Resource");
                if (string.IsNullOrEmpty(resource)) continue;
                float import = MFloat(tc, "ImportAmount");
                float export = MFloat(tc, "ExportAmount");
                if (import >= 1f) imp.Add((resource, (int)Math.Round(import), Round1(MFloat(tc, "BuyCost"))));
                if (export >= 1f) exp.Add((resource, (int)Math.Round(export), Round1(MFloat(tc, "SellCost"))));
            }
            imp.Sort((a, b) => b.amt.CompareTo(a.amt));
            exp.Sort((a, b) => b.amt.CompareTo(a.amt));

            var imports = imp.Select(x => (object)new { resource = x.r, amount_per_day = x.amt, buy_cost = x.cost }).ToList();
            var exports = exp.Select(x => (object)new { resource = x.r, amount_per_day = x.amt, sell_cost = x.cost }).ToList();

            return new { source = AssemblyName, mod_version = _version, imports, exports };
        }

        // ---- labor (workforce + age distribution) -------------------------

        static object BuildLabor(World world, ILog log)
        {
            object workforce = BuildWorkforce(world, log);
            object ages = BuildAgeDistribution(world, log);
            if (workforce == null && ages == null) return null;
            return new { source = AssemblyName, mod_version = _version, workforce, age_distribution = ages };
        }

        static object BuildWorkforce(World world, ILog log)
        {
            object sys = GetSystem(world, WorkforceSystemTypeName);
            if (sys == null) return null;

            object saved = SetSelectedDistrictNull(sys);
            try { Invoke(sys, "RecalculateNow"); }
            catch (Exception ex)
            {
                log?.Warn($"InfoLoomBridge: workforce RecalculateNow failed: {ex.Message}");
                RestoreSelectedDistrict(sys, saved);
                return null;
            }
            List<object> rows = ReadNativeArray(sys, "m_Results");
            RestoreSelectedDistrict(sys, saved);
            if (rows == null) return null;

            var byLevel = new List<object>();
            object totals = null;
            for (int i = 0; i < rows.Count; i++)
            {
                object r = rows[i];
                string level = i < WorkforceLevelNames.Length ? WorkforceLevelNames[i] : i.ToString();
                object entry = new
                {
                    level,
                    total = MInt(r, "Total"),
                    employed = MInt(r, "Worker"),
                    unemployed = MInt(r, "Unemployed"),
                    unemployment_rate = Round1(MFloat(r, "UnemploymentRate")),
                    employable = MInt(r, "Employable"),
                    commuting_out = MInt(r, "Outside"),
                    underemployed = MInt(r, "Under"),
                    homeless = MInt(r, "Homeless"),
                };
                if (string.Equals(level, "Totals", StringComparison.Ordinal)) totals = entry;
                else byLevel.Add(entry);
            }
            return new { by_education_level = byLevel, totals };
        }

        static object BuildAgeDistribution(World world, ILog log)
        {
            object sys = GetSystem(world, DemographicsTypeName);
            if (sys == null) return null;

            object saved = SetSelectedDistrictNull(sys);
            try { Invoke(sys, "UpdateDemographics"); }
            catch (Exception ex)
            {
                log?.Warn($"InfoLoomBridge: UpdateDemographics failed: {ex.Message}");
                RestoreSelectedDistrict(sys, saved);
                return null;
            }
            List<object> bands = ReadNativeArray(sys, "m_LifecycleDetails");
            List<object> totalsArr = ReadNativeArray(sys, "m_Totals");
            RestoreSelectedDistrict(sys, saved);

            var lifecycle = new List<object>();
            if (bands != null)
            {
                for (int i = 0; i < bands.Count; i++)
                {
                    object b = bands[i];
                    string band = i < LifecycleNames.Length ? LifecycleNames[i] : i.ToString();
                    lifecycle.Add(new
                    {
                        band,
                        total = MInt(b, "Total"),
                        working = MInt(b, "Work"),
                        students = MInt(b, "School1") + MInt(b, "School2") + MInt(b, "School3") + MInt(b, "School4"),
                        unemployed = MInt(b, "Unemployed"),
                        retired = MInt(b, "Retired"),
                        education = new
                        {
                            uneducated = MInt(b, "Uneducated"),
                            poorly_educated = MInt(b, "PoorlyEducated"),
                            educated = MInt(b, "Educated"),
                            well_educated = MInt(b, "WellEducated"),
                            highly_educated = MInt(b, "HighlyEducated"),
                        },
                    });
                }
            }

            object totals = null;
            // Index order mirrors InfoLoom's Totals enum / DataExporter labels:
            // AllCitizens, Locals, Tourists, Commuters, Students, Workers,
            // OldestCitizenAge (in days), MovingAways, DeadCitizens, Homeless.
            if (totalsArr != null && totalsArr.Count >= 10)
            {
                int T(int idx) => Convert.ToInt32(totalsArr[idx]);
                totals = new
                {
                    all_citizens = T(0),
                    locals = T(1),
                    tourists = T(2),
                    commuters = T(3),
                    students = T(4),
                    workers = T(5),
                    oldest_citizen_age_days = T(6),
                    moving_away = T(7),
                    dead = T(8),
                    homeless = T(9),
                };
            }

            if (lifecycle.Count == 0 && totals == null) return null;
            return new { lifecycle, totals };
        }

        // ---- reflective plumbing ------------------------------------------

        static object GetSystem(World world, string typeName)
        {
            Type t = ResolveType(typeName);
            if (t == null) return null;
            if (_getExistingDef == null)
                _getExistingDef = typeof(World).GetMethod("GetExistingSystemManaged", Type.EmptyTypes);
            if (_getExistingDef == null) return null;
            return _getExistingDef.MakeGenericMethod(t).Invoke(world, null);
        }

        static Type ResolveType(string name)
        {
            if (_types.TryGetValue(name, out var t)) return t;
            t = _asm?.GetType(name);
            _types[name] = t;
            return t;
        }

        // Reads a public NativeArray<T> field as a list of boxed elements.
        // Guards IsCreated so a disposed/uninitialized buffer yields null
        // rather than throwing. NativeArray<T> implements IEnumerable, so we
        // enumerate without referencing Unity.Collections at compile time.
        static List<object> ReadNativeArray(object sys, string fieldName)
        {
            FieldInfo f = sys.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (f == null) return null;
            object boxed = f.GetValue(sys);
            if (boxed == null) return null;

            PropertyInfo created = boxed.GetType().GetProperty("IsCreated", BindingFlags.Public | BindingFlags.Instance);
            if (created != null && created.GetValue(boxed) is bool b && !b) return null;

            if (boxed is not IEnumerable seq) return null;
            var list = new List<object>();
            foreach (object elem in seq) list.Add(elem);
            return list;
        }

        // Sets SelectedDistrict to citywide (Entity.Null) and returns the prior
        // value (boxed) so the caller can restore it — mirrors InfoLoom's own
        // DataExporter, which saves/restores the player's district selection.
        static object SetSelectedDistrictNull(object sys)
        {
            PropertyInfo p = sys.GetType().GetProperty("SelectedDistrict", BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) return null;
            object saved = p.GetValue(sys);
            p.SetValue(sys, Entity.Null);
            return saved;
        }

        static void RestoreSelectedDistrict(object sys, object saved)
        {
            if (saved == null) return;
            PropertyInfo p = sys.GetType().GetProperty("SelectedDistrict", BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite) p.SetValue(sys, saved);
        }

        static void Invoke(object sys, string method)
        {
            MethodInfo m = sys.GetType().GetMethod(method,
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            m?.Invoke(sys, null);
        }

        static object Member(object box, string name)
        {
            Type t = box.GetType();
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(box);
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f?.GetValue(box);
        }

        static int MInt(object box, string name)
        {
            object v = Member(box, name);
            return v == null ? 0 : Convert.ToInt32(v);
        }

        static float MFloat(object box, string name)
        {
            object v = Member(box, name);
            return v == null ? 0f : Convert.ToSingle(v);
        }

        static string MStr(object box, string name) => Member(box, name)?.ToString();

        static double Round1(float v) => Math.Round(v, 1);

        // ---- resolution ---------------------------------------------------

        static void EnsureResolved()
        {
            if (_resolved) return;
            try
            {
                // Don't latch if the mod manager isn't up yet — a too-early probe
                // (loading screen, first UI tick) would otherwise cache "absent"
                // for the whole session. Retry on the next call instead.
                var modManager = GameManager.instance?.modManager;
                if (modManager == null) return;
                Resolve(modManager);
                _resolved = true;
            }
            catch (Exception ex)
            {
                Mod.Log?.Error(ex, "InfoLoomBridge.Resolve threw.");
                _available = false;
                _resolved = true;
            }
        }

        static void Resolve(ModManager modManager)
        {
            // Match on ASSEMBLY name, not ModManager.ModInfo.name — for
            // subscribed (pdx_mods) mods the latter is the numeric mod folder
            // (e.g. "91433_42"), not "InfoLoomTwo", so a name prefilter would
            // skip it. Mirror CollectLoadedMods: inspect every mod's assembly.
            foreach (ModManager.ModInfo mod in modManager)
            {
                Assembly asm = null;
                try { asm = mod.asset?.assembly; }
                catch { continue; /* asset not loaded / disabled — skip */ }
                if (asm != null && string.Equals(asm.GetName().Name, AssemblyName, StringComparison.Ordinal))
                {
                    _asm = asm;
                    break;
                }
            }
            if (_asm == null)
            {
                Mod.Log?.Info("InfoLoomBridge: InfoLoomTwo not installed; trade/labor disabled.");
                return;
            }

            _version = _asm.GetName().Version?.ToString();

            // Sanity-check the data surface. If neither expected system resolves,
            // InfoLoom reorganized its namespaces — disable rather than risk a
            // half-populated block.
            if (ResolveType(TradeSystemTypeName) == null && ResolveType(WorkforceSystemTypeName) == null)
            {
                Mod.Log?.Warn($"InfoLoomBridge: InfoLoomTwo {_version} loaded but expected systems not found — API drift. trade/labor disabled.");
                return;
            }

            _available = true;
            Mod.Log?.Info($"InfoLoomBridge: InfoLoomTwo {_version} detected; trade/labor enabled.");
        }
    }
}
