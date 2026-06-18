using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Colossal.Logging;
using Game.Modding;
using Game.SceneFlow;
using Unity.Collections;
using Unity.Entities;

namespace CityStoryMod.Storyteller
{
    // Reflective, soft-coupled reader for ruzbeh0's Elections mod
    // (https://github.com/ruzbeh0/Elections — assembly "Elections", ModId
    // 146816). No compile-time reference to Elections.dll, so our DLL survives
    // Elections rebuilds across CS2 patches as long as the data surface holds.
    //
    // The entire election is carried in ONE serializable ECS singleton:
    //   Elections.Components.ElectionState : IComponentData
    // — campaign stage, the schedule, up to four parties (player-editable
    // names, reputation, term/win history, tags), up to four candidates (real
    // citizen Entity refs → age/education/wealth/work + a tag), donations,
    // bribery / vote-tampering / corruption signals, legislation, and poll +
    // result tallies. We query that one type, read it once per export, and map
    // it into the snapshot's `politics` block.
    //
    // Churn defense: the mod's author *deliberately* versions ElectionState's
    // field layout (CurrentVersion=23, "known published layouts 17/19/22"), so
    // raw field offsets are unstable. We therefore prefer the struct's PUBLIC
    // ACCESSOR METHODS (GetCandidate(i), GetPartyName(i), GetCandidateTagId(i),
    // HasLegislation(type), …) — the stable API the author maintains across
    // layout changes — and fall back to field-name reads only for scalars that
    // have no accessor. Every probe is name-based and null-tolerant: a renamed
    // or removed member yields a missing sub-value, never a crash. If the
    // ElectionState type itself can't be resolved, the bridge disables and the
    // `politics` block stays null.
    public static class ElectionsBridge
    {
        const string ElectionsAssemblyName = "Elections";
        const string StateTypeName = "Elections.Components.ElectionState";
        const string LegislationEnumTypeName = "Elections.Models.ElectionLegislationType";

        // The mod derives each candidate's profile from the real citizen's ECS
        // attributes. These label maps mirror Elections'
        // ElectionCandidateProfileUtility so we surface the same readings the
        // player sees in the candidate panel — not raw indices.
        //   education: CS2 citizen education 0..4
        //   wealth:    household wealth band 0..4
        //   age:       a CitizenAge band (0 Child, 1 Teen, 2 Adult, 3 Elderly),
        //              NOT a year count
        //   work_type: a threshold, not an index (>=30 student, >=10 working)
        static readonly string[] EducationLabels =
            { "Uneducated", "Poorly Educated", "Educated", "Well Educated", "Highly Educated" };
        static readonly string[] WealthLabels =
            { "Struggling", "Modest income", "Middle income", "Comfortable", "Wealthy" };
        static readonly string[] AgeBandLabels =
            { "Child", "Teen", "Adult", "Elderly" };

        // ElectionCandidateTags ids → human labels (mirror of the mod's internal
        // enum, which is localization-driven and not reflectively cheap to read).
        // Append-only on the mod's side; an unknown id degrades to "tag-#N".
        static readonly string[] CandidateTagLabels =
        {
            /* 0 */ null,
            "Corrupt", "Honest", "Humble Beginnings", "Controversial Past", "Scientist",
            "Frugal", "Lavish", "Grassroots", "Fundraiser", "Poor Speaker",
            "Charismatic", "Union Organizer", "Student Favorite", "Elder Statesperson", "Young Reformer",
            "Technocrat", "Populist", "Elite Connections", "Transit Advocate", "Motorist Advocate",
            "Law and Order", "Environmentalist", "Business Friendly", "Neighborhood Champion", "Polarizing",
            "Revolutionary", "Cautious",
        };

        // ElectionPartyTags ids → human labels (same mirroring rationale).
        static readonly string[] PartyTagLabels =
        {
            /* 0 */ null,
            "Civic Trust", "Reform Slate", "Organized Machine", "Transit Coalition", "Civil Liberties",
            "Local Roots", "Pragmatic", "Student Outreach", "Jobs Focused", "Business Friendly",
            "Unproven", "Ideological", "Divided", "Old Guard", "Overconfident",
            "Complacent", "Elitist", "Scandal Prone", "Disorganized", "Out of Touch",
        };

        const int PartyTagsPerParty = 3;
        const int MaxCandidates = 4;

        static bool _resolved;
        static bool _available;
        static string _version;
        static Type _stateType;
        static Type _legislationEnumType;
        static Array _legislationValues;
        static PropertyInfo _p_activeCandidateCount;
        static MethodInfo _getComponentDataDef;

        static readonly Dictionary<string, MethodInfo> _methods = new Dictionary<string, MethodInfo>();
        static readonly Dictionary<string, FieldInfo> _fields = new Dictionary<string, FieldInfo>();

