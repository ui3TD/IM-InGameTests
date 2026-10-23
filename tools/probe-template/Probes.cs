using InGameTests;
using System.Collections;
using System.Linq;

/// <summary>
/// Probe methods have the signature of a test, so one can become a test later by adding
/// [InGameTest] and moving it to a test project. ctx.Record and ctx.Note go into the reply;
/// ctx.Fail shows as an error. Game helpers (Game.AdvanceDays, Game.Girl, ...) all work here.
/// </summary>
internal static class Probes
{
    private static IEnumerator Example(TestContext ctx)
    {
        ctx.Record("idols", data_girls.girl.Count);
        ctx.Record("graduated", data_girls.girl.Count(g => g.status == data_girls._status.graduated));
        yield return Game.AdvanceDays(ctx, 1);
        ctx.Note("A day later it is " + staticVars.dateTime.ToString("yyyy-MM-dd"));
    }
}
