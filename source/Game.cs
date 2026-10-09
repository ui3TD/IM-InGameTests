using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        /// through dialogues and closing whatever pauses the clock, as a player would. It selects the
        /// fast speed unless the clock is already in the fast state, so a faster speed a test or a mod
        /// set there is kept. Records the dates, dialogue clicks and interventions. Fails only if it
        /// can't get there: the clock stalls, or a dialogue never ends.
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
                if (staticVars.timeState != mainScript._time_state.fast)
                {
                    main.Time_SetState(mainScript._time_state.fast);
                }
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

        /// <summary>Scoped setting: a save variable (what mod settings are stored in); Dispose restores it, or deletes it if it was unset.</summary>
        public static IDisposable Variable(string name, string value)
        {
            string before = variables.Get(name);
            variables.Set(name, value);
            return TestTools.Restore(() =>
            {
                if (before == null)
                {
                    variables.Delete(name);
                }
                else
                {
                    variables.Set(name, before);
                }
            });
        }

        /// <summary>Scoped setting: a game option such as random events; Dispose restores it.</summary>
        public static IDisposable Option(staticVars._playerData._options option, bool on)
        {
            staticVars._playerData._option setting = staticVars.PlayerData.GetOption(option);
            bool before = setting.Val;
            setting.Val = on;
            return TestTools.Restore(() => setting.Val = before);
        }

        /// <summary>Lookup: the time control button of this speed; throws if the scene has none.</summary>
        public static TimeControlButton TimeControl(mainScript._time_state state)
        {
            TimeControlButton button = UnityEngine.Object.FindObjectsOfType<TimeControlButton>().FirstOrDefault(b => b.Type == state);
            if (button == null)
            {
                throw new InvalidOperationException("The scene has no time control button for " + state);
            }
            return button;
        }

        /// <summary>Lookup: the audition popup.</summary>
        public static Popup_Audition AuditionPopup =>
            Main.Data.GetComponent<PopupManager>().GetByType(PopupManager._type.audition).obj.GetComponent<Popup_Audition>();

        /// <summary>
        /// Player action: holds an audition of this type without paying, as the game's free audition
        /// does (GenerateAudition), and waits until the popup has a card for every candidate. Regional
        /// and nationwide auditions set their cooldown dates, as in play. Fails if no cards appear.
        /// </summary>
        public static IEnumerator OpenAudition(TestContext ctx, Auditions.type type)
        {
            Auditions auditions = Main.Data.GetComponent<Auditions>();
            Auditions.data data = auditions.Get(type);
            var earlier = new HashSet<data_girls.girls>(data.Girls.Select(g => g.girl));
            auditions.GenerateAudition(data, ShouldPay: false);
            yield return TestTools.WaitFor(ctx,
                () => data.Girls.Count > 0 && data.Girls.All(g => !earlier.Contains(g.girl) && g.CardObject != null),
                30f, "a card for every " + type + " audition candidate");
        }

        /// <summary>
        /// Player action: starts a new election, as the Elections tab's Continue and then the
        /// new-election popup's Continue do, keeping the popup's own choices: the current concert,
        /// the first unreleased single and the cheapest broadcast. Throws if that Continue would be
        /// disabled. The popup then closes on its own.
        /// </summary>
        public static SEvent_SSK._SSK NewElection()
        {
            Main.Data.GetComponent<SEvent_SSK>().NewSSK();
            SSK_New_Popup popup = Main.Data.GetComponent<PopupManager>().GetByType(PopupManager._type.sevent_SSK_new).obj.GetComponent<SSK_New_Popup>();
            SEvent_SSK._SSK election = popup.SSK;
            if (election.Single == null || election.Concert == null)
            {
                popup.OnCancel();
                throw new InvalidOperationException("The new-election popup can't continue: it needs "
                    + (election.Single == null ? "an unreleased single" : "a concert that isn't finished"));
            }
            popup.OnContinue();
            return election;
        }

        /// <summary>
        /// Player action: clicks through the election results popup at ctx.TimeScale: the next
        /// place's Continue while it's shown, and the big button once the last reveal and the idol's
        /// reaction are over, until the popup closes after first place. Fails if the popup doesn't
        /// open, or nothing moves for 20 s.
        /// </summary>
        public static IEnumerator ClickThroughElection(TestContext ctx)
        {
            yield return TestTools.WaitFor(ctx, () => PopupManager.GetOpenPopupType() == PopupManager._type.sevent_SSK,
                30f, "the election results popup");
            if (PopupManager.GetOpenPopupType() != PopupManager._type.sevent_SSK)
            {
                yield break;
            }

            SSK_Popup popup = Main.Data.GetComponent<PopupManager>().GetByType(PopupManager._type.sevent_SSK).obj.GetComponent<SSK_Popup>();
            CanvasGroup nextPlace = popup.VN_Overlay.GetComponent<CanvasGroup>();
            float lastClick = Time.realtimeSinceStartup;
            float revealOver = -1f;
            try
            {
                Time.timeScale = ctx.TimeScale;
                while (PopupManager.GetOpenPopupType() == PopupManager._type.sevent_SSK)
                {
                    if (nextPlace.interactable)
                    {
                        popup.OnContinue();
                        lastClick = Time.realtimeSinceStartup;
                        revealOver = -1f;
                    }
                    else if (popup.BigButton.activeSelf && !ContinueBlocked(popup))
                    {
                        // The idol's reaction ends about a second after the reveal unblocks the button.
                        if (revealOver < 0f)
                        {
                            revealOver = Time.time + 2f;
                        }
                        else if (Time.time >= revealOver)
                        {
                            popup.OnBigButton();
                            lastClick = Time.realtimeSinceStartup;
                            revealOver = -1f;
                        }
                    }
                    if (Time.realtimeSinceStartup - lastClick > 20f)
                    {
                        ctx.Fail("The election results popup stopped at place " + popup.NextPlace + " for 20 s");
                        yield break;
                    }
                    yield return null;
                }
            }
            finally
            {
                Time.timeScale = 1f;
            }
        }

        private static readonly AccessTools.FieldRef<SSK_Popup, bool> ContinueBlocked =
            AccessTools.FieldRefAccess<SSK_Popup, bool>("ContinueBlocked");

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

        /// <summary>
        /// Player action: closes every open popup, as their close buttons would, and waits until none
        /// is open and each has finished hiding. A popup reopened while it still hides is switched off
        /// when the hide ends, so wait here before opening the next one.
        /// </summary>
        public static IEnumerator CloseAllPopups(TestContext ctx)
        {
            for (int i = 0; i < 10 && PopupManager.IsThereAnOpenPopup_(); i++)
            {
                PopupManager.Close_();
                yield return new WaitForSecondsRealtime(0.1f);
            }
            PopupManager popups = Main.Data.GetComponent<PopupManager>();
            yield return TestTools.WaitFor(ctx, () => popups.popups.All(p => !p.open && (p.obj == null || !p.obj.activeSelf)),
                5f, "popups to close and finish hiding");
        }

        /// <summary>Lookup: the scene's save manager.</summary>
        public static SaveManager Saves => Main.Data.GetComponent<SaveManager>();

        /// <summary>
        /// Lookup: the quicksave file F5 writes and F9 loads. In a test run it's in the run's own
        /// save folder, never among the player's saves.
        /// </summary>
        public static string QuicksaveFile =>
            Path.Combine(Path.Combine(Application.persistentDataPath, "data"), GetSaveFileName(Saves, false) + ".json");

        private static readonly Func<SaveManager, bool, string> GetSaveFileName =
            AccessTools.MethodDelegate<Func<SaveManager, bool, string>>(
                AccessTools.Method(typeof(SaveManager), "GetSaveFileName", new[] { typeof(bool) }));

        /// <summary>
        /// Player action: quicksaves, as F5 does, and waits until the game's writer thread has
        /// written <see cref="QuicksaveFile"/>. Fails if the file isn't written within 30 s.
        /// </summary>
        public static IEnumerator Quicksave(TestContext ctx)
        {
            string file = QuicksaveFile;
            // The writer thread holds the file open while it writes, so a missing file, or one
            // that can't be opened alone, isn't finished yet.
            if (File.Exists(file))
            {
                File.Delete(file);
            }
            Saves.SaveData(autoSave: false);
            yield return TestTools.WaitFor(ctx, () => IsWritten(file), 30f, "the quicksave to be written to " + file);
        }

        private static bool IsWritten(string file)
        {
            if (!File.Exists(file))
            {
                return false;
            }
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    return stream.Length > 0;
                }
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// Player action: quickloads, as F9 does, during play and with no scene change, then waits
        /// for the idol portraits and closes the popups the load leaves. Fails if nothing loads,
        /// such as when there's no quicksave.
        /// </summary>
        public static IEnumerator Quickload(TestContext ctx)
        {
            bool loaded = false;
            SaveManager.LoadDelegate onLoad = () => loaded = true;
            SaveManager.LoadEvent += onLoad;
            try
            {
                Saves.LoadData(autoSave: false);
            }
            finally
            {
                SaveManager.LoadEvent -= onLoad;
            }
            if (!loaded)
            {
                ctx.Fail("Quickload loaded nothing from " + QuicksaveFile);
                yield break;
            }
            yield return TestTools.WaitFor(ctx, () => data_girls.ready && data_girls_textures.IsReady(), 60f, "the quickloaded save to be ready");
            // Post-load popups and tweens, as after the fixture load.
            yield return new WaitForSecondsRealtime(3f);
            yield return CloseAllPopups(ctx);
        }

        /// <summary>
        /// Player action: leaves for the main menu, as the Settings tab's Main Menu button does, so
        /// the game autosaves first (into the run's save folder). The Steam game loads mods once, when
        /// Steam starts, so the menu keeps the mods and patches it has. Fails if the menu isn't ready
        /// within 60 s.
        /// </summary>
        public static IEnumerator ToMainMenu(TestContext ctx)
        {
            Main.Data.GetComponent<Tabs_Manager>().CloseTab(force: true);
            Saves.SaveData();
            SceneManager.LoadScene(Runner.MainMenuScene);
            yield return TestTools.WaitFor(ctx, Runner.AtMainMenu, 60f, "the main menu");
            yield return Settle(ctx, "menu", 2f);
        }

        /// <summary>
        /// Player action: loads a save from the main menu, as the Load popup's Load button does, with
        /// a full scene load (F9 loads in place). Then waits for the idol portraits and closes the
        /// popups the load leaves. Needs the main menu (<see cref="ToMainMenu"/>). Fails if the save
        /// isn't loaded within 180 s.
        /// </summary>
        public static IEnumerator LoadFromMainMenu(TestContext ctx, string path)
        {
            yield return Runner.LoadFromMenu(path, ctx.Result);
            if (SceneManager.GetActiveScene().name == Runner.GameScene)
            {
                yield return Settle(ctx, "load", 3f);
                yield return CloseAllPopups(ctx);
            }
        }

        /// <summary>
        /// Player action: starts a free-play game, as the main menu's Free Play and New Game buttons,
        /// the name fields and the difficulty popup's Start do: default options, normal difficulty.
        /// Waits for the game scene and the idol portraits; the intro dialogue is left to the test.
        /// Needs the main menu (<see cref="ToMainMenu"/>). Fails if the game isn't ready within 180 s.
        /// </summary>
        public static IEnumerator NewGame(TestContext ctx)
        {
            Main.Data.GetComponent<MainMenu_Buttons_Controller>().OnClick_FreePlay();
            staticVars._playerData player = staticVars.PlayerData;
            player.SetDefaults();
            player.IsStoryMode = false;
            player.SetGender(_IsMale: true);
            player.SetFirstName("Test");
            player.SetLastName("Runner");
            player.SetGroupName("Test Group");
            Runner.UseMenuLoader().StartNewGame();
            yield return TestTools.WaitFor(ctx,
                () => SceneManager.GetActiveScene().name == Runner.GameScene
                      && data_girls_textures.IsReady()
                      && Camera.main != null && Camera.main.GetComponent<mainScript>() != null
                      && !mainScript.IsMainMenu(),
                180f, "the new game's scene");
            yield return Settle(ctx, "newGame", 3f);
        }

        /// <summary>
        /// After a scene change: waits until no popup is open and no tween plays for 0.2 s, or for
        /// at most maxSeconds, and records how long it took. Never fails.
        /// </summary>
        private static IEnumerator Settle(TestContext ctx, string what, float maxSeconds)
        {
            float start = Time.realtimeSinceStartup;
            float quietSince = -1f;
            while (Time.realtimeSinceStartup - start < maxSeconds)
            {
                bool quiet;
                try
                {
                    quiet = !PopupManager.IsThereAnOpenPopup_() && !AnyTweenPlaying();
                }
                catch (Exception)
                {
                    quiet = false;
                }
                if (!quiet)
                {
                    quietSince = -1f;
                }
                else if (quietSince < 0f)
                {
                    quietSince = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - quietSince >= 0.2f)
                {
                    break;
                }
                yield return null;
            }
            ctx.Record(what + "SettleSeconds", (Time.realtimeSinceStartup - start).ToString("0.0", CultureInfo.InvariantCulture));
        }

        // The game's DOTween (1.2.335) has no public count of playing tweens. Only tweens that
        // will finish on a live object count: not the menu background's endless loops, not tweens
        // whose object a scene change destroyed (they never finish), and not music fades.
        private static readonly FieldInfo ActiveTweens =
            AccessTools.Field(typeof(DG.Tweening.DOTween).Assembly.GetType("DG.Tweening.Core.TweenManager"), "_activeTweens");

        private static readonly AccessTools.FieldRef<DG.Tweening.Tween, int> TweenLoops =
            AccessTools.FieldRefAccess<DG.Tweening.Tween, int>("loops");

        private static readonly AccessTools.FieldRef<DG.Tweening.Tween, object> TweenTarget =
            AccessTools.FieldRefAccess<DG.Tweening.Tween, object>("target");

        private static bool AnyTweenPlaying() =>
            ((DG.Tweening.Tween[])ActiveTweens.GetValue(null)).Any(t => t != null && TweenLoops(t) != -1
                && TweenTarget(t) is UnityEngine.Object target && target != null && !(target is AudioSource)
                && DG.Tweening.TweenExtensions.IsPlaying(t));

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
