using SimpleJSON;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace InGameTests.Tests
{
    /// <summary>
    /// A save, a load and a second save give the same save file, apart from what the base game
    /// itself changes on a load. A mod whose save and load don't mirror each other, or whose load
    /// hook changes state, shows as a difference. A difference can't be traced to a mod, so it
    /// counts whatever the scope.
    /// </summary>
    [ModUnderTest(ModUnderTestAttribute.EveryMod)]
    internal static class SaveLoadTests
    {
        private const int MaxDifferencesShown = 20;

        /// <summary>
        /// After AdvanceWeeks, so the save holds weeks of play and whatever mods added along the
        /// way. Then a day runs on the reloaded save, since one exception in a day handler stops
        /// the clock for good.
        /// </summary>
        [InGameTest(Order = 2)]
        private static IEnumerator QuicksaveQuickloadRoundTrip(TestContext ctx)
        {
            string beforePath = Path.Combine(SaveSandbox.Dir, "roundtrip_before.json");
            string afterPath = Path.Combine(SaveSandbox.Dir, "roundtrip_after.json");

            yield return Game.Quicksave(ctx);
            if (!ctx.Result.Passed)
            {
                yield break;
            }
            File.Copy(Game.QuicksaveFile, beforePath, overwrite: true);

            yield return Game.Quickload(ctx);
            if (!ctx.Result.Passed)
            {
                yield break;
            }
            string loadDate = ExtensionMethods.ToDataString(staticVars.dateTime);

            yield return Game.Quicksave(ctx);
            if (!ctx.Result.Passed)
            {
                yield break;
            }
            File.Copy(Game.QuicksaveFile, afterPath, overwrite: true);

            CompareSaves(ctx, beforePath, afterPath, loadDate, sceneLoad: false, "quicksave, quickload and quicksave");
            yield return RunADay(ctx, "the quickload");
        }

        internal static void CompareSaves(TestContext ctx, string beforePath, string afterPath, string loadDate, bool sceneLoad, string steps)
        {
            JSONNode before = JSON.Parse(File.ReadAllText(beforePath));
            List<SaveDiff.Difference> differences = SaveDiff.Compare(before, JSON.Parse(File.ReadAllText(afterPath)));
            int baseGame = differences.RemoveAll(d => IsBaseGameChange(d, before, loadDate, sceneLoad));
            ctx.Record("saveBytes", new FileInfo(beforePath).Length);
            ctx.Record("baseGameChangesIgnored", baseGame);
            if (differences.Count > 0)
            {
                ctx.Fail(differences.Count + " save values changed across " + steps + " (files: "
                    + beforePath + ", " + afterPath + "):\n"
                    + string.Join("\n", differences.Take(MaxDifferencesShown).Select(d => d.ToString()).ToArray())
                    + (differences.Count > MaxDifferencesShown ? "\n..." : ""));
            }
            if (HarmonyMod.Enabled().Any(m => !ModScope.Includes(m.Mod)))
            {
                ctx.Note("A difference can come from any loaded mod, including the ones outside the scope.");
            }
        }

        /// <summary>One day on the reloaded game, since one exception in a day handler stops the clock for good.</summary>
        internal static IEnumerator RunADay(TestContext ctx, string after)
        {
            mainScript main = Game.Main;
            int newDays = 0;
            mainScript.newDay onDay = () => newDays++;
            main.onNewDay += onDay;
            try
            {
                yield return Game.AdvanceDays(ctx, 1);
            }
            finally
            {
                main.onNewDay -= onDay;
            }
            ctx.Assert(newDays >= 1, "Expected an onNewDay event after " + after + ", got " + newDays);
        }

        /// <summary>
        /// What the base game changes between two saves around a load, with no mod loaded.
        /// sceneLoad: the load came from the main menu, with a new game scene.
        /// </summary>
        private static bool IsBaseGameChange(SaveDiff.Difference d, JSONNode before, string loadDate, bool sceneLoad)
        {
            switch (Regex.Replace(d.Path, @"\[\d+\]", "[]"))
            {
                // Real time: when the save was made and how long the game has been played.
                case "staticVars__PlayerData.LastSave":
                case "staticVars__PlayerData.Playtime_Seconds":
                    return true;
                // data_girls.LoadFunction makes each saved idol with GenerateGirl, which takes a new ID for her.
                case "data_girls__LastGirlID":
                    return long.TryParse(d.Before, out long a) && long.TryParse(d.After, out long b)
                           && b - a == before["data_girls__Girls"].Count;
                // The agency's fans per group are a total that resources.RecalcFans works out again
                // from the idols who haven't graduated, whenever the fan tooltip redraws. A load
                // redraws it, so a total saved since the idols' fans last changed is brought up to date.
                case "resources__Fans[].people":
                    return d.After == RecalculatedFans(before, int.Parse(Regex.Match(d.Path, @"\[(\d+)\]").Groups[1].Value))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                // A load from the menu runs data_dialogues.LoadFunction before the new scene's
                // data_dialogues.Start, which reads the dialogues in again and so forgets every
                // date they were last triggered. F9 loads in place, with no Start.
                case "data_dialogues__Data":
                    return sceneLoad && d.After == "0 items";
                // _concert.Initiate dates an unfinished concert's finish to the day it's loaded.
                case "SEvent_Concert__Concerts[].FinishDate":
                    return d.After == loadDate;
                // Event_Manager loads a running event (state active) as complete.
                case "Event_Manager__activeEvents[].state":
                    return d.Before == "1" && d.After == "2";
                default:
                    return false;
            }
        }

        /// <summary>resources.RecalcFans for one fan group of a save: its people summed over the idols who haven't graduated.</summary>
        private static long RecalculatedFans(JSONNode save, int index)
        {
            JSONNode group = save["resources__Fans"][index];
            string graduated = ((int)data_girls._status.graduated).ToString(System.Globalization.CultureInfo.InvariantCulture);
            JSONNode idols = save["data_girls__Girls"];
            long total = 0;
            for (int i = 0; i < idols.Count; i++)
            {
                if (idols[i]["status"].Value == graduated)
                {
                    continue;
                }
                JSONNode fans = idols[i]["Fans"];
                for (int j = 0; j < fans.Count; j++)
                {
                    // girls.GetFan: the idol's first fan entry of the group's gender, hardcoreness and age.
                    if (fans[j]["gender"].Value == group["gender"].Value
                        && fans[j]["hardcoreness"].Value == group["hardcoreness"].Value
                        && fans[j]["age"].Value == group["age"].Value)
                    {
                        total += long.Parse(fans[j]["people"].Value, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    }
                }
            }
            return total;
        }
    }
}
