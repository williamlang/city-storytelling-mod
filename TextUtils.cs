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
        //
        // Frontmatter is detected as the text between the first two `---`
        // markers in the file. If either marker is missing, returns false.
        public static bool FrontmatterHasEndedRealDate(string content)
        {
            if (content == null) return false;
            int first = content.IndexOf("---", StringComparison.Ordinal);
            if (first < 0) return false;
            int second = content.IndexOf("---", first + 3, StringComparison.Ordinal);
            if (second < 0) return false;
            string yaml = content.Substring(first + 3, second - first - 3);
            foreach (string line in yaml.Split('\n'))
            {
                string trimmed = line.TrimStart();
                const string key = "ended_real_date:";
                if (trimmed.StartsWith(key, StringComparison.Ordinal)
                    && trimmed.Substring(key.Length).Trim().Length > 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
