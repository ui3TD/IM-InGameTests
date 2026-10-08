using HarmonyLib;

namespace InGameTests
{
    // Only applied in test mode (Plugin.Awake patches nothing otherwise).
    // A test run must never touch the player's saves, global data or Steam stats.

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveData))]
    internal static class Block_SaveData
    {
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveGlobalData))]
    internal static class Block_SaveGlobalData
    {
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveChapter))]
    internal static class Block_SaveChapter
    {
        private static bool Prefix() => false;
    }

    // Rewrites auto_save.json/manual_save.json in place on startup.
    [HarmonyPatch(typeof(SaveManager), "FixSaveFile")]
    internal static class Block_FixSaveFile
    {
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(Achievements), nameof(Achievements.Unlock))]
    internal static class Block_Unlock
    {
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(Achievements), nameof(Achievements.UnlockStory))]
    internal static class Block_UnlockStory
    {
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(Achievements), nameof(Achievements.Stat_Increase))]
    internal static class Block_StatIncrease
    {
        private static bool Prefix() => false;
    }

    // Applied by Plugin.Awake only with -imtest-vanilla: run the unmodded game.
    internal static class Vanilla_IsModEnabled
    {
        internal static bool Prefix(ref bool __result)
        {
            __result = false;
            return false;
        }
    }

    // Last step of Mods.LoadModsCoroutine, after mod loaders hooked into LoadMods have applied patches.
    [HarmonyPatch(typeof(Mods), nameof(Mods.StopSpinner))]
    internal static class Mods_StopSpinner_Signal
    {
        internal static bool ModsLoaded;

        private static void Postfix() => ModsLoaded = true;
    }
}
