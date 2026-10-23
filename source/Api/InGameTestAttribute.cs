using System;

namespace InGameTests
{
    /// <summary>
    /// Marks a coroutine test. Signature: <c>static IEnumerator Name(TestContext ctx)</c>.
    /// Tests run in Order after the fixture save is loaded, all in one game boot. Tests with
    /// NeedsSave = false run first, on the boot menu, before the save loads.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class InGameTestAttribute : Attribute
    {
        /// <summary>The sequence of tests this one runs in. Name a suite after its scenario, not after a mod.</summary>
        public string Suite = "smoke";
        public int Order;

        /// <summary>
        /// False for a test of the main menu itself: it runs on the boot menu, and when no test in the
        /// run needs the save, the save isn't loaded. Such a test must leave the game on the main menu.
        /// </summary>
        public bool NeedsSave = true;
    }

    /// <summary>
    /// Names the one mod a test class's tests exercise, by HarmonyID. Its tests run only while that
    /// mod is in scope (see ModScope), and the "affected" suite runs them whatever their suite.
    /// <see cref="EveryMod"/> marks a class whose checks judge every mod in scope. Tests in an
    /// untagged class are general: they run whenever their suite does, and never in "affected".
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ModUnderTestAttribute : Attribute
    {
        public const string EveryMod = "*";

        public ModUnderTestAttribute(string harmonyId) => HarmonyId = harmonyId;

        public string HarmonyId { get; }
    }
}
