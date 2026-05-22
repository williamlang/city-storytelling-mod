using System;
using System.Text;

namespace CityStoryMod
{
    // Pure-C# string/text helpers shared by ExportSystem and reachable by the
    // test project. Anything in this file MUST stay free of Unity / Game.dll /
    // Colossal.* references so `CityStoryMod.Tests` can `<Compile Link>` it
    // without dragging the modding toolchain into the test build.
    public static class TextUtils
    {
        // Lowercases name, replaces runs of non-alphanumeric characters with a
        // single dash, trims trailing dashes. Returns null for null/whitespace
        // input or input that produces an empty result after stripping. Used for
        // per-city directory names and any other filesystem-safe identifier.
        public static string Slugify(string name)
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

        // True if the markdown frontmatter block in `content` contains a
        // non-empty `ended_real_date:` key. Used by ExportSystem to decide
        // whether a session file is still open (no `ended_real_date:` → open)
        // before auto-creating a new session stub.
        public static bool FrontmatterHasEndedRealDate(string content)
        {
            string val = GetFrontmatterField(content, "ended_real_date");
            return !string.IsNullOrEmpty(val);
        }

        // Returns the value of `<key>:` from the leading YAML frontmatter
        // block of `content`, or null if the key is missing / empty / the
        // content has no frontmatter. Treats whatever is between the first
        // two `---` markers as the YAML block.
        //
        // Lightweight scan rather than a full YAML parser — only reads
        // `key: value` shapes (no nested objects, lists, or multi-line
        // strings). Sufficient for the kinds of frontmatter our template
        // and session files use.
        public static string GetFrontmatterField(string content, string key)
        {
            if (content == null || string.IsNullOrEmpty(key)) return null;
            int first = content.IndexOf("---", StringComparison.Ordinal);
            if (first < 0) return null;
            int second = content.IndexOf("---", first + 3, StringComparison.Ordinal);
            if (second < 0) return null;
            string yaml = content.Substring(first + 3, second - first - 3);
            string prefix = key + ":";
            foreach (string line in yaml.Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string value = trimmed.Substring(prefix.Length).Trim();
                return value.Length > 0 ? value : null;
            }
            return null;
        }
    }
}
