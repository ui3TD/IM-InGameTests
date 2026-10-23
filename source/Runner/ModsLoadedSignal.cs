using HarmonyLib;

namespace InGameTests
{
    // Last step of Mods.LoadModsCoroutine, after mod loaders hooked into LoadMods have applied patches.
    [HarmonyPatch(typeof(Mods), nameof(Mods.StopSpinner))]
    internal static class Mods_StopSpinner_Signal
    {
        internal static bool ModsLoaded;

        private static void Postfix() => ModsLoaded = true;
    }
}
