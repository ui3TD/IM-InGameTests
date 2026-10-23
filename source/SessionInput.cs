using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.EventSystems;

namespace InGameTests
{
    /// <summary>
    /// A player's mouse and keyboard for probe sessions, without OS input. A click raycasts at the
    /// control's centre through the EventSystem, as the UI input module does, and sends the
    /// pointer events a click makes to what the ray hits: so a control that is covered, hidden or
    /// switched off can't be clicked, and one that isn't interactable ignores the click. A key
    /// press goes through the input block's patches, so the game's own key handling reads it.
    /// </summary>
    internal static class SessionInput
    {

        internal sealed class Clickable
        {
            public GameObject Target;
            public string Label;
            public string Kind;
            public string Where;
            public bool Enabled;
            public Vector2 Point;
        }

        /// <summary>The last list 'buttons' showed, for 'click #n'.</summary>
        private static List<Clickable> listed = new List<Clickable>();

        /// <summary>Every control a click at its centre would reach now, top of the screen first.</summary>
        public static List<Clickable> Clickables()
        {
            var found = new List<Clickable>();
            var seen = new HashSet<GameObject>();
            foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (!(behaviour is IPointerClickHandler || behaviour is IPointerDownHandler) || !behaviour.isActiveAndEnabled
                    || behaviour is UnityEngine.UI.ScrollRect || behaviour is UnityEngine.UI.Scrollbar
                    || !seen.Add(behaviour.gameObject) || !(behaviour.transform is RectTransform rect))
                {
                    continue;
                }
                if (!ScreenCentre(rect, out Vector2 point) || Handler(HitAt(point)) != behaviour.gameObject)
                {
                    continue;
                }
                var selectable = behaviour.GetComponent<UnityEngine.UI.Selectable>();
                found.Add(new Clickable
                {
                    Target = behaviour.gameObject,
                    Label = LabelOf(behaviour.gameObject),
                    Kind = KindOf(behaviour.gameObject),
                    Where = PathOf(behaviour.transform),
                    Enabled = selectable == null || selectable.IsInteractable(),
                    Point = point,
                });
            }
            // Top to bottom, then left to right, as a player reads the screen.
            return found.OrderByDescending(c => Mathf.Round(c.Point.y / 20f)).ThenBy(c => c.Point.x).ToList();
        }

        /// <summary>
        /// The list 'buttons' shows. A filter (a regex on label or path) shows fewer lines but keeps each
        /// control's number from the whole list, so 'click #n' means the same either way.
        /// </summary>
        public static string List(string filter = null)
        {
            listed = Clickables();
            if (listed.Count == 0)
            {
                return "Nothing on screen can be clicked: " + CoverAtCentre();
            }
            var regex = filter == null ? null : new Regex(filter, RegexOptions.IgnoreCase);
            List<string> lines = listed.Select((c, i) => new { c, i })
                .Where(x => regex == null || regex.IsMatch(x.c.Label) || regex.IsMatch(x.c.Where))
                .Select(x => "#" + (x.i + 1) + "  " + x.c.Label + "  [" + x.c.Kind + (x.c.Enabled ? "" : ", disabled") + "]  " + x.c.Where)
                .ToList();
            string head = regex == null
                ? listed.Count + " things can be clicked (click #n or click <label>):"
                : lines.Count + " of " + listed.Count + " clickable things match " + filter + ":";
            return head + (lines.Count == 0 ? "" : "\n" + string.Join("\n", lines.ToArray()));
        }

