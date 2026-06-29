using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Modding;
using Game.SceneFlow;
using Unity.Entities;

namespace CityStoryMod.Storyteller
{
    // Reflective, soft-coupled writer for CleyraMods' Custom Chirps mod
    // (Thunderstore; assembly "CustomChirps"). Unlike CartoBridge /
    // ElectionsBridge / InfoLoomBridge — which READ a peer mod's state into
    // the snapshot — this bridge is OUTBOUND: it pushes the storyteller's
    // canon into the in-game Chirper feed so events the agent writes surface
    // as live chirps the player sees while building.
    //
    // No compile-time reference to CustomChirps.dll, so our DLL survives
    // Custom Chirps rebuilds across CS2 patches as long as the public API
    // surface holds. The surface we lean on:
    //
    //   CustomChirps.Systems.CustomChirpApiSystem.PostChirp(
    //       string text, DepartmentAccount dept, Entity targetEntity,
    //       string customSenderName = null)            // static, thread-safe
    //   CustomChirps.Systems.DepartmentAccount         // 18-value icon enum
    //
    // PostChirp enqueues onto a thread-safe queue Custom Chirps drains on its
    // own main-thread tick, so we can call it from the ExportSystem heartbeat
    // without worrying about which thread we're on. We post COMPACT chirps
    // with no entity target (Entity.Null) for v1 — the agent authors text and
    // a sender name from canon, but it has no way to hand us an ECS Entity, so
    // clickable {LINK} targets are deferred. customSenderName carries the
    // canon character / civic voice without needing a citizen entity.
    //
    // If Custom Chirps isn't installed (or the API drifted), IsAvailable is
    // false and PostChirp is a no-op returning false; ExportSystem reports
    // "skipped — Custom Chirps not installed" back to the agent via
    // chirp-results.json.
    public static class CustomChirpsBridge
    {
        const string ChirpsAssemblyName = "CustomChirps";
        const string ApiTypeName = "CustomChirps.Systems.CustomChirpApiSystem";
        const string DepartmentEnumTypeName = "CustomChirps.Systems.DepartmentAccount";

        // Icon shown when the agent omits a department or names one Custom
        // Chirps doesn't define. "BusinessNews" reads like a municipal news
        // ticker — the closest vanilla account to a civic-events byline.
        const string FallbackDepartment = "BusinessNews";

        static bool _resolved;
        static bool _available;
        static string _version;
        static Type _deptEnumType;
        static MethodInfo _postChirp;          // PostChirp(string, enum, Entity, string)
        static object _entityNullBoxed;        // boxed Entity.Null, reused per call

        public static bool IsAvailable { get { EnsureResolved(); return _available; } }
        public static string Version { get { EnsureResolved(); return _version; } }

        // Department names Custom Chirps accepts, for the agent-facing
        // documentation and so the caller can validate before posting. Empty
        // until the enum resolves.
        public static IReadOnlyList<string> DepartmentNames
        {
            get
            {
                EnsureResolved();
                return _deptEnumType == null
                    ? Array.Empty<string>()
                    : Enum.GetNames(_deptEnumType);
            }
        }

