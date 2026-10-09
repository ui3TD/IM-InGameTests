using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace InGameTests
{
    /// <summary>
    /// Helpers for tests, including tests in other InGameTests.* assemblies: waiting, and
    /// driving the game clock the way a player clicking through would.
    /// </summary>
    public static class Game
    {
        private static readonly AccessTools.FieldRef<ActiveDialogueController, data_dialogues._dialogue._node> ActiveNode =
            AccessTools.FieldRefAccess<ActiveDialogueController, data_dialogues._dialogue._node>("activeNode");

        public static mainScript Main => Camera.main.GetComponent<mainScript>();

        /// <summary>Waits in real time (independent of Time.timeScale); fails the test on timeout.</summary>
        public static IEnumerator WaitFor(TestContext ctx, Func<bool> condition, float timeoutSeconds, string what)
            => Runner.WaitFor(condition, timeoutSeconds, what, ctx.Result);

        /// <summary>
        /// Runs the clock forward the given number of days at ctx.TimeScale with no player input,
        /// clicking through dialogues and closing whatever pauses the clock. Records the dates,
        /// day/week event counts, dialogue clicks and interventions; fails if the clock stalls.
        /// </summary>
        public static IEnumerator AdvanceDays(TestContext ctx, int days)
        {
            mainScript main = Main;
            DateTime startDate = staticVars.dateTime;
            DateTime target = startDate.AddDays(days);
            int newWeeks = 0;
            int newDays = 0;
            mainScript.newWeek onWeek = () => newWeeks++;
            mainScript.newDay onDay = () => newDays++;
            main.onNewWeek += onWeek;
            main.onNewDay += onDay;

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
                main.onNewWeek -= onWeek;
                main.onNewDay -= onDay;
                main.Time_SetState(mainScript._time_state.pause);
            }

            float seconds = Time.realtimeSinceStartup - realStart;
            ctx.Record("start", startDate.ToString("yyyy-MM-dd HH:mm"));
            ctx.Record("end", staticVars.dateTime.ToString("yyyy-MM-dd HH:mm"));
            ctx.Record("newDayEvents", newDays);
            ctx.Record("newWeekEvents", newWeeks);
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
            ctx.Assert(newWeeks >= days / 7, "Expected at least " + days / 7 + " onNewWeek events, got " + newWeeks);
            ctx.Assert(newDays >= days, "Expected at least " + days + " onNewDay events, got " + newDays);
        }

        /// <summary>
        /// One player click on the dialogue: pick the first choice button if any are shown,
        /// otherwise click the screen. Returns the choice text when a choice was taken.
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

        /// <summary>Close whatever is holding the clock, the way a player clicking through would. Returns what it did.</summary>
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

        /// <summary>Time state, speed, forced pause and open popup, for failure messages.</summary>
        public static string DescribeClock()
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