        /// <summary>What a click in the middle of the screen would hit, to explain an empty list.</summary>
        private static string CoverAtCentre()
        {
            try
            {
                RaycastResult hit = HitAt(new Vector2(Screen.width / 2f, Screen.height / 2f));
                return hit.gameObject == null
                    ? "nothing on the UI is hit either (no canvas is up yet?)"
                    : PathOf(hit.gameObject.transform) + " covers the middle of the screen (a fade or a loading overlay?)";
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
        }

        /// <summary>The control 'click' names: #n from the last list, or a label (exact, then a unique part of one).</summary>
        public static Clickable Find(string target)
        {
            if (target.StartsWith("#", StringComparison.Ordinal)
                && int.TryParse(target.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                if (n < 1 || n > listed.Count)
                {
                    throw new ArgumentException("There's no #" + n + " in the last 'buttons' list (" + listed.Count + " entries)");
                }
                Clickable chosen = listed[n - 1];
                if (chosen.Target == null)
                {
                    throw new ArgumentException("#" + n + " (" + chosen.Label + ") is gone; run 'buttons' again");
                }
                return chosen;
            }
            List<Clickable> now = Clickables();
            List<Clickable> exact = now.Where(c => string.Equals(c.Label, target, StringComparison.OrdinalIgnoreCase)).ToList();
            List<Clickable> matches = exact.Count > 0 ? exact
                : now.Where(c => c.Label.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
            listed = now;
            if (matches.Count == 0)
            {
                throw new ArgumentException("Nothing clickable is labelled '" + target + "'. " + List(null));
            }
            throw new ArgumentException(matches.Count + " clickable things match '" + target + "': "
                + string.Join(", ", matches.Select(c => "#" + (listed.IndexOf(c) + 1) + " " + c.Label).ToArray()) + "; use click #n");
        }

        /// <summary>
        /// Clicks the control with the virtual mouse, as a player would: moves the mouse to its centre,
        /// presses and releases the left button. Unity's input module turns that into the hover,
        /// press and click events, and game code that reads the mouse itself sees the same. Fails if
        /// the control is off screen or something else is on top of it.
        /// </summary>
        public static IEnumerator Click(Clickable control, Action<string> report)
        {
            RequireVirtualMouse();
            Vector2 point = Reachable(control);
            yield return MoveTo(point);
            InputBlock_Buttons.SetLeftButton(true);
            yield return null;
            // About as long as a quick click; the game starts a drag only after 0.1 s held.
            yield return new WaitForSecondsRealtime(0.05f);
            InputBlock_Buttons.SetLeftButton(false);
            yield return null;
            yield return null;
            report("Clicked " + control.Label + " [" + control.Kind + (control.Enabled ? "" : ", disabled: a click does nothing") + "]");
        }

        /// <summary>
        /// Drags with the virtual mouse: presses on the first control, holds, moves to the second's
        /// centre in small steps, and releases there.
        /// </summary>
        public static IEnumerator Drag(Clickable from, Clickable to, Action<string> report)
        {
            RequireVirtualMouse();
            Vector2 start = Reachable(from);
            if (to.Target == null || !(to.Target.transform is RectTransform rect) || !ScreenCentre(rect, out Vector2 end))
            {
                throw new ArgumentException(to.Label + " is not on screen");
            }
            yield return MoveTo(start);
            InputBlock_Buttons.SetLeftButton(true);
            yield return null;
            yield return new WaitForSecondsRealtime(0.3f);
            for (int i = 1; i <= 12; i++)
            {
                Vector2 p = Vector2.Lerp(start, end, i / 12f);
                InputBlock_MousePosition.Virtual = new Vector3(p.x, p.y, 0f);
                yield return new WaitForSecondsRealtime(0.04f);
            }
            yield return new WaitForSecondsRealtime(0.2f);
            InputBlock_Buttons.SetLeftButton(false);
            yield return null;
            yield return null;
            report("Dragged " + from.Label + " onto " + to.Label);
        }

        private static void RequireVirtualMouse()
        {
            if (!InputBlock_Buttons.CanInject)
            {
                throw new ArgumentException("The virtual mouse and keyboard work only while input is blocked (a session started without --allow-input); with input allowed, use your own");
            }
        }

        /// <summary>The control's centre, if a click there reaches it now.</summary>
        private static Vector2 Reachable(Clickable control)
        {
            if (control.Target == null || !(control.Target.transform is RectTransform rect) || !ScreenCentre(rect, out Vector2 point))
            {
                throw new ArgumentException(control.Label + " is no longer on screen");
            }
            RaycastResult hit = HitAt(point);
            if (Handler(hit) != control.Target)
            {
                throw new ArgumentException(control.Label + " is covered by " + (hit.gameObject == null ? "nothing (off the UI)" : PathOf(hit.gameObject.transform)));
            }
            return point;
        }

        /// <summary>Moves the virtual mouse and gives the input module two frames to see it, so hover comes first.</summary>
        private static IEnumerator MoveTo(Vector2 point)
        {
            InputBlock_MousePosition.Virtual = new Vector3(point.x, point.y, 0f);
            yield return null;
            yield return null;
        }

        /// <summary>Presses a key for one frame and lets the game handle it.</summary>
        public static IEnumerator Key(string name, Action<string> report)
        {
            RequireVirtualMouse();
            KeyCode key = ParseKey(name);
            InputBlock_Buttons.InjectKey(key, UnityName(key), Time.frameCount + 1);
            yield return null;
            yield return null;
            yield return null;
            report("Pressed " + key + " (\"" + UnityName(key) + "\")");
        }

        /// <summary>
        /// Types into the text field that has focus (click one first), one keyboard event per
        /// character as the field gets them from the keyboard, then Enter to finish: so the field's
        /// own limits and validation apply, and its end-of-edit handlers run.
        /// </summary>
        public static IEnumerator Type(string text, Action<string> report)
        {
            GameObject selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            TMPro.TMP_InputField field = selected == null ? null : selected.GetComponent<TMPro.TMP_InputField>();
            if (field == null || !field.isFocused)
            {
                throw new ArgumentException("No text field has focus" + (selected == null ? "" : " (selected: " + PathOf(selected.transform) + ")")
                    + "; click one first");
            }
            foreach (char c in text)
            {
                field.ProcessEvent(new Event { type = EventType.KeyDown, character = c, keyCode = KeyCode.None });
                field.ForceLabelUpdate();
                yield return null;
            }
            field.ProcessEvent(new Event { type = EventType.KeyDown, character = '\n', keyCode = KeyCode.Return });
            yield return null;
            report("Typed \"" + text + "\" and Enter into " + field.name + "; it now reads \"" + field.text + "\"");
        }

        internal static KeyCode ParseKey(string name)
        {
            if (name.Length == 1 && char.IsDigit(name[0]))
            {
                return KeyCode.Alpha0 + (name[0] - '0');
            }
            try
            {
                if (!Regex.IsMatch(name, "^[0-9]+$"))
                {
                    return (KeyCode)Enum.Parse(typeof(KeyCode), name, ignoreCase: true);
                }
            }
            catch (ArgumentException)
            {
            }
            switch (name.ToLowerInvariant())
            {
                case "esc": return KeyCode.Escape;
                case "enter": return KeyCode.Return;
                case "up": return KeyCode.UpArrow;
                case "down": return KeyCode.DownArrow;
                case "left": return KeyCode.LeftArrow;
                case "right": return KeyCode.RightArrow;
            }
            throw new ArgumentException("Unknown key '" + name + "': use a Unity KeyCode name such as F5, Space, Escape, A or 1");
        }

        /// <summary>The name Input.GetKeyDown(string) takes for this key.</summary>
        internal static string UnityName(KeyCode key)
        {
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
            {
                return ((int)(key - KeyCode.Alpha0)).ToString(CultureInfo.InvariantCulture);
            }
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9)
            {
                return "[" + (int)(key - KeyCode.Keypad0) + "]";
            }
            switch (key)
            {
                case KeyCode.UpArrow: return "up";
                case KeyCode.DownArrow: return "down";
                case KeyCode.LeftArrow: return "left";
                case KeyCode.RightArrow: return "right";
                case KeyCode.LeftShift: return "left shift";
                case KeyCode.RightShift: return "right shift";
                case KeyCode.LeftControl: return "left ctrl";
                case KeyCode.RightControl: return "right ctrl";
                case KeyCode.LeftAlt: return "left alt";
                case KeyCode.RightAlt: return "right alt";
                default: return key.ToString().ToLowerInvariant();
            }
        }

        private static bool ScreenCentre(RectTransform rect, out Vector2 point)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            point = RectTransformUtility.WorldToScreenPoint(camera, (corners[0] + corners[2]) / 2f);
            return point.x >= 0 && point.y >= 0 && point.x < Screen.width && point.y < Screen.height;
        }

        private static readonly List<RaycastResult> Hits = new List<RaycastResult>();

        private static RaycastResult HitAt(Vector2 point)
        {
            if (EventSystem.current == null)
            {
                throw new ArgumentException("The scene has no EventSystem, so nothing can be clicked");
            }
            Hits.Clear();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, Hits);
            return Hits.Count > 0 ? Hits[0] : default(RaycastResult);
        }

