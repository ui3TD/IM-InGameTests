using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace InGameTests.Tests
{
    /// <summary>
    /// Load the fixture save with every enabled mod, then let the game run for a
    /// few weeks. Any exception or Debug.LogError along the way fails the test
    /// it happened in (the runner checks the log after each test).
    /// </summary>
    internal static class SmokeTests
    {
        private static readonly AccessTools.FieldRef<ActiveDialogueController, data_dialogues._dialogue._node> ActiveNode =
            AccessTools.FieldRefAccess<ActiveDialogueController, data_dialogues._dialogue._node>("activeNode");


        /// <summary>Every enabled Harmony mod has at least one patch applied by its HarmonyID.</summary>
        [InGameTest(Order = 0)]
        private static IEnumerator EveryEnabledHarmonyModIsPatched(TestContext ctx)
        {
            var patchCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                foreach (string owner in Harmony.GetPatchInfo(method).Owners)
                {
                    patchCounts.TryGetValue(owner, out int n);
                    patchCounts[owner] = n + 1;
                }
            }

            // A mod can be installed twice (LocalLow and Workshop) under one HarmonyID;
            // the mod loader patches it if any copy is enabled.
            var copiesById = new Dictionary<string, List<Mods._mod>>(StringComparer.Ordinal);
            foreach (Mods._mod mod in Mods._Mods)
            {
                string id = ModFilter.HarmonyId(mod);
                if (id == null)
                {
                    continue;
                }
                if (!copiesById.TryGetValue(id, out var copies))
                {
                    copiesById[id] = copies = new List<Mods._mod>();
                }
                copies.Add(mod);
            }

            foreach (var pair in copiesById)
            {
                string id = pair.Key;
                Mods._mod mod = pair.Value.FirstOrDefault(m => m.IsEnabled());
                if (mod == null)
                {
                    ctx.Note("Harmony mod not enabled, not tested: " + pair.Value[0].Title + " (" + id + ")");
                    continue;
                }

                if (!File.Exists(Path.Combine(mod.Path, id + ".dll")))
                {
                    ctx.Fail(mod.Title + ": info.json names HarmonyID " + id + " but " + id + ".dll is missing");
                    continue;
                }

                patchCounts.TryGetValue(id, out int count);
                ctx.Record(id, count);
                ctx.Assert(count > 0, mod.Title + " (" + id + ") is enabled but has no patches applied");
            }

            yield break;
        }

        /// <summary>Run the clock forward Weeks weeks at high speed with no player input.</summary>
        [InGameTest(Order = 1)]
        private static IEnumerator AdvanceWeeks(TestContext ctx)
        {
            mainScript main = Camera.main.GetComponent<mainScript>();
            DateTime startDate = staticVars.dateTime;
            DateTime target = startDate.AddDays(7 * ctx.Weeks);
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
            ctx.Record("realSecondsToAdvance", seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
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
            ctx.Assert(newWeeks >= ctx.Weeks, "Expected at least " + ctx.Weeks + " onNewWeek events, got " + newWeeks);
            ctx.Assert(newDays >= 7 * ctx.Weeks, "Expected at least " + 7 * ctx.Weeks + " onNewDay events, got " + newDays);
        }

        /// <summary>
        /// One player click on the dialogue: pick the first choice button if any are shown,
        /// otherwise click the screen. Returns the choice text when a choice was taken.
        /// </summary>
        internal static string ClickDialogue(ActiveDialogueController dialogue)
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
        private static string Unstall(mainScript main)
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

        private static string DescribeClock()
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
