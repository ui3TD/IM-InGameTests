using System.Collections;

namespace InGameTests.Tests
{
    /// <summary>
    /// Load the fixture save with every enabled mod, then let the game run for a
    /// few weeks. Any exception or Debug.LogError along the way fails the test
    /// it happened in (the runner checks the log after each test).
    /// </summary>
    internal static class SmokeTests
    {
        /// <summary>
        /// Run the clock forward Weeks weeks at high speed with no player input; every day and
        /// week fires the game's new-day and new-week events.
        /// </summary>
        [InGameTest(Order = 1)]
        private static IEnumerator AdvanceWeeks(TestContext ctx)
        {
            mainScript main = Game.Main;
            int newDays = 0;
            int newWeeks = 0;
            mainScript.newDay onDay = () => newDays++;
            mainScript.newWeek onWeek = () => newWeeks++;
            main.onNewDay += onDay;
            main.onNewWeek += onWeek;
            try
            {
                yield return Game.AdvanceDays(ctx, 7 * ctx.Weeks);
            }
            finally
            {
                main.onNewDay -= onDay;
                main.onNewWeek -= onWeek;
            }

            ctx.Record("newDayEvents", newDays);
            ctx.Record("newWeekEvents", newWeeks);
            ctx.Assert(newWeeks >= ctx.Weeks, "Expected at least " + ctx.Weeks + " onNewWeek events, got " + newWeeks);
            ctx.Assert(newDays >= 7 * ctx.Weeks, "Expected at least " + 7 * ctx.Weeks + " onNewDay events, got " + newDays);
        }
    }
}
