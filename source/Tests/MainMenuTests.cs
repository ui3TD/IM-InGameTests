using System.Collections;
using System.IO;

namespace InGameTests.Tests
{
    /// <summary>
    /// Routes through the main menu: a load from the menu and a new game. Not tagged with a mod,
    /// so the "affected" suite leaves them out: run them with --suite menu, such as before a
    /// release or after a change to the runner. They take about 20 s together.
    /// </summary>
    internal static class MainMenuTests
    {
        /// <summary>
        /// The quickload round trip through the main menu: quicksave, leave for the menu, load that
        /// file from the menu and quicksave again. Unlike F9 this loads a new scene, so the game's
        /// objects start fresh and only static state carries over. A mod that keeps a value in a
        /// static, or doesn't set one up again when the scene loads, shows here and not in the
        /// quickload.
        /// </summary>
        [InGameTest(Suite = "menu", Order = 3)]
        private static IEnumerator MainMenuRoundTrip(TestContext ctx)
        {
            string beforePath = Path.Combine(SaveSandbox.Dir, "menu_roundtrip_before.json");
            string loadPath = Path.Combine(SaveSandbox.Dir, "menu_roundtrip_load.json");
            string afterPath = Path.Combine(SaveSandbox.Dir, "menu_roundtrip_after.json");

            yield return Game.Quicksave(ctx);
            if (!ctx.Result.Passed)
            {
                yield break;
            }
            File.Copy(Game.QuicksaveFile, beforePath, overwrite: true);
            // Load a copy: the game may rewrite the file it loaded from.
            File.Copy(Game.QuicksaveFile, loadPath, overwrite: true);

            yield return Game.ToMainMenu(ctx);
            if (!ctx.Result.Passed)
            {
                yield break;
            }
            yield return Game.LoadFromMainMenu(ctx, loadPath);
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

            SaveLoadTests.CompareSaves(ctx, beforePath, afterPath, loadDate, sceneLoad: true, "quicksave, main menu, load and quicksave");
            yield return SaveLoadTests.RunADay(ctx, "the load from the main menu");
        }

        /// <summary>
        /// A new free-play game started from the menu after the fixture was played: no save is
        /// loaded, so mods run their new-game setup instead of their load code, with whatever they
        /// kept from the fixture still in memory. It starts on a Thursday with no idols and no intro
        /// (that's story mode only), so 4 days reach the first Monday's onNewWeek. Last, because it
        /// replaces the fixture's game.
        /// </summary>
        [InGameTest(Suite = "menu", Order = 100)]
        private static IEnumerator NewGameRunsToItsFirstWeek(TestContext ctx)
        {
            yield return Game.ToMainMenu(ctx);
            if (!ctx.Result.Passed)
            {
                yield break;
            }
            yield return Game.NewGame(ctx);
            if (!ctx.Result.Passed)
            {
                yield break;
            }
            ctx.Assert(staticVars.dateTime == staticVars.StartDate,
                "Expected the new game to start on " + staticVars.StartDate.ToString("yyyy-MM-dd")
                + ", got " + staticVars.dateTime.ToString("yyyy-MM-dd HH:mm"));
            ctx.Record("idolsAtStart", data_girls.girl.Count);

            mainScript main = Game.Main;
            int newDays = 0;
            int newWeeks = 0;
            mainScript.newDay onDay = () => newDays++;
            mainScript.newWeek onWeek = () => newWeeks++;
            main.onNewDay += onDay;
            main.onNewWeek += onWeek;
            try
            {
                yield return Game.AdvanceDays(ctx, 4);
            }
            finally
            {
                main.onNewDay -= onDay;
                main.onNewWeek -= onWeek;
            }
            ctx.Assert(newDays >= 4, "Expected at least 4 onNewDay events in the new game, got " + newDays);
            ctx.Assert(newWeeks >= 1, "Expected an onNewWeek event in the new game's first 4 days, got " + newWeeks);
        }
    }
}
