using HarmonyLib;
using System.Reflection;

namespace InGameTests
{
    /// <summary>
    /// In a probe session the game window counts as focused. Unity's input module ignores the mouse
    /// in an unfocused window, and you usually watch a session from another window.
    /// </summary>
    [HarmonyPatch]
    internal static class SessionFocus
    {
        private static MethodBase Getter => AccessTools.PropertyGetter(typeof(UnityEngine.EventSystems.EventSystem), "isFocused");

        private static bool Prepare() => Plugin.Options.Session && !Plugin.Options.AllowInput && Getter != null;

        private static MethodBase TargetMethod() => Getter;

        private static void Postfix(ref bool __result) => __result = true;
    }
}
