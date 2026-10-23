using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;

namespace InGameTests
{
    // Applied in test mode unless -imtest-sound. The game's music, voice and sound effect
    // volumes all come from these getters (each multiplies by the master volume setting),
    // so returning 0 silences the game without pausing audio or touching saved settings.
    // AudioListener.volume can't be used: its setter is stripped from the game's Unity build.
    [HarmonyPatch]
    internal static class Mute_Volumes
    {
        private static bool Prepare() => !Plugin.Options.Sound;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MusicManager), nameof(MusicManager.GetBGMVolume));
            yield return AccessTools.Method(typeof(MusicManager), nameof(MusicManager.GetStoryMusicVolume));
            yield return AccessTools.Method(typeof(MusicManager), nameof(MusicManager.GetAmbientVolume));
            yield return AccessTools.Method(typeof(MusicManager), nameof(MusicManager.GetVoiceoverVolume));
            yield return AccessTools.Method(typeof(MusicManager), nameof(MusicManager.GetSoundVolume));
        }

        private static void Postfix(ref float __result) => __result = 0f;
    }
}
