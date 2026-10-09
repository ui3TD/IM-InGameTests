using SimpleJSON;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InGameTests.Tests
{
    /// <summary>
    /// The game's text table after every enabled mod's constants have loaded, in load order.
    /// A mod's own tests only see its file; here a later mod can replace its text.
    /// </summary>
    internal static class ModTextTests
    {
        private static readonly string ConstantsFile = Path.Combine(Path.Combine("JSON", "Constants"), "constants.json");

        /// <summary>
        /// Every text an enabled mod defines is in Language.Data. Texts that a later mod replaces
        /// are noted with the mod that wins, since the first mod then shows that mod's wording.
        /// </summary>
        [InGameTest(Order = 0)]
        private static IEnumerator EveryModTextIsLoaded(TestContext ctx)
        {
            List<Mods._mod> enabled = Mods._Mods.Where(m => m != null && m.IsEnabled()).ToList();

            // Language._Load reads the base game, then each enabled mod in Mods._Mods order; the last definition wins.
            var lastDefinedBy = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Mods._mod mod in enabled)
            {
                foreach (JSONNode node in Constants(mod))
                {
                    lastDefinedBy[node["id"]] = mod.Title;
                }
            }

            int keys = 0;
            int mods = 0;
            var replaced = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (Mods._mod mod in enabled)
            {
                List<JSONNode> constants = Constants(mod).ToList();
                if (constants.Count == 0)
                {
                    continue;
                }
                mods++;
                foreach (JSONNode node in constants)
                {
                    string id = node["id"];
                    string text = node["text"];
                    keys++;
                    if (!Language.Data.TryGetValue(id, out string loaded))
                    {
                        ctx.Fail(mod.Title + ": " + id + " isn't in Language.Data");
                    }
                    else if (loaded != text && lastDefinedBy.TryGetValue(id, out string winner) && winner != mod.Title)
                    {
                        string pair = mod.Title + "'s text is replaced by " + winner + "'s";
                        if (!replaced.TryGetValue(pair, out List<string> ids))
                        {
                            replaced[pair] = ids = new List<string>();
                        }
                        ids.Add(id);
                    }
                }
            }
            foreach (var pair in replaced)
            {
                ctx.Note(pair.Key + ": " + string.Join(", ", pair.Value.ToArray()));
            }
            ctx.Record("mods", mods);
            ctx.Record("keys", keys);
            yield break;
        }

        private static IEnumerable<JSONNode> Constants(Mods._mod mod)
        {
            string file = Path.Combine(mod.Path, ConstantsFile);
            if (!File.Exists(file))
            {
                yield break;
            }
            foreach (JSONNode node in mainScript.ProcessInboundData(File.ReadAllText(file)).AsArray)
            {
                yield return node;
            }
        }
    }
}
