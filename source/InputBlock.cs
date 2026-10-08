using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace InGameTests
{
    // Applied in test mode unless -imtest-allow-input. A key press or click during a run
    // (Space pauses, 1-3 change speed, Escape opens the menu, clicks open popups) would
    // change what the test sees. Game hotkeys and the UI input module both read
    // UnityEngine.Input, so blocking it there covers keyboard, mouse and UI.
    // Tests drive the game by calling its methods directly, never through Input.

    [HarmonyPatch]
    internal static class InputBlock_Buttons
    {
        /// <summary>How many reads returned "pressed" and were turned into "not pressed".</summary>
        internal static int Suppressed;

        private static bool Prepare() => !Plugin.Options.AllowInput;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            string[] names =
            {
                nameof(Input.GetKey), nameof(Input.GetKeyDown), nameof(Input.GetKeyUp),
                nameof(Input.GetMouseButton), nameof(Input.GetMouseButtonDown), nameof(Input.GetMouseButtonUp),
                nameof(Input.GetButton), nameof(Input.GetButtonDown), nameof(Input.GetButtonUp),
            };
            return typeof(Input).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => names.Contains(m.Name) && m.ReturnType == typeof(bool))
                .Cast<MethodBase>()
                .Concat(new MethodBase[]
                {
                    AccessTools.PropertyGetter(typeof(Input), nameof(Input.anyKey)),
                    AccessTools.PropertyGetter(typeof(Input), nameof(Input.anyKeyDown)),
                })
                // The game's stripped Unity may lack members the reference assemblies have.
                .Where(m => m != null);
        }

        private static void Postfix(ref bool __result)
        {
            if (__result)
            {
                Suppressed++;
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.mousePosition), MethodType.Getter)]
    internal static class InputBlock_MousePosition
    {
        // Off-screen: UI raycasts hit nothing, so nothing hovers or highlights.
        private static readonly Vector3 OffScreen = new Vector3(-10000f, -10000f, 0f);

        private static bool Prepare() => !Plugin.Options.AllowInput;

        private static void Postfix(ref Vector3 __result) => __result = OffScreen;
    }

    [HarmonyPatch]
    internal static class InputBlock_Scroll
    {
        private static MethodBase Getter => AccessTools.PropertyGetter(typeof(Input), nameof(Input.mouseScrollDelta));

        private static bool Prepare() => !Plugin.Options.AllowInput && Getter != null;

        private static MethodBase TargetMethod() => Getter;

        private static void Postfix(ref Vector2 __result)
        {
            if (__result != Vector2.zero)
            {
                InputBlock_Buttons.Suppressed++;
                __result = Vector2.zero;
            }
        }
    }
}
