using System.Collections;

namespace InGameTests.Tests
{
    /// <summary>
    /// A new game started from the main menu after the fixture was played: no save is loaded, so
    /// mods run their new-game setup instead of their load code, with whatever they kept from the
    /// fixture still in memory. Any error along the way fails the test.
    /// </summary>
    [ModUnderTest(ModUnderTestAttribute.EveryMod)]
    internal static class NewGameTests
    {
        /// <summary>
        /// Last in every suite it runs in, because it replaces the fixture's game. A free-play game
        /// starts with no idols and no intro (that's story mode only), so the week is the agency's
        /// own daily and weekly code.
        /// </summary>
        [InGameTest(Suite = "menu", Order = 100)]
        private static IEnumerator NewGameRunsAWeek(TestContext ctx)
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
            mainScript.newDay onDay = () => newDays++;
            main.onNewDay += onDay;
            try
            {
                yield return Game.AdvanceDays(ctx, 7);
            }
            finally
            {
                main.onNewDay -= onDay;
            }
            ctx.Record("idolsAfterAWeek", data_girls.girl.Count);
            ctx.Assert(newDays >= 7, "Expected at least 7 onNewDay events in the new game, got " + newDays);
        }
    }
}