        public static bool IsAvailable { get { EnsureResolved(); return _available; } }
        public static string Version { get { EnsureResolved(); return _version; } }

        // Slim, comparable summary of the politics state — retained by
        // ExportSystem between exports so the diff block can surface
        // transitions (stage change, election concluded, new mayor) without
        // re-reading the previous snapshot file.
        public struct Summary
        {
            public bool Present;
            public string Stage;
            public string MayorName;
            public int MayorPartyIndex;
            public int ElectionYear;
            public int ElectionMonth;
            public int WinnerIndex;     // victoryPartyWinnerIndex, -1 when unset
            public string WinnerName;
        }

        public sealed class Reading
        {
            public object Block;        // serialized as snapshot.politics
            public Summary Diffable;    // fed to ExportSystem.ComputeDiff
        }

        // Reads the live ElectionState singleton. Returns null when Elections
        // isn't installed, or is installed but hasn't created its state yet
        // (e.g. elections disabled in the mod's own settings, or pre-first-tick).
        // `resolveName` maps a citizen Entity to its rendered label — pass
        // NameSystem.GetRenderedLabelName from the caller (we don't take a
        // Game.UI dependency here).
        public static Reading TryRead(EntityManager em, Func<Entity, string> resolveName, ILog log)
        {
            EnsureResolved();
            if (!_available) return null;

            EntityQuery q = default;
            bool created = false;
            try
            {
                q = em.CreateEntityQuery(ComponentType.ReadOnly(_stateType));
                created = true;
                if (q.IsEmptyIgnoreFilter) return null;

                using var ents = q.ToEntityArray(Allocator.Temp);
                if (ents.Length == 0) return null;

                object box = GetComponentDataBoxed(em, ents[0]);
                if (box == null) return null;

                return Build(box, resolveName, log);
            }
            catch (Exception ex)
            {
                log?.Warn($"ElectionsBridge.TryRead failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (created) { try { q.Dispose(); } catch { /* best-effort */ } }
            }
        }

        static Reading Build(object s, Func<Entity, string> resolveName, ILog log)
        {
            string Name(Entity e) =>
                (e == Entity.Null || resolveName == null) ? null : resolveName(e);

            string stage = FieldEnumStr(s, "stage") ?? "None";
            int active = _p_activeCandidateCount != null
                ? Convert.ToInt32(_p_activeCandidateCount.GetValue(s))
                : 0;

            // Parties. candidateCount also bounds the active party slate; we
            // emit a party only when it has a non-empty name.
            var parties = new List<object>();
            for (int i = 0; i < MaxCandidates; i++)
            {
                string pname = Call(s, "GetPartyName", i) as string;
                if (string.IsNullOrWhiteSpace(pname)) continue;
                parties.Add(new
                {
                    index = i,
                    name = pname,
                    color = ColorHex(CallInt(s, "GetPartyColor", i)),
                    reputation = CallInt(s, "GetPartyReputation", i),
                    consecutive_terms = CallInt(s, "GetPartyConsecutiveTerms", i),
                    wins = CallInt(s, "GetPartyWins", i),
                    tags = PartyTags(s, i),
                });
            }

            // Candidates — real citizens, so identity comes from the game's own
            // name system, not the mod.
            var candidates = new List<object>();
            for (int i = 0; i < active; i++)
            {
                Entity ce = CallEntity(s, "GetCandidate", i);
                if (ce == Entity.Null) continue;
                int partyIndex = CallInt(s, "GetCandidatePartyIndex", i);
                candidates.Add(new
                {
                    index = i,
                    name = Name(ce),
                    party_index = partyIndex,
                    party = (parties.Count > 0) ? (Call(s, "GetPartyName", partyIndex) as string) : null,
                    tag = CandidateTag(CallInt(s, "GetCandidateTagId", i)),
                    age_band = AgeBandLabel(CallInt(s, "GetCandidateAge", i)),
                    education = EducationLabel(CallInt(s, "GetCandidateEducation", i)),
                    work = WorkLabel(CallInt(s, "GetCandidateWorkType", i)),
                    wealth = WealthLabel(CallInt(s, "GetCandidateWealth", i)),
                    support_modifier_percent = CallInt(s, "GetCandidateSupportModifierPercent", i),
                    donation = CallInt(s, "GetCandidateDonation", i),
                    poll_votes = CallInt(s, "GetCandidatePollVotes", i),
                    votes = CallInt(s, "GetCandidateVotes", i),
                    corruption_risk_steps = CallInt(s, "GetCandidateCorruptionRiskSteps", i),
                    negative_softened = CallBool(s, "GetCandidateNegativeSoftened", i),
                });
            }

            // Sitting mayor.
            Entity mayorE = FieldEntity(s, "mayor");
            string mayorName = Name(mayorE);
            int mayorPartyIndex = FieldInt(s, "mayorPartyIndex");
            object mayor = mayorE == Entity.Null ? null : new
            {
                name = mayorName,
                party_index = mayorPartyIndex,
                party = Call(s, "GetPartyName", mayorPartyIndex) as string,
                tag = CandidateTag(FieldInt(s, "mayorTagId")),
                term_year = FieldInt(s, "mayorEffectTermYear"),
                bribe_total = FieldInt(s, "mayorBribeTotal"),
            };

            // Result / turnout. winner_index is -1 until an election concludes.
            int winnerIndex = FieldHas("victoryPartyWinnerIndex") ? FieldInt(s, "victoryPartyWinnerIndex") : -1;
            Entity outgoingE = FieldEntity(s, "outgoingMayor");
            object result = new
            {
                winner_index = winnerIndex >= 0 ? (int?)winnerIndex : null,
                winner_name = winnerIndex >= 0 ? Name(CallEntity(s, "GetCandidate", winnerIndex)) : null,
                turnout_requests = FieldInt(s, "voteRequests"),
                turnout_arrivals = FieldInt(s, "voteArrivals"),
                outgoing_mayor = Name(outgoingE),
                outgoing_mayor_bribe_total = FieldInt(s, "outgoingMayorBribeTotal"),
            };

            // Legislation — enumerate the enum and ask the struct which passed.
            var legislation = new List<string>();
            if (_legislationValues != null)
            {
                foreach (object lv in _legislationValues)
                {
                    object r = Call(s, "HasLegislation", lv);
                    if (r is bool b && b) legislation.Add(lv.ToString());
                }
            }

            // Scandal-engine signals — the storyteller's raw material for secrets.
            object integrity = new
            {
                mayor_bribe_total = FieldInt(s, "mayorBribeTotal"),
                strict_voting_id_law_passed = FieldBool(s, "strictVotingIdLawPassed"),
                vote_tampering_active = FieldEntity(s, "voteTamperingCandidate") != Entity.Null,
                corruption_investigation_active = FieldEntity(s, "corruptionInvestigationMayor") != Entity.Null,
            };

            object block = new
            {
                source = "Elections",
                mod_version = _version,
                stage = stage,
                accelerated_cycle = FieldBool(s, "acceleratedCycle"),
                runoff_active = FieldBool(s, "runoffActive"),
                schedule = new
                {
                    selection = Ym(FieldInt(s, "selectionYear"), FieldInt(s, "selectionMonth")),
                    poll = Ym(FieldInt(s, "pollYear"), FieldInt(s, "pollMonth")),
                    election = Ym(FieldInt(s, "electionYear"), FieldInt(s, "electionMonth")),
                    mayor_term_year = FieldInt(s, "mayorTermYear"),
                },
                mayor = mayor,
                parties = parties,
                candidates = candidates,
                poll_undecided = FieldInt(s, "pollUndecided"),
                result = result,
                legislation = legislation,
                integrity = integrity,
            };

            var summary = new Summary
            {
                Present = true,
                Stage = stage,
                MayorName = mayorName,
                MayorPartyIndex = mayorPartyIndex,
                ElectionYear = FieldInt(s, "electionYear"),
                ElectionMonth = FieldInt(s, "electionMonth"),
                WinnerIndex = winnerIndex,
                WinnerName = winnerIndex >= 0 ? Name(CallEntity(s, "GetCandidate", winnerIndex)) : null,
            };

            return new Reading { Block = block, Diffable = summary };
        }

        // ---- decode helpers ----------------------------------------------

        static string CandidateTag(int id) =>
            (id > 0 && id < CandidateTagLabels.Length) ? CandidateTagLabels[id]
            : (id > 0 ? $"tag-#{id}" : null);

        static string PartyTagLabel(int id) =>
            (id > 0 && id < PartyTagLabels.Length) ? PartyTagLabels[id]
            : (id > 0 ? $"tag-#{id}" : null);

        static List<string> PartyTags(object s, int partyIndex)
        {
            var tags = new List<string>();
            for (int slot = 0; slot < PartyTagsPerParty; slot++)
            {
                string t = PartyTagLabel(CallInt(s, "GetPartyTagId", partyIndex, slot));
                if (t != null) tags.Add(t);
            }
            return tags;
        }

        static string EducationLabel(int level) =>
            (level >= 0 && level < EducationLabels.Length) ? EducationLabels[level] : null;

        static string WealthLabel(int wealth) =>
            (wealth >= 0 && wealth < WealthLabels.Length) ? WealthLabels[wealth] : null;

        static string AgeBandLabel(int band) =>
            (band >= 0 && band < AgeBandLabels.Length) ? AgeBandLabels[band] : null;

        // Mirrors ElectionCandidateProfileUtility.GetWorkLabel — a threshold,
        // not an index.
        static string WorkLabel(int workType) =>
            workType >= 30 ? "Student" : workType >= 10 ? "Working" : "Non-working";

        static string ColorHex(int rgb) => rgb == 0 ? null : $"#{rgb & 0xFFFFFF:x6}";

        // "2026-03" from a year+month pair; null when unset (year 0).
        static string Ym(int year, int month) =>
            year <= 0 ? null : $"{year:D4}-{Math.Max(1, month):D2}";

        // ---- reflective plumbing -----------------------------------------

        static MethodInfo Method(string name, int argc)
        {
            string key = name + "/" + argc;
            if (_methods.TryGetValue(key, out var mi)) return mi;
            mi = _stateType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argc);
            _methods[key] = mi;
            return mi;
        }

