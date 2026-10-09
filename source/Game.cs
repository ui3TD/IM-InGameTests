using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace InGameTests
{
    /// <summary>
    /// Game primitives for tests, including tests in other InGameTests.* assemblies: player
    /// actions, lookups and scoped settings. README.md ("Adding a helper to the runner") has the
    /// rules a member must pass; instruments such as WaitFor are in TestTools.
    /// </summary>
    public static class Game
    {
        private static readonly AccessTools.FieldRef<ActiveDialogueController, data_dialogues._dialogue._node> ActiveNode =
            AccessTools.FieldRefAccess<ActiveDialogueController, data_dialogues._dialogue._node>("activeNode");

        /// <summary>Lookup: the scene's mainScript.</summary>
        public static mainScript Main => Camera.main.GetComponent<mainScript>();

        /// <summary>
        /// Player action: runs the clock forward the given number of days at ctx.TimeScale, clicking
        /// through dialogues and closing whatever pauses the clock, as a player would. Records the
        /// dates, dialogue clicks and interventions. Fails only if it can't get there: the clock
        /// stalls, or a dialogue never ends.
        /// </summary>
        public static IEnumerator AdvanceDays(TestContext ctx, int days)
        {
            mainScript main = Main;
            DateTime startDate = staticVars.dateTime;
            DateTime target = startDate.AddDays(days);
            var interventions = new Dictionary<string, int>(StringComparer.Ordinal);
            float realStart = Time.realtimeSinceStartup;
            float lastProgress = realStart;
            DateTime lastDate = startDate;
            int stallsWithoutProgress = 0;
            var dialogue = main.Data.GetComponent<ActiveDialogueController>();
            int dialogueClicks = 0;
            var dialogueChoices = new List<string>();
            float lastClick = 0f;

            try
            {
                main.Time_SetState(mainScript._time_state.fast);
                Time.timeScale = ctx.TimeScale;

                while (staticVars.dateTime < target)
                {
                    // The game's settings can reapply these; keep the frame rate uncapped.
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = 1000;

                    // A visual-novel dialogue re-pauses the clock every frame until it is
                    // clicked through. Click like a player, taking the first choice offered.
                    if (ActiveDialogueController.ShowingDialogue)
                    {
                        if (Time.realtimeSinceStartup - lastClick > 0.1f)
                        {
                            lastClick = Time.realtimeSinceStartup;
                            string choice = ClickDialogue(dialogue);
                            if (choice != null)
                            {
                                dialogueChoices.Add(choice);
                            }
                            dialogueClicks++;
                            if (dialogueClicks > 3000)
                            {
                                ctx.Fail("Dialogue did not end after 3000 clicks at " + staticVars.dateTime.ToString("yyyy-MM-dd HH:mm"));
                                break;
                            }
                        }
                        lastProgress = Time.realtimeSinceStartup;
                    }
                    else if (staticVars.dateTime != lastDate)
                    {
                        lastDate = staticVars.dateTime;
                        lastProgress = Time.realtimeSinceStartup;
                        stallsWithoutProgress = 0;
                    }
                    else if (Time.realtimeSinceStartup - lastProgress > 2f)
                    {
                        // Something paused the clock and is waiting for the player.
                        stallsWithoutProgress++;
                        if (stallsWithoutProgress > 15)
                        {
                            ctx.Fail("Clock stalled at " + staticVars.dateTime.ToString("yyyy-MM-dd HH:mm")
                                     + " and could not be resumed. " + DescribeClock());
                            break;
                        }
                        string action = Unstall(main);
                        if (action.StartsWith("no known cause", StringComparison.Ordinal) && stallsWithoutProgress >= 3)
                        {
                            // Clock running but date frozen: an exception in a day/tick handler
                            // ends mainScript.TimeProgress for good (see the logged exception).
                            ctx.Fail("Game clock stopped at " + staticVars.dateTime.ToString("yyyy-MM-dd HH:mm")
                                     + " although time is not paused; the TimeProgress coroutine likely died. " + DescribeClock());
                            break;
                        }
                        interventions.TryGetValue(action, out int n);
                        interventions[action] = n + 1;
                        lastProgress = Time.realtimeSinceStartup;
                    }
                    yield return null;
                }
            }
            finally
            {
                Time.timeScale = 1f;
                main.Time_SetState(mainScript._time_state.pause);
            }

            float seconds = Time.realtimeSinceStartup - realStart;
            ctx.Record("start", startDate.ToString("yyyy-MM-dd HH:mm"));
            ctx.Record("end", staticVars.dateTime.ToString("yyyy-MM-dd HH:mm"));
            ctx.Record("realSecondsToAdvance", seconds.ToString("0.0", CultureInfo.InvariantCulture));
            ctx.Record("dialogueClicks", dialogueClicks);
            foreach (string choice in dialogueChoices)
            {
                ctx.Note("Dialogue choice taken: " + choice);
            }
            foreach (var kv in interventions)
            {
                ctx.Note("Auto-resumed clock " + kv.Value + "x: " + kv.Key);
            }

            ctx.Assert(staticVars.dateTime >= target,
                "Expected the date to reach " + target.ToString("yyyy-MM-dd") + ", got " + staticVars.dateTime.ToString("yyyy-MM-dd"));
        }

        /// <summary>
        /// Player action: one click on the dialogue, picking the first choice button if any are
        /// shown, otherwise clicking the screen. Returns the choice text when a choice was taken.
        /// </summary>
        public static string ClickDialogue(ActiveDialogueController dialogue)
        {
            // Before the first node runs, Next() dereferences a null node; a player can't click that early.
            if (dialogue.Transitioning || dialogue.DisableClick || ActiveDialogueController.ShowingPopup
                || ActiveNode(dialogue) == null)
            {
                return null;
            }
            vnButton[] buttons = dialogue.buttonsContainer.GetComponentsInChildren<vnButton>();
            if (buttons.Length > 0)
            {
                var label = buttons[0].GetComponentInChildren<TMPro.TextMeshProUGUI>();
                buttons[0].onClick();
                return label != null ? label.text : buttons[0].name;
            }
            dialogue.OnScreenClick();
            return null;
        }

        /// <summary>Player action: closes whatever is holding the clock, the way a player clicking through would. Returns what it did.</summary>
        public static string Unstall(mainScript main)
        {
            string state = DescribeClock();
            if (PopupManager.IsThereAnOpenPopup_())
            {
                PopupManager.Close_();
                return "closed popup (" + state + ")";
            }
            if (staticVars.dateTimeAddMinutesPerSecond == 0.0)
            {
                main.Time_Resume();
                if (staticVars.dateTimeAddMinutesPerSecond == 0.0)
                {
                    main.Time_SetState(mainScript._time_state.fast);
                }
                return "resumed paused clock (" + state + ")";
            }
            if (staticVars.timeState == mainScript._time_state.pause)
            {
                main.Time_SetState(mainScript._time_state.fast);
                return "unpaused time state (" + state + ")";
            }
            return "no known cause (" + state + ")";
        }

        /// <summary>Lookup: the idol with this ID in the loaded save; throws if there's none.</summary>
        public static data_girls.girls Girl(int id)
        {
            data_girls.girls girl = data_girls.girl.FirstOrDefault(g => g.id == id);
            if (girl == null)
            {
                throw new InvalidOperationException("Idol " + id + " isn't in the save; fixtures/README.md lists the fixture's idols");
            }
            return girl;
        }

        /// <summary>Scoped setting: selects one value of a policy type; Dispose restores the previous selection.</summary>
        public static IDisposable SelectPolicy(policies._type type, policies._value value)
        {
            List<policies.value> values = policies.Values.Where(v => v.Type == type).ToList();
            Dictionary<policies.value, bool> before = values.ToDictionary(v => v, v => v.Selected);
            foreach (policies.value v in values)
            {
                v.Selected = v.Value == value;
            }
            return TestTools.Restore(() =>
            {
                foreach (var pair in before)
                {
                    pair.Key.Selected = pair.Value;
                }
            });
        }

        /// <summary>Scoped setting: the clock speed the game's per-tick code reads (0 while paused); Dispose restores it.</summary>
        public static IDisposable ClockSpeed(double minutesPerSecond)
        {
            double before = staticVars.dateTimeAddMinutesPerSecond;
            staticVars.dateTimeAddMinutesPerSecond = minutesPerSecond;
            return TestTools.Restore(() => staticVars.dateTimeAddMinutesPerSecond = before);
        }

        /// <summary>Lookup: the agency room the idol is in; throws if she's in none.</summary>
        public static agency._room RoomOf(data_girls.girls girl)
        {
            agency._room room = agency.GetRooms().FirstOrDefault(r => r.girl == girl);
            if (room == null)
            {
                throw new InvalidOperationException("Idol " + girl.id + " isn't in any agency room");
            }
            return room;
        }

        /// <summary>Lookup: the idol profile popup.</summary>
        public static Profile_Popup ProfilePopup =>
            Main.Data.GetComponent<PopupManager>().GetByType(PopupManager._type.girl_profile).obj.GetComponent<Profile_Popup>();

        /// <summary>Player action: opens an idol's profile on a tab, as clicking her portrait and the tab would, and waits a frame.</summary>
        public static IEnumerator OpenProfile(data_girls.girls girl, Profile_Popup._tabs tab)
        {
            if (PopupManager.GetOpenPopupType() != PopupManager._type.girl_profile)
            {
                Main.Data.GetComponent<PopupManager>().Open(PopupManager._type.girl_profile);
            }
            ProfilePopup.Set(girl);
            ProfilePopup.SetTab(tab);
            yield return null;
        }

        /// <summary>Player action: closes every open popup, as their close buttons would, and waits until none is open.</summary>
        public static IEnumerator CloseAllPopups(TestContext ctx)
        {
            for (int i = 0; i < 10 && PopupManager.IsThereAnOpenPopup_(); i++)
            {
                PopupManager.Close_();
                yield return new WaitForSecondsRealtime(0.1f);
            }
            yield return TestTools.WaitFor(ctx, () => !PopupManager.IsThereAnOpenPopup_(), 5f, "popups to close");
        }

        /// <summary>Time state, speed, forced pause and open popup, for failure messages.</summary>
        internal static string DescribeClock()
        {
            string popup = "none";
            try
            {
                if (PopupManager.IsThereAnOpenPopup_())
                {
                    popup = PopupManager.GetOpenPopupType().ToString();
                }
            }
            catch (Exception ex)
            {
                popup = "error: " + ex.GetType().Name;
            }
            return "timeState=" + staticVars.timeState
                   + " minutesPerSecond=" + staticVars.dateTimeAddMinutesPerSecond
                   + " forcedPause=" + staticVars.dateTimeForcedPause
                   + " popup=" + popup;
        }
    }
}
