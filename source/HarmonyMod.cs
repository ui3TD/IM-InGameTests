using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace InGameTests
{
    /// <summary>An enabled mod whose info.json names a HarmonyID, and its loaded patch assembly.</summary>
    internal sealed class HarmonyMod
    {
        public string Title;
        public string HarmonyId;

        /// <summary>The first enabled installed copy.</summary>
        public Mods._mod Mod;

        /// <summary>The assembly whose methods are applied as patches under the HarmonyID, or else a loaded assembly of that name. Null when not loaded.</summary>
        public Assembly Assembly;

        /// <summary>
        /// One entry per HarmonyID among the enabled mods, in load order. A mod installed both
        /// locally and from the Workshop counts once.
        /// </summary>
        public static List<HarmonyMod> Enabled()
        {
            var owners = new Dictionary<string, Assembly>(StringComparer.Ordinal);
            foreach (MethodBase original in Harmony.GetAllPatchedMethods())
            {
                Patches info = Harmony.GetPatchInfo(original);
                foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                {
                    if (!owners.ContainsKey(patch.owner))
                    {
                        owners[patch.owner] = patch.PatchMethod.DeclaringType.Assembly;
                    }
                }
            }

            var mods = new List<HarmonyMod>();
            foreach (Mods._mod mod in Mods._Mods.Where(m => m != null && m.IsEnabled()))
            {
                string id = ModFilter.HarmonyId(mod);
                if (id == null || mods.Any(m => m.HarmonyId == id))
                {
                    continue;
                }
                owners.TryGetValue(id, out Assembly assembly);
                mods.Add(new HarmonyMod
                {
                    Title = mod.Title,
                    HarmonyId = id,
                    Mod = mod,
                    Assembly = assembly ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == id),
                });
            }
            return mods;
        }

        public override string ToString() => Title + " (" + HarmonyId + ")";
    }
}
