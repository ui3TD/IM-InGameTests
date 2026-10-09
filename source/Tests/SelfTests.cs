using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace InGameTests.Tests
{
    /// <summary>
    /// Checks of the runner's own player stand-ins, which the smoke suite only
    /// exercises when the game happens to raise them. Run with --suite selftest.
    /// </summary>
    internal static class SelfTests
    {
        /// <summary>Game.ClickDialogue gets a dialogue with a choice to the end.</summary>
        [InGameTest(Suite = "selftest")]
        private static IEnumerator DialogueClickThroughFinishesDialogue(TestContext ctx)
        {
            // Start candidates through the game's own entry point (it assigns actors)
            // until one shows: dialogues with a choice that can trigger right now.
            var candidates = data_dialogues.dialogue.Where(d =>
                d.type == data_dialogues._dialogue._type.dialogue
                && d.script.SelectMany(data_dialogues.GetSelfAndChildren)
                    .Any(n => n.type == data_dialogues._dialogue._node._type.choice)
                && SafeCanTrigger(d)).Take(10).ToList();
            ctx.Record("candidates", candidates.Count);

            ActiveDialogueController controller = Game.Main.Data.GetComponent<ActiveDialogueController>();
            data_dialogues._dialogue dialogue = null;
            float start = Time.realtimeSinceStartup;
            foreach (data_dialogues._dialogue candidate in candidates)
            {
                Substories_Manager.StartDialogue(candidate);
                start = Time.realtimeSinceStartup;
                while (!ActiveDialogueController.ShowingDialogue && Time.realtimeSinceStartup - start < 8f)
                {
                    yield return null;
                }
                if (ActiveDialogueController.ShowingDialogue)
                {
                    dialogue = candidate;
                    break;
                }
                ctx.Note("Did not show: " + candidate.id);
            }
            if (dialogue == null)
            {
                ctx.Fail("None of " + candidates.Count + " candidate dialogues could be shown");
                yield break;
            }
            ctx.Record("dialogue", dialogue.id);

            int clicks = 0;
            var choices = new List<string>();
            while (ActiveDialogueController.ShowingDialogue)
            {
                if (clicks > 500 || Time.realtimeSinceStartup - start > 120f)
                {
                    ctx.Fail("Dialogue " + dialogue.id + " still showing after " + clicks + " clicks");
                    yield break;
                }
                string choice = Game.ClickDialogue(controller);
                if (choice != null)
                {
                    choices.Add(choice);
                }
                clicks++;
                yield return new WaitForSecondsRealtime(0.1f);
            }

            ctx.Record("clicks", clicks);
            ctx.Assert(choices.Count > 0, "Expected the click-through to take at least one choice");
            foreach (string choice in choices)
            {
                ctx.Note("Choice taken: " + choice);
            }
        }

        /// <summary>Player input is blocked (unless --allow-input) and the game is muted (unless --sound).</summary>
        [InGameTest(Suite = "selftest", Order = 1)]
        private static IEnumerator InputBlockedAndMuted(TestContext ctx)
        {
            if (!Plugin.Options.AllowInput)
            {
                var methods = new[]
                {
                    AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) }),
                    AccessTools.Method(typeof(Input), nameof(Input.GetKey), new[] { typeof(KeyCode) }),
                    AccessTools.Method(typeof(Input), nameof(Input.GetMouseButtonDown), new[] { typeof(int) }),
                    AccessTools.PropertyGetter(typeof(Input), nameof(Input.mousePosition)),
                };
                foreach (var method in methods)
                {
                    var info = Harmony.GetPatchInfo(method);
                    ctx.Assert(info != null && info.Postfixes.Any(p => p.owner == Plugin.PluginGuid),
                        "Input." + method.Name + " is not blocked");
                }
                ctx.Assert(Input.mousePosition.x < 0, "Input.mousePosition is not off-screen: " + Input.mousePosition);
            }
            if (!Plugin.Options.Sound)
            {
                ctx.Assert(MusicManager.GetSoundVolume() == 0f && MusicManager.GetBGMVolume() == 0f,
                    "Game volume is not zero: sound=" + MusicManager.GetSoundVolume() + " music=" + MusicManager.GetBGMVolume());
            }
            yield break;
        }

        private static bool SafeCanTrigger(data_dialogues._dialogue d)
        {
            try
            {
                return d.CanBeTriggered();
            }
            catch (System.Exception)
            {
                return false;
            }
        }
    }
}
