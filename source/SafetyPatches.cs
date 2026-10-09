using HarmonyLib;
using System;
using System.IO;
using UnityEngine;

namespace InGameTests
{
    // Only applied in test mode (Plugin.Awake patches nothing otherwise).
    // A test run must never touch the player's saves, global data or Steam stats. Saves still
    // happen, autosaves and quicksaves included, but into the run's own folder; run_ingame_tests.py
    // checks afterwards that nothing in the game's data folder changed.

    /// <summary>The run's save folder, standing in for the game's data folder for save files.</summary>
    internal static class SaveSandbox
    {
        internal static string Dir => Path.Combine(Plugin.Options.OutDir, "saves");

        private static string GameDataDir => Path.Combine(Application.persistentDataPath, "data");

        /// <summary>
        /// The sandbox path for a save path: one relative to the data folder (as DataSaver takes
        /// them) or one inside it. Other paths are returned unchanged.
        /// </summary>
        internal static string Map(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }
            if (!Path.IsPathRooted(path))
            {
                return Path.Combine(Dir, path);
            }
            string full = Path.GetFullPath(path);
            string data = Path.GetFullPath(GameDataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Equals(data, StringComparison.OrdinalIgnoreCase))
            {
                return Dir;
            }
            if (full.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(Dir, full.Substring(data.Length + 1));
            }
            return path;
        }
    }

    // Autosave, quicksave (F5) and quickload (F9) all take their file from this.
    [HarmonyPatch(typeof(SaveManager), "GetSaveFileName", new[] { typeof(bool) })]
    internal static class Redirect_SaveFileName
    {
        private static void Postfix(ref string __result) => __result = SaveSandbox.Map(__result);
    }

    [HarmonyPatch(typeof(SaveManager), "GetSaveFileName", new[] { typeof(tasks._chapter) })]
    internal static class Redirect_ChapterSaveFileName
    {
        private static void Postfix(ref string __result) => __result = SaveSandbox.Map(__result);
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.GetPlaythroughPath))]
    internal static class Redirect_PlaythroughPath
    {
        private static void Postfix(ref string __result) => __result = SaveSandbox.Map(__result);
    }

    [HarmonyPatch(typeof(SaveManager), "GetFreeplayAutoSavePath")]
    internal static class Redirect_FreeplayAutoSavePath
    {
        private static void Postfix(ref string __result) => __result = SaveSandbox.Map(__result);
    }

    [HarmonyPatch(typeof(staticVars), nameof(staticVars.ScreenshotForSave))]
    internal static class Redirect_ScreenshotForSave
    {
        private static void Prefix(ref string path) => path = SaveSandbox.Map(path);
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveGlobalData))]
    internal static class Block_SaveGlobalData
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

    // Last step of Mods.LoadModsCoroutine, after mod loaders hooked into LoadMods have applied patches.
    [HarmonyPatch(typeof(Mods), nameof(Mods.StopSpinner))]
    internal static class Mods_StopSpinner_Signal
    {
        internal static bool ModsLoaded;

        private static void Postfix() => ModsLoaded = true;
    }
}