        /// <summary>The object a click on this hit reaches: the nearest click or press handler up from it.</summary>
        private static GameObject Handler(RaycastResult hit) =>
            hit.gameObject == null ? null
                : ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject) ?? ExecuteEvents.GetEventHandler<IPointerDownHandler>(hit.gameObject);

        private static string LabelOf(GameObject target)
        {
            var field = target.GetComponent<TMPro.TMP_InputField>();
            if (field != null)
            {
                // A text field's own text, not its placeholder; the placeholder names the field.
                string name = field.placeholder is TMPro.TMP_Text hint ? LabelText.Clean(hint.text) : "";
                return LabelText.Field(name.Length > 0 ? name : target.name, field.text);
            }
            foreach (Component c in target.GetComponentsInChildren<Component>())
            {
                string text = LabelText.Clean(c is TMPro.TMP_Text tmp ? tmp.text : c is UnityEngine.UI.Text ui ? ui.text : null);
                if (text.Length > 0)
                {
                    return text;
                }
            }
            var names = new List<string>();
            for (Transform t = target.transform; t != null && names.Count < 4; t = t.parent)
            {
                names.Add(t.name);
            }
            return LabelText.FromNames(names);
        }

        private static string KindOf(GameObject target)
        {
            MonoBehaviour handler = target.GetComponents<MonoBehaviour>().FirstOrDefault(b => b is IPointerClickHandler || b is IPointerDownHandler);
            return handler == null ? "?" : handler.GetType().Name;
        }

        private static string PathOf(Transform t)
        {
            var names = new List<string>();
            for (int i = 0; t != null && i < 3; i++, t = t.parent)
            {
                names.Insert(0, t.name);
            }
            return string.Join("/", names.ToArray());
        }
    }
}
