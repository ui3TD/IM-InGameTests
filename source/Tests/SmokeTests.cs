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
        /// <summary>Run the clock forward Weeks weeks at high speed with no player input.</summary>
        [InGameTest(Order = 1)]
        private static IEnumerator AdvanceWeeks(TestContext ctx) => Game.AdvanceDays(ctx, 7 * ctx.Weeks);
    }
}
