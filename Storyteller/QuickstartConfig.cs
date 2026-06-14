using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityStoryMod.Storyteller
{
    // Pure-C# glue between the native Quickstart wizard (UI) and the agent.
    //
    // Two directions:
    //   BuildConfigBlock   — UI's founding-config JSON → the <<QUICKSTART_CONFIG>>
    //                        text block appended to /new-city (docs/quickstart-wizard.md §4.1).
    //   NormalizeWizardDone — the agent's wizard_done tool input → the compact
    //                        JSON the `wizardDone` value binding carries (§4.2).
    //
    // No Unity / Game.dll dependency (Newtonsoft only) so the test project can
    // link it directly — see tests/CityStoryMod.Tests.
    public static class QuickstartConfig
    {
        public const string BeginMarker = "<<QUICKSTART_CONFIG>>";
        public const string EndMarker = "<<END_CONFIG>>";

        // Field order matches docs/quickstart-wizard.md §4.1. `era` is
        // intentionally absent — the agent always derives it from the in-game
        // date (snapshot.captured_at_ingame), never from config.
        static readonly string[] _fieldOrder =
        {
            "region",
            "name",
            "tone",
            "focus",
            "player_role",
            "player_character_name",
            "real_world_refs",
            "cast_density",
            "content_maturity",
            "secrets_visibility",
            "levelup_storylines",
            "storyteller_proactivity",
            "git_versioning",
            "integrations",
        };

        // Renders the founding-config JSON the UI sends via foundCity() into the
        // line-oriented <<QUICKSTART_CONFIG>> block new-city.md reads. Every
        // known field is emitted in a fixed order so the output is deterministic
        // (and unit-testable). Unparseable / empty input yields a block with all
        // fields blank — new-city.md then asks for everything in prose.
        public static string BuildConfigBlock(string configJson)
        {
            JObject cfg;
            try { cfg = string.IsNullOrWhiteSpace(configJson) ? new JObject() : JObject.Parse(configJson); }
            catch { cfg = new JObject(); }

            var sb = new StringBuilder();
            sb.Append(BeginMarker).Append('\n');
            foreach (string key in _fieldOrder)
            {
                sb.Append(key).Append(": ").Append(FormatValue(key, cfg[key])).Append('\n');
            }
            sb.Append(EndMarker);
            return sb.ToString();
        }

        static string FormatValue(string key, JToken token)
        {
            // Blank name is the "let the storyteller pick" signal.
            if (key == "name")
            {
                string n = token == null || token.Type == JTokenType.Null ? null : token.ToString();
                return string.IsNullOrWhiteSpace(n) ? "(suggest)" : n.Trim();
            }

            if (token == null || token.Type == JTokenType.Null) return "";

            if (token.Type == JTokenType.Array)
            {
                var items = new List<string>();
                foreach (JToken el in (JArray)token)
                {
                    if (el == null || el.Type == JTokenType.Null) continue;
                    string s = el.ToString().Trim();
                    if (s.Length > 0) items.Add(s);
                }
                return string.Join(", ", items);
            }

            if (token.Type == JTokenType.Boolean)
                return token.Value<bool>() ? "true" : "false";

            return token.ToString().Trim();
        }

        // Normalizes a wizard_done tool call's input into the {city_name, region,
        // founded, premise} shape the `wizardDone` binding carries. Missing
        // fields become empty strings so the JSON stays well-formed; the UI
        // treats an empty `founded` as "omit it" (see bindings.ts WizardDone).
        public static string NormalizeWizardDone(JObject input)
        {
            var payload = new JObject
            {
                ["city_name"] = Scalar(input, "city_name"),
                ["region"] = Scalar(input, "region"),
                ["founded"] = Scalar(input, "founded"),
                ["premise"] = Scalar(input, "premise"),
            };
            return payload.ToString(Formatting.None);
        }

        static string Scalar(JObject input, string key)
        {
            JToken t = input?[key];
            return t == null || t.Type == JTokenType.Null ? "" : t.ToString().Trim();
        }
    }
}