        // Post one compact chirp. Returns true if the request was handed to
        // Custom Chirps' queue, false if the bridge is unavailable or the call
        // threw. `resolvedDepartment` reports which icon was actually used
        // (may differ from the request when an unknown name fell back), for
        // the results file. `error` carries the exception message on failure.
        public static bool PostChirp(string text, string department, string senderName,
            out string resolvedDepartment, out string error)
        {
            resolvedDepartment = null;
            error = null;
            EnsureResolved();
            if (!_available)
            {
                error = "Custom Chirps not installed";
                return false;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "empty chirp text";
                return false;
            }

            try
            {
                object deptValue = ResolveDepartment(department, out resolvedDepartment);
                string sender = string.IsNullOrWhiteSpace(senderName) ? null : senderName.Trim();
                // PostChirp(string text, DepartmentAccount dept, Entity target, string customSenderName)
                _postChirp.Invoke(null, new object[] { text.Trim(), deptValue, _entityNullBoxed, sender });
                return true;
            }
            catch (Exception ex)
            {
                // Unwrap the reflection wrapper so the results file shows the
                // real cause, not "Exception has been thrown by the target".
                error = (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        // Parse the requested department name into the enum (case-insensitive),
        // falling back to FallbackDepartment when blank or unknown.
        static object ResolveDepartment(string name, out string used)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                try
                {
                    object v = Enum.Parse(_deptEnumType, name.Trim(), ignoreCase: true);
                    used = v.ToString();
                    return v;
                }
                catch { /* unknown name — fall through to the default icon */ }
            }
            object fallback = Enum.Parse(_deptEnumType, FallbackDepartment, ignoreCase: true);
            used = fallback.ToString();
            return fallback;
        }

        static void EnsureResolved()
        {
            if (_resolved) return;
            try
            {
                // Don't latch if the mod manager isn't up yet — a too-early
                // probe (loading screen, first UI tick) would cache "absent"
                // for the whole session. Retry on the next call. Mirrors
                // ElectionsBridge.EnsureResolved.
                var modManager = GameManager.instance?.modManager;
                if (modManager == null) return;
                Resolve(modManager);
                _resolved = true;
            }
            catch (Exception ex)
            {
                Mod.Log?.Error(ex, "CustomChirpsBridge.Resolve threw.");
                _available = false;
                _resolved = true;
            }
        }

        static void Resolve(ModManager modManager)
        {
            // Match on ASSEMBLY name, not ModManager.ModInfo.name — subscribed
            // (pdx_mods) mods carry a numeric folder name, so a name prefilter
            // would skip them. Inspect every mod's assembly. Mirrors the other
            // bridges.
            Assembly asm = null;
            foreach (ModManager.ModInfo mod in modManager)
            {
                Assembly candidate = null;
                try { candidate = mod.asset?.assembly; }
                catch { continue; }
                if (candidate != null && string.Equals(candidate.GetName().Name, ChirpsAssemblyName, StringComparison.Ordinal))
                {
                    asm = candidate;
                    break;
                }
            }
            if (asm == null)
            {
                Mod.Log?.Info("CustomChirpsBridge: Custom Chirps not installed; chirp posting disabled.");
                return;
            }

            _version = asm.GetName().Version?.ToString();

            Type apiType = asm.GetType(ApiTypeName);
            _deptEnumType = asm.GetType(DepartmentEnumTypeName);
            if (apiType == null || _deptEnumType == null || !_deptEnumType.IsEnum)
            {
                Mod.Log?.Warn($"CustomChirpsBridge: expected API types not found in Custom Chirps {_version} — update Custom Chirps or report API drift. Chirp posting disabled.");
                return;
            }

            // Bind the compact PostChirp overload: static, 4 params
            // (string, DepartmentAccount, Entity, string).
            _postChirp = apiType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "PostChirp" && MatchesPostChirpSignature(m));
            if (_postChirp == null)
            {
                Mod.Log?.Warn($"CustomChirpsBridge: PostChirp(string, DepartmentAccount, Entity, string) not found in Custom Chirps {_version}. Chirp posting disabled.");
                _deptEnumType = null;
                return;
            }

            _entityNullBoxed = Entity.Null;
            _available = true;
            Mod.Log?.Info($"CustomChirpsBridge: Custom Chirps {_version} detected; chirp posting enabled.");
        }

        static bool MatchesPostChirpSignature(MethodInfo m)
        {
            var p = m.GetParameters();
            return p.Length == 4
                && p[0].ParameterType == typeof(string)
                && p[1].ParameterType == _deptEnumType
                && p[2].ParameterType == typeof(Entity)
                && p[3].ParameterType == typeof(string);
        }
    }
}
