using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace InGameTests.Tests
{
    /// <summary>
    /// Load the fixture save with every enabled mod, then let the game run for a
    /// few weeks. Any exception or Debug.LogError along the way fails the test
    /// it happened in (the runner checks the log after each test).
    /// </summary>
    internal static class SmokeTests
    {
        /// <summary>Every enabled Harmony mod in scope has at least one patch applied by its HarmonyID.</summary>
        [InGameTest(Order = 0)]
        private static IEnumerator EveryEnabledHarmonyModIsPatched(TestContext ctx)
        {
            var patchCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                foreach (string owner in Harmony.GetPatchInfo(method).Owners)
                {
                    patchCounts.TryGetValue(owner, out int n);
                    patchCounts[owner] = n + 1;
                }
            }

            // A mod can be installed twice (LocalLow and Workshop) under one HarmonyID;
            // the mod loader patches it if any copy is enabled.
            var copiesById = new Dictionary<string, List<Mods._mod>>(StringComparer.Ordinal);
            foreach (Mods._mod mod in Mods._Mods)
            {
                string id = ModFilter.HarmonyId(mod);
                if (id == null)
                {
                    continue;
                }
                if (!copiesById.TryGetValue(id, out var copies))
                {
                    copiesById[id] = copies = new List<Mods._mod>();
                }
                copies.Add(mod);
            }

            var outOfScope = new List<string>();
            foreach (var pair in copiesById)
            {
                string id = pair.Key;
                Mods._mod mod = pair.Value.FirstOrDefault(m => m.IsEnabled());
                if (mod == null)
                {
                    ctx.Note("Harmony mod not enabled, not tested: " + pair.Value[0].Title + " (" + id + ")");
                    continue;
                }
                if (!ModScope.Includes(mod))
                {
                    outOfScope.Add(mod.Title);
                    continue;
                }

                if (!File.Exists(Path.Combine(mod.Path, id + ".dll")))
                {
                    ctx.Fail(mod.Title + ": info.json names HarmonyID " + id + " but " + id + ".dll is missing");
                    continue;
                }

                patchCounts.TryGetValue(id, out int count);
                ctx.Record(id, count);
                ctx.Assert(count > 0, mod.Title + " (" + id + ") is enabled but has no patches applied");
            }
            ModScope.NoteSkipped(ctx, outOfScope);

            yield break;
        }

        /// <summary>Run the clock forward Weeks weeks at high speed with no player input.</summary>
        [InGameTest(Order = 1)]
        private static IEnumerator AdvanceWeeks(TestContext ctx) => Game.AdvanceDays(ctx, 7 * ctx.Weeks);
    }
}