        static object Call(object box, string name, params object[] args)
        {
            var mi = Method(name, args.Length);
            return mi?.Invoke(box, args);
        }

        static int CallInt(object box, string name, params object[] args)
        {
            object r = Call(box, name, args);
            return r == null ? 0 : Convert.ToInt32(r);
        }

        static bool CallBool(object box, string name, params object[] args)
        {
            object r = Call(box, name, args);
            return r is bool b && b;
        }

        static Entity CallEntity(object box, string name, params object[] args)
        {
            object r = Call(box, name, args);
            return r is Entity e ? e : Entity.Null;
        }

        static FieldInfo Field(string name)
        {
            if (_fields.TryGetValue(name, out var f)) return f;
            f = _stateType.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            _fields[name] = f;
            return f;
        }

        static bool FieldHas(string name) => Field(name) != null;

        static int FieldInt(object box, string name)
        {
            var f = Field(name);
            return f == null ? 0 : Convert.ToInt32(f.GetValue(box));
        }

        static bool FieldBool(object box, string name)
        {
            var f = Field(name);
            return f != null && f.GetValue(box) is bool b && b;
        }

        static string FieldEnumStr(object box, string name) => Field(name)?.GetValue(box)?.ToString();

        static Entity FieldEntity(object box, string name)
        {
            var f = Field(name);
            return (f != null && f.GetValue(box) is Entity e) ? e : Entity.Null;
        }

