using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace InGameTests
{
    /// <summary>
    /// Which mods the per-mod checks judge. Every mod stays loaded; mods outside the scope are
    /// listed in a note instead of checked, so a mod you don't maintain can't fail your runs.
    /// The rules come from the run's scope.txt, staged by the host script from scope.txt,
    /// scope.local.txt and any --scope-file. With no rules, every mod is in scope.
    /// </summary>
    internal static class ModScope
    {
        private static readonly List<Regex> Rules = new List<Regex>();

        /// <summary>The rules as written, for the report.</summary>
        public static IEnumerable<string> RuleTexts => Rules.Select(r => r.ToString());

        /// <summary>One .NET regex per line; blank lines and # comments are skipped.</summary>
        internal static void Load(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                Rules.Add(new Regex(line));
            }
        }

        /// <summary>Whether the mod's HarmonyID, title or folder name (a Workshop ID for Workshop mods) matches a rule.</summary>
        public static bool Includes(Mods._mod mod)
        {
            return Rules.Count == 0 || Names(mod).Any(name => Rules.Any(r => r.IsMatch(name)));
        }

        /// <summary>Notes the mods a check left out, if any.</summary>
        public static void NoteSkipped(TestContext ctx, IEnumerable<string> titles)
        {
            List<string> skipped = titles.Distinct().ToList();
            if (skipped.Count > 0)
            {
                ctx.Note("Not in scope, not checked: " + string.Join(", ", skipped.ToArray()));
            }
        }

        /// <summary>Rules that match no installed mod (likely typos).</summary>
        internal static List<string> UnmatchedRules()
        {
            return Rules
                .Where(r => !Mods._Mods.Any(m => m != null && Names(m).Any(r.IsMatch)))
                .Select(r => r.ToString())
                .ToList();
        }

        private static IEnumerable<string> Names(Mods._mod mod)
        {
            return new[] { ModFilter.HarmonyId(mod), mod.Title, mod.ModName }.Where(n => !string.IsNullOrEmpty(n));
        }
    }
}
