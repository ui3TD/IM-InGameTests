using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace InGameTests
{
    /// <summary>
    /// A probe session (-imtest-session): the same setup as a test run (save sandbox, blocked
    /// writes, input block, error capture, the fixture or another save loaded), then the game
    /// waits with its clock paused and runs one command at a time from probe_session.py. Commands
    /// arrive as cmd\NNNN.txt in the run folder and get reply\NNNN.txt back: a status line (ok,
    /// stopped or error) and the text. Every exchange is appended to transcript.txt, and watched
    /// calls to watch.log. Nothing here passes or fails; a session answers questions.
    /// </summary>
    internal static class Session
    {
        private static string Dir => Path.GetFullPath(Plugin.Options.OutDir);
        private static string CommandDir => Path.Combine(Dir, "cmd");
        private static string ReplyDir => Path.Combine(Dir, "reply");
        private static string SnapDir => Path.Combine(Dir, "snaps");
        private static string CancelFile => Path.Combine(Dir, "cancel");
        private static string Transcript => Path.Combine(Dir, "transcript.txt");
        private static string WatchLog => Path.Combine(Dir, "watch.log");

        private const int MaxDiffLines = 200;
        private const int MaxStackLines = 15;

        /// <summary>The command running now, 0 between commands; the heartbeat reports it.</summary>
        private static int runningId;
        private static float sessionStart;

        private sealed class Reply
        {
            public string Status = "ok";
            public readonly StringBuilder Text = new StringBuilder();

            public void Line(string line) => Text.Append(Text.Length > 0 ? "\n" : "").Append(line);

            public void Error(string line)
            {
                Status = "error";
                Line(line);
            }

            public void Stopped(string line)
            {
                if (Status == "ok")
                {
                    Status = "stopped";
                }
                Line(line);
            }
        }

        public static IEnumerator Run()
        {
            sessionStart = Time.realtimeSinceStartup;
            Directory.CreateDirectory(CommandDir);
            Directory.CreateDirectory(ReplyDir);
            Directory.CreateDirectory(SnapDir);
            Plugin.Instance.StartCoroutine(Heartbeat());

            var boot = new TestResult { Name = "bootstrap" };
            yield return Runner.Guarded(Runner.BootToMenu(boot), boot);
            Runner.AddLogFailures(boot, 0);
            if (boot.Passed)
            {
                Runner.LoadTestAssemblies(boot);
                if (!Plugin.Options.NoSave)
                {
                    int logStart = LogCapture.Count;
                    yield return Runner.Guarded(Runner.LoadFixture(boot), boot);
                    Runner.AddLogFailures(boot, logStart, "load");
                }
            }
            if (boot.Passed && InGame())
            {
                Game.Main.Time_SetState(mainScript._time_state.pause);
            }
            WriteAtomic(Path.Combine(Dir, "ready.txt"), boot.Passed
                ? "ok\n" + (InGame() ? "Save loaded, " + Now() + ", clock paused" : "On the main menu, no save loaded")
                : "error\n" + string.Join("\n", boot.Failures.ToArray()));
            if (!boot.Passed)
            {
                Quit("startup failed");
                yield break;
            }

            AppDomain.CurrentDomain.AssemblyResolve += ResolveLoaded;
            float lastActivity = Time.realtimeSinceStartup;
            int lastId = 0;
            while (true)
            {
                float now = Time.realtimeSinceStartup;
                if (now - lastActivity > Plugin.Options.IdleMinutes * 60f)
                {
                    Quit("no command for " + Plugin.Options.IdleMinutes + " min (idle limit)");
                    yield break;
                }
                if (now - sessionStart > Plugin.Options.MaxMinutes * 60f)
                {
                    Quit("the session reached its " + Plugin.Options.MaxMinutes + " min maximum");
                    yield break;
                }
                // A cancel with nothing running has nothing to stop.
                TryDelete(CancelFile);
                SessionHooks.Flush(WatchLog);

                string line = NextCommand(lastId, out int id);
                if (line != null)
                {
                    lastId = id;
                    yield return Execute(id, line);
                    lastActivity = Time.realtimeSinceStartup;
                    if (line.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase))
                    {
                        Quit("quit command");
                        yield break;
                    }
                }
                yield return new WaitForSecondsRealtime(0.1f);
            }
        }

        /// <summary>Rewrites heartbeat.txt every second, so the launcher can tell a frozen game from a busy one.</summary>
        private static IEnumerator Heartbeat()
        {
            string path = Path.Combine(Dir, "heartbeat.txt");
            while (true)
            {
                try
                {
                    File.WriteAllText(path, Time.realtimeSinceStartup.ToString("0.0", CultureInfo.InvariantCulture)
                                            + " command=" + runningId + " date=" + Now());
                }
                catch (Exception)
                {
                    // The launcher may be reading it; the next beat will do.
                }
                yield return new WaitForSecondsRealtime(1f);
            }
        }

        /// <summary>The lowest-numbered command after lastId, or null.</summary>
        private static string NextCommand(int lastId, out int id)
        {
            id = 0;
            foreach (string file in Directory.GetFiles(CommandDir, "*.txt"))
            {
                if (int.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                    && n > lastId && (id == 0 || n < id))
                {
                    id = n;
                }
            }
            return id == 0 ? null : File.ReadAllText(Path.Combine(CommandDir, id.ToString("0000", CultureInfo.InvariantCulture) + ".txt")).Trim();
        }

        private static IEnumerator Execute(int id, string line)
        {
            runningId = id;
            LogCapture.Phase = "session #" + id;
            int logStart = LogCapture.Count;
            var reply = new Reply();
            SessionCommand cmd = null;
            try
            {
                cmd = SessionCommand.Parse(line);
            }
            catch (FormatException ex)
            {
                reply.Error(ex.Message);
            }

            if (cmd != null && cmd.Runs)
            {
                yield return Drive(id, cmd, reply);
            }
            else if (cmd != null)
            {
                try
                {
                    Immediate(cmd, reply);
                }
                catch (Exception ex)
                {
                    reply.Error(Message(ex));
                }
            }

            List<LogEntry> errors = LogCapture.Failures(logStart);
            if (errors.Count > 0)
            {
                reply.Line(errors.Count + (errors.Count == 1 ? " error" : " errors") + " logged during this command:");
                foreach (LogEntry e in errors)
                {
                    reply.Line("[" + e.Source + " " + e.Level + "] " + e.Message + StackHead(e.StackTrace));
                }
            }
            SessionHooks.Flush(WatchLog);
            string text = reply.Status + "\n" + reply.Text;
            WriteAtomic(Path.Combine(ReplyDir, id.ToString("0000", CultureInfo.InvariantCulture) + ".txt"), text);
            File.AppendAllText(Transcript, "#" + id + " > " + line + "\n" + text + "\n\n");
            LogCapture.Phase = "session idle";
            runningId = 0;
        }

        /// <summary>
        /// Runs a command that takes frames, stepping it with a CoroutineDriver so the real-time
        /// limit and a cancel stop it wherever it is, nested helpers included. Afterwards the
        /// clock is paused again.
        /// </summary>
        private static IEnumerator Drive(int id, SessionCommand cmd, Reply reply)
        {
            var ctx = new TestContext { Result = new TestResult { Name = "session #" + id } };
            var driver = new CoroutineDriver(Body(cmd, ctx, reply));
            float start = Time.realtimeSinceStartup;
            float limit = Plugin.Options.CommandMinutes * 60f;
            float lastCancelCheck = start;
            string stoppedBy = null;
            while (true)
            {
                bool more;
                object instruction;
                try
                {
                    more = driver.Step(out instruction);
                }
                catch (Exception ex)
                {
                    reply.Error(Message(ex));
                    StopDriver(driver, reply);
                    break;
                }
                if (!more)
                {
                    break;
                }
                float now = Time.realtimeSinceStartup;
                if (now - start > limit)
                {
                    stoppedBy = "the real-time limit (" + Plugin.Options.CommandMinutes + " min)";
                }
                else if (now - lastCancelCheck > 0.25f)
                {
                    lastCancelCheck = now;
                    if (File.Exists(CancelFile))
                    {
                        TryDelete(CancelFile);
                        stoppedBy = "cancel";
                    }
                }
                if (stoppedBy != null)
                {
                    StopDriver(driver, reply);
                    reply.Stopped("Stopped by " + stoppedBy + " at " + Now());
                    break;
                }
                yield return instruction;
            }

            Time.timeScale = 1f;
            if (InGame() && staticVars.timeState != mainScript._time_state.pause)
            {
                Game.Main.Time_SetState(mainScript._time_state.pause);
            }
            foreach (string failure in ctx.Result.Failures)
            {
                reply.Error(failure);
            }
            foreach (string note in ctx.Result.Notes)
            {
                reply.Line("note: " + note);
            }
            foreach (var kv in ctx.Result.Data)
            {
                reply.Line(kv.Key + " = " + kv.Value);
            }
        }

        private static void StopDriver(CoroutineDriver driver, Reply reply)
        {
            foreach (Exception ex in driver.Stop())
            {
                reply.Error("While stopping: " + Message(ex));
            }
        }

        /// <summary>The frames-long part of each running command. Checks throw ArgumentException, which becomes an error reply.</summary>
        private static IEnumerator Body(SessionCommand cmd, TestContext ctx, Reply reply)
        {
            switch (cmd.Verb)
            {
                case "step":
                {
                    RequireGame();
                    DateTime from = staticVars.dateTime;
                    if (cmd.Date.HasValue && cmd.Date.Value <= from)
                    {
                        throw new ArgumentException(Format(cmd.Date.Value) + " isn't after the current date, " + Now());
                    }
                    int watched = SessionHooks.WatchedCalls;
                    yield return Game.Advance(ctx, d => cmd.Date ?? cmd.Span.Value.AddTo(d), null);
                    reply.Line("Now " + Now() + " (was " + Format(from) + ")");
                    WatchedSince(watched, reply);
                    break;
                }
                case "until":
                {
                    RequireGame();
                    bool error = cmd.Target == "error";
                    List<MethodInfo> methods = error ? null : TypeIndex.OfLoadedAssemblies().Methods(cmd.Target);
                    int logStart = LogCapture.Count;
                    DateTime limitDate = cmd.Within.AddTo(staticVars.dateTime);
                    Func<bool> hit = error ? (Func<bool>)(() => LogCapture.Failures(logStart).Count > 0) : () => SessionHooks.UntilReached;
                    int watched = SessionHooks.WatchedCalls;
                    if (!error)
                    {
                        SessionHooks.StartUntil(methods, cmd.Count);
                    }
                    try
                    {
                        yield return Game.Advance(ctx, d => limitDate, hit);
                    }
                    finally
                    {
                        // Also runs when the command is stopped, so the hooks always come off.
                        bool reached = hit();
                        int seen = error ? 0 : SessionHooks.UntilSeen;
                        List<string> calls = error ? new List<string>() : SessionHooks.EndUntil();
                        string what = error ? "an error was logged" : cmd.Target + " was called " + Times(seen) + " (wanted " + cmd.Count + ")";
                        if (reached)
                        {
                            reply.Line("Hit: " + what + "; clock stopped at " + Now());
                        }
                        else if (staticVars.dateTime >= limitDate)
                        {
                            reply.Stopped("Game-time limit (within " + cmd.Within + ") reached at " + Now() + ": "
                                          + (error ? "no error was logged" : what));
                        }
                        else
                        {
                            reply.Line("Not reached yet: " + (error ? "no error was logged" : what) + "; now " + Now());
                        }
                        foreach (string call in calls)
                        {
                            reply.Line("  " + call);
                        }
                        WatchedSince(watched, reply);
                    }
                    break;
                }
                case "snap":
                    RequireGame();
                    yield return Game.Quicksave(ctx);
                    if (ctx.Result.Passed)
                    {
                        File.Copy(Game.QuicksaveFile, SnapPath(cmd.Target), overwrite: true);
                        reply.Line("Saved snap " + cmd.Target + " at " + Now());
                    }
                    break;
                case "restore":
                {
                    RequireGame();
                    string snap = SnapPath(cmd.Target);
                    if (!File.Exists(snap))
                    {
                        throw new ArgumentException("No snap " + cmd.Target + "; snaps: " + SnapNames());
                    }
                    File.Copy(snap, Game.QuicksaveFile, overwrite: true);
                    yield return Game.Quickload(ctx);
                    if (ctx.Result.Passed)
                    {
                        reply.Line("Restored snap " + cmd.Target + ": now " + Now()
                                   + ". A load itself changes a few values the game works out again (SaveLoadTests lists them).");
                    }
                    break;
                }
                case "diff":
                {
                    foreach (string name in new[] { cmd.Target, cmd.Second }.Where(n => n == "now").Distinct())
                    {
                        RequireGame();
                        yield return Game.Quicksave(ctx);
                        if (!ctx.Result.Passed)
                        {
                            yield break;
                        }
                        File.Copy(Game.QuicksaveFile, SnapPath(name), overwrite: true);
                    }
                    foreach (string name in new[] { cmd.Target, cmd.Second })
                    {
                        if (!File.Exists(SnapPath(name)))
                        {
                            throw new ArgumentException("No snap " + name + "; snaps: " + SnapNames());
                        }
                    }
                    var filter = cmd.Filter == null ? null : new Regex(cmd.Filter);
                    List<SaveDiff.Difference> differences = SaveDiff.Compare(
                        SimpleJSON.JSON.Parse(File.ReadAllText(SnapPath(cmd.Target))),
                        SimpleJSON.JSON.Parse(File.ReadAllText(SnapPath(cmd.Second))))
                        .Where(d => filter == null || filter.IsMatch(d.Path)).ToList();
                    reply.Line(differences.Count + (differences.Count == 1 ? " value differs" : " values differ")
                               + (filter == null ? "" : " on paths matching " + cmd.Filter));
                    foreach (SaveDiff.Difference d in differences.Take(MaxDiffLines))
                    {
                        reply.Line(d.ToString());
                    }
                    if (differences.Count > MaxDiffLines)
                    {
                        reply.Line("... " + (differences.Count - MaxDiffLines) + " more; narrow it with a regex: diff a b <regex>");
                    }
                    break;
                }
                case "run":
                    yield return RunProbe(cmd, ctx);
                    break;
                case "click":
                    yield return SessionInput.Click(SessionInput.Find(cmd.Target), reply.Line);
                    yield return AfterInput(reply);
                    break;
                case "key":
                    yield return SessionInput.Key(cmd.Target, reply.Line);
                    yield return AfterInput(reply);
                    break;
                case "drag":
                    yield return SessionInput.Drag(SessionInput.Find(cmd.Target), SessionInput.Find(cmd.Second), reply.Line);
                    yield return AfterInput(reply);
                    break;
                case "type":
                    yield return SessionInput.Type(cmd.Target, reply.Line);
                    yield return AfterInput(reply);
                    break;
            }
        }

        private static int probeLoads;

        /// <summary>
        /// The probe DLL under a name no loaded assembly has. Mono gives back the assembly already
        /// loaded under a name, even for new bytes, so a rebuilt probe would otherwise run its old code.
        /// </summary>
        private static byte[] RenamedProbe(string path)
        {
            using (var probe = Mono.Cecil.AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(path))))
            {
                probe.Name.Name += "_session" + (++probeLoads).ToString(CultureInfo.InvariantCulture);
                var renamed = new MemoryStream();
                probe.Write(renamed);
                return renamed.ToArray();
            }
        }

        /// <summary>
        /// Loads the probe DLL from its bytes under a new name each time, so a rebuilt probe runs its
        /// new code, and runs its static IEnumerator Method(TestContext ctx).
        /// </summary>
        private static IEnumerator RunProbe(SessionCommand cmd, TestContext ctx)
        {
            string path = Path.GetFullPath(cmd.Target);
            if (!File.Exists(path))
            {
                throw new ArgumentException("No probe DLL at " + path);
            }
            Assembly assembly = Assembly.Load(RenamedProbe(path));
            int dot = cmd.Second.LastIndexOf('.');
            if (dot <= 0)
            {
                throw new ArgumentException("Write the probe as Class.Method");
            }
            string typeName = cmd.Second.Substring(0, dot);
            Type type = assembly.GetTypes().FirstOrDefault(t => t.FullName.Replace('+', '.') == typeName || t.Name == typeName);
            if (type == null)
            {
                throw new ArgumentException(Path.GetFileName(path) + " has no class " + typeName);
            }
            MethodInfo method = type.GetMethod(cmd.Second.Substring(dot + 1), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(TestContext) }, null);
            if (method == null || method.ReturnType != typeof(IEnumerator))
            {
                throw new ArgumentException(type.FullName + " has no static IEnumerator " + cmd.Second.Substring(dot + 1) + "(TestContext ctx)");
            }
            IEnumerator body;
            try
            {
                body = (IEnumerator)method.Invoke(null, new object[] { ctx });
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
            yield return body;
        }

        private static void Immediate(SessionCommand cmd, Reply reply)
        {
            switch (cmd.Verb)
            {
                case "get":
                    reply.Line(PathReader.Read(TypeIndex.OfLoadedAssemblies(), cmd.Target));
                    break;
                case "watch":
                {
                    List<MethodInfo> methods = TypeIndex.OfLoadedAssemblies().Methods(cmd.Target);
                    SessionHooks.Watch(methods);
                    foreach (MethodInfo method in methods)
                    {
                        int others = Owners(method).Count(o => o != "im.ingametests.session");
                        reply.Line("Watching " + SessionHooks.Describe(method) + " ("
                                   + (others == 0 ? "no other patches" : others + " other patch owners: " + string.Join(", ", Owners(method).Where(o => o != "im.ingametests.session").ToArray()))
                                   + ")");
                    }
                    reply.Line("Calls go to " + WatchLog + "; 'calls' shows the last ones.");
                    break;
                }
                case "unwatch":
                {
                    int n = SessionHooks.Unwatch(cmd.Target == null ? null : TypeIndex.OfLoadedAssemblies().Methods(cmd.Target));
                    reply.Line(n == 0 ? "Nothing was watched" : "Stopped watching " + n + (n == 1 ? " method" : " methods"));
                    break;
                }
                case "calls":
                {
                    List<string> calls = SessionHooks.Last(cmd.Count);
                    reply.Line(calls.Count == 0 ? "No watched calls yet"
                        : "Last " + calls.Count + " of " + SessionHooks.WatchedCalls + " watched calls (all of them in " + WatchLog + "):");
                    foreach (string call in calls)
                    {
                        reply.Line("  " + call);
                    }
                    break;
                }
                case "patches":
                    foreach (MethodInfo method in TypeIndex.OfLoadedAssemblies().Methods(cmd.Target))
                    {
                        reply.Line(SessionHooks.Describe(method) + ":");
                        Patches info = Harmony.GetPatchInfo(method);
                        if (info == null || info.Owners.Count == 0)
                        {
                            reply.Line("  no patches");
                            continue;
                        }
                        AddPatches(reply, "prefix", info.Prefixes);
                        AddPatches(reply, "postfix", info.Postfixes);
                        AddPatches(reply, "transpiler", info.Transpilers);
                        AddPatches(reply, "finalizer", info.Finalizers);
                    }
                    reply.Line("Harmony runs prefixes and postfixes by priority (higher first), then in the order they were applied.");
                    break;
                case "buttons":
                    reply.Line(SessionInput.List(cmd.Target));
                    break;
                case "status":
                    Status(reply);
                    break;
                case "state":
                    State(reply);
                    break;
                case "help":
                    reply.Line(SessionCommand.Help);
                    break;
                case "quit":
                    reply.Line("Quitting");
                    break;
            }
        }

        private static void AddPatches(Reply reply, string kind, IEnumerable<Patch> patches)
        {
            foreach (Patch patch in patches.OrderByDescending(p => p.priority).ThenBy(p => p.index))
            {
                reply.Line("  " + kind + " " + patch.owner + "  priority " + patch.priority + "  applied #" + patch.index + "  "
                           + ValueText.TypeName(patch.PatchMethod.DeclaringType) + "." + patch.PatchMethod.Name);
            }
        }

        private static IEnumerable<string> Owners(MethodBase method) =>
            Harmony.GetPatchInfo(method)?.Owners ?? (IEnumerable<string>)new string[0];

        /// <summary>
        /// The agency's numbers through the game's own read methods, each line naming its source, so a
        /// value is never a guess at what a field means. Two fan totals: the game's sum, and the idols'
        /// fan groups added up here; a difference between them is worth a look.
        /// </summary>
        private static void Status(Reply reply)
        {
            RequireGame();
            List<data_girls.girls> active = data_girls.girl.Where(g => g.status != data_girls._status.graduated).ToList();
            long groups = active.Sum(g => g.Fans.Sum(f => (long)f.people));
            reply.Line("Date:            " + Now() + "   (staticVars.dateTime)");
            reply.Line("Money:           " + resources.Money().ToString("N0", CultureInfo.InvariantCulture) + "   (resources.Money())");
            reply.Line("Fans:            " + resources.GetFansTotal().ToString("N0", CultureInfo.InvariantCulture) + "   (resources.GetFansTotal())");
            reply.Line("Fans in groups:  " + groups.ToString("N0", CultureInfo.InvariantCulture) + "   (the people of every active idol's fan groups, added up)");
            reply.Line("Fame:            level " + resources.GetFameLevel() + ", " + resources.Get(resources.type.fame).ToString(CultureInfo.InvariantCulture)
                       + " points   (resources.GetFameLevel(), resources.Get(fame))");
            reply.Line("Buzz:            " + resources.Get(resources.type.buzz).ToString(CultureInfo.InvariantCulture) + "   (resources.Get(buzz))");
            reply.Line("Scandal points:  " + resources.Get(resources.type.scandalPoints).ToString(CultureInfo.InvariantCulture) + "   (resources.Get(scandalPoints))");
            reply.Line("Active idols:    " + active.Count + "   (data_girls.girl, not graduated)");
        }

        private static void State(Reply reply)
        {
            reply.Line("Scene: " + SceneManager.GetActiveScene().name);
            if (InGame())
            {
                reply.Line("Date: " + Now());
                reply.Line("Clock: " + Game.DescribeClock());
                reply.Line("Dialogue showing: " + ActiveDialogueController.ShowingDialogue);
            }
            List<MethodBase> watched = SessionHooks.WatchedMethods.ToList();
            reply.Line("Watching: " + (watched.Count == 0 ? "nothing" : string.Join(", ", watched.Select(SessionHooks.Describe).ToArray())));
            reply.Line("Snaps: " + SnapNames());
            List<LogEntry> errors = LogCapture.Failures(0);
            reply.Line("Errors logged in the session: " + errors.Count);
            foreach (LogEntry e in errors.Skip(Math.Max(0, errors.Count - 5)))
            {
                reply.Line("  [" + e.Phase + "] " + e.Message);
            }
            reply.Line("Session: " + Minutes(Time.realtimeSinceStartup - sessionStart) + " min of at most " + Plugin.Options.MaxMinutes
                       + "; quits after " + Plugin.Options.IdleMinutes + " min without a command; each command may run "
                       + Plugin.Options.CommandMinutes + " min");
            reply.Line("Folder: " + Dir);
        }

        /// <summary>Half a second for the UI to react, as a player would wait, then what's on screen.</summary>
        private static IEnumerator AfterInput(Reply reply)
        {
            yield return new WaitForSecondsRealtime(0.5f);
            string popup = "none";
            try
            {
                if (InGame() && PopupManager.IsThereAnOpenPopup_())
                {
                    popup = PopupManager.GetOpenPopupType().ToString();
                }
            }
            catch (Exception)
            {
            }
            reply.Line("Now: scene " + SceneManager.GetActiveScene().name + ", open popup " + popup
                       + (ActiveDialogueController.ShowingDialogue ? ", a dialogue is showing" : ""));
        }

        private static void WatchedSince(int before, Reply reply)
        {
            int n = SessionHooks.WatchedCalls - before;
            if (n == 0)
            {
                return;
            }
            reply.Line(n + " watched " + (n == 1 ? "call" : "calls") + (n > 20 ? "; the last 20:" : ":"));
            foreach (string call in SessionHooks.Last(Math.Min(n, 20)))
            {
                reply.Line("  " + call);
            }
        }

        private static void RequireGame()
        {
            if (!InGame())
            {
                throw new ArgumentException("This needs a loaded game; the session is on the " + SceneManager.GetActiveScene().name + " scene");
            }
        }

        private static bool InGame()
        {
            try
            {
                return SceneManager.GetActiveScene().name == Runner.GameScene && Camera.main != null
                       && Camera.main.GetComponent<mainScript>() != null && !mainScript.IsMainMenu();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Now() => Format(staticVars.dateTime);

        private static string Format(DateTime date) => date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        private static string Times(int n) => n == 1 ? "once" : n + " times";

        private static string Minutes(float seconds) => (seconds / 60f).ToString("0", CultureInfo.InvariantCulture);

        private static string SnapPath(string name) => Path.Combine(SnapDir, name + ".json");

        private static string SnapNames()
        {
            string[] names = Directory.GetFiles(SnapDir, "*.json").Select(Path.GetFileNameWithoutExtension).ToArray();
            return names.Length == 0 ? "none" : string.Join(", ", names);
        }

        /// <summary>A check's own message; anything unexpected in full, with its stack.</summary>
        private static string Message(Exception ex)
        {
            if (ex is TargetInvocationException && ex.InnerException != null)
            {
                ex = ex.InnerException;
            }
            return ex is ArgumentException || ex is FormatException ? ex.Message : "Threw " + ex;
        }

        private static string StackHead(string stack)
        {
            if (string.IsNullOrEmpty(stack))
            {
                return "";
            }
            string[] lines = stack.TrimEnd().Split('\n');
            return "\n    " + string.Join("\n    ", lines.Take(MaxStackLines).Select(l => l.TrimEnd()).ToArray())
                   + (lines.Length > MaxStackLines ? "\n    ..." : "");
        }

        private static void Quit(string reason)
        {
            Plugin.Log.LogInfo("Probe session ended: " + reason);
            try
            {
                File.AppendAllText(Transcript, "Session ended: " + reason + "\n");
                WriteAtomic(Path.Combine(Dir, "ended.txt"), reason);
            }
            catch (Exception)
            {
            }
            Time.timeScale = 1f;
            Application.Quit();
        }

        /// <summary>Write then rename, so the launcher never reads half a file.</summary>
        private static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tmp, path);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>A probe loaded from bytes finds the assemblies it references among the loaded ones.</summary>
        private static Assembly ResolveLoaded(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && a.GetName().Name == name);
        }
    }
}
