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

        private static KeyCode injectedKey;
        private static string injectedName;
        private static int injectedFrame = -10;

        /// <summary>Whether a key can be injected: only while input is blocked, since these patches deliver it.</summary>
        internal static bool CanInject => !Plugin.Options.AllowInput;

        /// <summary>
        /// Makes the game read one key press on the given frame, as if a player pressed it: GetKeyDown,
        /// GetKey and anyKeyDown on that frame, GetKeyUp on the next. unityName is the name the
        /// string overloads take, such as "f5" or "space".
        /// </summary>
        internal static void InjectKey(KeyCode key, string unityName, int frame)
        {
            injectedKey = key;
            injectedName = unityName;
            injectedFrame = frame;
        }

        /// <summary>A probe session's virtual left mouse button.</summary>
        private static bool leftHeld;
        private static int leftDownFrame = -10;
        private static int leftUpFrame = -10;

        /// <summary>
        /// Presses or releases the virtual left button. Called from a coroutine, so the change is read
        /// as GetMouseButtonDown/Up on the next frame, where the input module will see it.
        /// </summary>
        internal static void SetLeftButton(bool down)
        {
            leftHeld = down;
            if (down)
            {
                leftDownFrame = Time.frameCount + 1;
            }
            else
            {
                leftUpFrame = Time.frameCount + 1;
            }
        }

        private static void Postfix(ref bool __result, MethodBase __originalMethod, object[] __args)
        {
            if (__result)
            {
                Suppressed++;
                __result = false;
            }
            int frame = Time.frameCount;
            if (__originalMethod.Name.StartsWith("GetMouseButton", System.StringComparison.Ordinal))
            {
                if (__args.Length == 1 && __args[0] is int button && button == 0)
                {
                    switch (__originalMethod.Name)
                    {
                        case "GetMouseButton": __result = leftHeld; break;
                        case "GetMouseButtonDown": __result = frame == leftDownFrame; break;
                        case "GetMouseButtonUp": __result = frame == leftUpFrame; break;
                    }
                }
                return;
            }
            if (__originalMethod.Name == "get_anyKey" && leftHeld || __originalMethod.Name == "get_anyKeyDown" && frame == leftDownFrame)
            {
                __result = true;
                return;
            }
            if (frame != injectedFrame && frame != injectedFrame + 1)
            {
                return;
            }
            bool down = frame == injectedFrame;
            switch (__originalMethod.Name)
            {
                case "GetKeyDown":
                case "GetKey":
                    __result = down && Matches(__args);
                    break;
                case "GetKeyUp":
                    __result = !down && Matches(__args);
                    break;
                case "get_anyKey":
                case "get_anyKeyDown":
                    __result = down;
                    break;
            }
        }

        private static bool Matches(object[] args) =>
            args.Length == 1 && (args[0] is KeyCode key ? key == injectedKey
                : args[0] is string name && string.Equals(name, injectedName, System.StringComparison.OrdinalIgnoreCase));
    }

    [HarmonyPatch(typeof(Input), nameof(Input.mousePosition), MethodType.Getter)]
    internal static class InputBlock_MousePosition
    {
        // Off-screen: UI raycasts hit nothing, so nothing hovers or highlights.
        private static readonly Vector3 OffScreen = new Vector3(-10000f, -10000f, 0f);

        /// <summary>A probe session's virtual mouse; null keeps the mouse off-screen.</summary>
        internal static Vector3? Virtual;

        private static bool Prepare() => !Plugin.Options.AllowInput;

        private static void Postfix(ref Vector3 __result) => __result = Virtual ?? OffScreen;
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
