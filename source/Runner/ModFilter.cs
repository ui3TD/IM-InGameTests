using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace InGameTests
{
    /// <summary>
    /// --load-vanilla and --load: decide which mods count as enabled for this run.
    /// Applied as a prefix on staticVars._settings.IsModEnabled, which every enabled
    /// check in the game (and in Harmony mod loaders) goes through. Settings writes
    /// are blocked in test mode, so the player's mod list is never changed.
    /// </summary>
    internal static class ModFilter
    {
        private static readonly Regex HarmonyIdPattern = new Regex("\"HarmonyID\"\\s*:\\s*\"([^\"]+)\"");

        internal static bool Prefix(string ModName, ref bool __result)
        {
            __result = !Plugin.Options.LoadVanilla && IsAllowed(ModName);
            return false;
        }

        /// <summary>True when the mod with this folder name matches a --load name.</summary>
        internal static bool IsAllowed(string modName)
        {
            if (Plugin.Options.Load.Any(name => Same(name, modName)))
            {
                return true;
            }
            return Mods._Mods.Where(m => m != null && m.ModName == modName).Any(Matches);
        }

        /// <summary>Folder name, Workshop ID, title or HarmonyID equals one of the --load names.</summary>
        internal static bool Matches(Mods._mod mod)
        {
            return Plugin.Options.Load.Any(name =>
                Same(name, mod.ModName) || Same(name, mod.Title) || Same(name, HarmonyId(mod)));
        }

        /// <summary>--load names that match no installed mod (likely typos).</summary>
        internal static List<string> UnmatchedNames()
        {
            return Plugin.Options.Load
                .Where(name => !Mods._Mods.Any(m => m != null
                    && (Same(name, m.ModName) || Same(name, m.Title) || Same(name, HarmonyId(m)))))
                .ToList();
        }

        /// <summary>The HarmonyID in the mod's info.json, or null.</summary>
        internal static string HarmonyId(Mods._mod mod)
        {
            try
            {
                string info = Path.Combine(mod.Path, "info.json");
                if (!File.Exists(info))
                {
                    return null;
                }
                Match m = HarmonyIdPattern.Match(File.ReadAllText(info));
                return m.Success ? m.Groups[1].Value : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool Same(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
                && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