        static object GetComponentDataBoxed(EntityManager em, Entity e)
        {
            if (_getComponentDataDef == null)
            {
                _getComponentDataDef = typeof(EntityManager)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetComponentData"
                        && m.IsGenericMethodDefinition
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(Entity));
                if (_getComponentDataDef == null)
                    throw new InvalidOperationException("EntityManager.GetComponentData<T>(Entity) not found — Entities API drift.");
            }
            var g = _getComponentDataDef.MakeGenericMethod(_stateType);
            object emBox = em;   // box the struct so reflection has an instance
            return g.Invoke(emBox, new object[] { e });
        }

        // ---- resolution ---------------------------------------------------

        static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try { Resolve(); }
            catch (Exception ex)
            {
                Mod.Log?.Error(ex, "ElectionsBridge.Resolve threw.");
                _available = false;
            }
        }

        static void Resolve()
        {
            var modManager = GameManager.instance?.modManager;
            if (modManager == null)
            {
                Mod.Log?.Info("ElectionsBridge: modManager unavailable; politics block disabled.");
                return;
            }

            Assembly asm = null;
            foreach (ModManager.ModInfo mod in modManager)
            {
                if (mod.name != null && mod.name.StartsWith(ElectionsAssemblyName, StringComparison.Ordinal))
                {
                    asm = mod.asset?.assembly;
                    if (asm != null && string.Equals(asm.GetName().Name, ElectionsAssemblyName, StringComparison.Ordinal))
                        break;
                    asm = null;
                }
            }
            if (asm == null)
            {
                Mod.Log?.Info("ElectionsBridge: Elections not installed; politics block disabled.");
                return;
            }

            _version = asm.GetName().Version?.ToString();
            _stateType = asm.GetType(StateTypeName);
            if (_stateType == null)
            {
                Mod.Log?.Warn($"ElectionsBridge: {StateTypeName} not found in Elections {_version} — update Elections or report API drift. Politics block disabled.");
                return;
            }

            _p_activeCandidateCount = _stateType.GetProperty("ActiveCandidateCount", BindingFlags.Public | BindingFlags.Instance);

            _legislationEnumType = asm.GetType(LegislationEnumTypeName);
            if (_legislationEnumType != null && _legislationEnumType.IsEnum)
                _legislationValues = Enum.GetValues(_legislationEnumType);

            _available = true;
            Mod.Log?.Info($"ElectionsBridge: Elections {_version} detected; politics block enabled.");
        }
    }
}
