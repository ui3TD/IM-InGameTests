using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace InGameTests
{
    /// <summary>
    /// Marks a coroutine test. Signature: <c>static IEnumerator Name(TestContext ctx)</c>.
    /// Tests run in Order after the fixture save is loaded, all in one game boot.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class InGameTestAttribute : Attribute
    {
        public string Suite = "smoke";
        public int Order;
    }

    internal sealed class TestResult
    {
        public string Name;
        public bool Passed = true;
        public float Seconds;
        public readonly List<string> Failures = new List<string>();
        public readonly List<string> Notes = new List<string>();
        internal readonly Dictionary<string, string> Data = new Dictionary<string, string>();
    }

    public sealed class TestContext
    {
        internal TestResult Result;

        public void Fail(string message)
        {
            Result.Passed = false;
            Result.Failures.Add(message);
        }

        public void Assert(bool condition, string message)
        {
            if (!condition)
            {
                Fail(message);
            }
        }

        public void Note(string message) => Result.Notes.Add(message);

        public void Record(string key, object value) => Result.Data[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

        public int Weeks => Plugin.Options.Weeks;

        public float TimeScale => Plugin.Options.TimeScale;
    }

    internal static class Runner
    {
        private const string MainMenuScene = "Main Menu";
        private const string GameScene = "main";

        private static readonly List<TestResult> Results = new List<TestResult>();
        private static bool saveLoaded;

        public static IEnumerator Run()
        {
            float start = Time.realtimeSinceStartup;
            var bootstrap = new TestResult { Name = "bootstrap" };
            Results.Add(bootstrap);

            IEnumerator boot = Bootstrap(bootstrap);
            while (true)
            {
                bool more;
                try
                {
                    more = boot.MoveNext();
                }
                catch (Exception ex)
                {
                    bootstrap.Passed = false;
                    bootstrap.Failures.Add("Bootstrap threw: " + ex);
                    break;
                }
                if (!more)
                {
                    break;
                }
                yield return boot.Current;
            }
            AddLogFailures(bootstrap, 0);
            bootstrap.Seconds = Time.realtimeSinceStartup - start;

            if (bootstrap.Passed || saveLoaded)
            {
                LoadTestAssemblies(bootstrap);
                foreach (MethodInfo test in DiscoverTests(bootstrap))
                {
                    yield return RunTest(test);
                }
            }

            Time.timeScale = 1f;
            ResultsWriter.Write(Path.Combine(Plugin.Options.OutDir, "results.json"), Results, LogCapture.Snapshot());
            Plugin.Log.LogInfo("In-game tests finished; quitting.");
            Application.Quit();
        }

        private static IEnumerator Bootstrap(TestResult result)
        {
            if (Plugin.StartupErrors.Count > 0)
            {
                result.Passed = false;
                result.Failures.AddRange(Plugin.StartupErrors);
                result.Failures.Add("Not loading the save: save and achievement blocking may not be in place.");
                yield break;
            }

            LogCapture.Phase = "main-menu";
            yield return WaitFor(
                () => Mods_StopSpinner_Signal.ModsLoaded
                      && SceneManager.GetActiveScene().name == MainMenuScene
                      && UnityEngine.Object.FindObjectOfType<MainMenu_LoadGameManager>() != null
                      && Camera.main != null && Camera.main.GetComponent<mainScript>() != null,
                180f, "main menu with mods loaded", result);
            if (!result.Passed)
            {
                yield break;
            }

            if (Plugin.Options.Only.Count > 0)
            {
                foreach (string name in ModFilter.UnmatchedNames())
                {
                    result.Passed = false;
                    result.Failures.Add("--only " + name + ": no installed mod has that folder name, Workshop ID, title or HarmonyID");
                }
                if (!result.Passed)
                {
                    yield break;
                }
            }
            foreach (string rule in ModScope.UnmatchedRules())
            {
                result.Passed = false;
                result.Failures.Add("Scope rule " + rule + " matches no installed mod's HarmonyID, title or folder name");
            }
            if (!result.Passed)
            {
                yield break;
            }
            result.Data["scope"] = ModScope.RuleTexts.Any() ? string.Join("  ", ModScope.RuleTexts.ToArray()) : "every mod";
            result.Data["enabledMods"] = string.Join(", ",
                Mods._Mods.Where(m => m != null && m.IsEnabled()).Select(m => m.Title).Distinct().ToArray());

            // Let mod loaders finish logging and any startup popups settle.
            yield return new WaitForSecondsRealtime(2f);

            string fixture = Plugin.Options.SavePath;
            if (string.IsNullOrEmpty(fixture) || !File.Exists(fixture))
            {
                result.Passed = false;
                result.Failures.Add("Fixture save not found: " + fixture + " (pass -imtest-save <path>)");
                yield break;
            }

            // Load a copy: the game may rewrite the file it loaded from.
            string copy = Path.Combine(Plugin.Options.OutDir, "fixture_copy.json");
            File.Copy(fixture, copy, overwrite: true);

            LogCapture.Phase = "load";
            SaveManager.LoadEvent += OnLoad;
            Popup_Load_Story.Story_Mode = false;
            UnityEngine.Object.FindObjectOfType<MainMenu_LoadGameManager>().LoadGame(copy);

            yield return WaitFor(
                () => saveLoaded
                      && SceneManager.GetActiveScene().name == GameScene
                      && data_girls_textures.IsReady()
                      && Camera.main != null && Camera.main.GetComponent<mainScript>() != null,
                180f, "fixture save loaded into scene 'main'", result);
            SaveManager.LoadEvent -= OnLoad;

            // Post-load popups and tweens.
            yield return new WaitForSecondsRealtime(3f);
            result.Data["loadedDate"] = staticVars.dateTime.ToString("yyyy-MM-dd HH:mm");
            result.Data["mods"] = Plugin.Options.Vanilla ? "all disabled (--vanilla)"
                : Plugin.Options.Only.Count > 0 ? "only " + string.Join(", ", Plugin.Options.Only.ToArray()) + " (--only)"
                : "as configured in game";
        }

        private static void OnLoad() => saveLoaded = true;

        private static IEnumerator RunTest(MethodInfo method)
        {
            var result = new TestResult { Name = method.DeclaringType.Name + "." + method.Name };
            Results.Add(result);
            var ctx = new TestContext { Result = result };
            LogCapture.Phase = result.Name;
            int logStart = LogCapture.Count;
            int inputStart = InputBlock_Buttons.Suppressed;
            float start = Time.realtimeSinceStartup;

            IEnumerator body = null;
            try
            {
                body = (IEnumerator)method.Invoke(null, new object[] { ctx });
            }
            catch (Exception ex)
            {
                ctx.Fail("Test threw on start: " + (ex.InnerException ?? ex));
            }

            while (body != null)
            {
                bool more;
                try
                {
                    more = body.MoveNext();
                }
                catch (Exception ex)
                {
                    ctx.Fail("Test threw: " + ex);
                    break;
                }
                if (!more)
                {
                    break;
                }
                if (Time.realtimeSinceStartup - start > Plugin.Options.TimeoutSeconds)
                {
                    ctx.Fail("Test exceeded " + Plugin.Options.TimeoutSeconds + "s");
                    break;
                }
                yield return body.Current;
            }

            Time.timeScale = 1f;
            int blocked = InputBlock_Buttons.Suppressed - inputStart;
            if (blocked > 0)
            {
                result.Notes.Add("Blocked " + blocked + " keyboard/mouse input reads during this test");
            }
            AddLogFailures(result, logStart);
            result.Seconds = Time.realtimeSinceStartup - start;
        }

        private static void AddLogFailures(TestResult result, int fromIndex)
        {
            foreach (LogEntry e in LogCapture.Failures(fromIndex))
            {
                if (fromIndex == 0 || e.Phase == result.Name)
                {
                    result.Passed = false;
                    result.Failures.Add("[" + e.Source + " " + e.Level + "] " + e.Message
                        + (string.IsNullOrEmpty(e.StackTrace) ? "" : "\n" + e.StackTrace.TrimEnd()));
                }
            }
        }

        /// <summary>
        /// BepInEx only loads assemblies that contain a plugin, so test assemblies placed
        /// next to this one (InGameTests.*.dll) are loaded here. Failures are reported
        /// under bootstrap.
        /// </summary>
        private static void LoadTestAssemblies(TestResult result)
        {
            string dir = Path.GetDirectoryName(typeof(Runner).Assembly.Location);
            foreach (string path in Directory.GetFiles(dir, "InGameTests.*.dll"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == name))
                {
                    continue;
                }
                try
                {
                    Assembly.LoadFrom(path);
                }
                catch (Exception ex)
                {
                    result.Passed = false;
                    result.Failures.Add("Could not load test assembly " + Path.GetFileName(path) + ": " + ex);
                }
            }
        }

        private static IEnumerable<MethodInfo> DiscoverTests(TestResult result)
        {
            string suite = Plugin.Options.Suite;
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name.StartsWith("InGameTests", StringComparison.Ordinal))
                .SelectMany(a => SafeTypes(a, result))
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Select(m => new { m, a = m.GetCustomAttributes(typeof(InGameTestAttribute), false).Cast<InGameTestAttribute>().FirstOrDefault() })
                .Where(x => x.a != null && (suite == "all" || x.a.Suite == suite))
                .OrderBy(x => x.a.Order)
                .Select(x => x.m)
                .ToList();
        }

        private static IEnumerable<Type> SafeTypes(Assembly a, TestResult result)
        {
            try
            {
                return a.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Tests in the types that failed to load would otherwise be skipped silently.
                result.Passed = false;
                foreach (string message in ex.LoaderExceptions.Where(e => e != null).Select(e => e.Message).Distinct())
                {
                    result.Failures.Add("Types in " + a.GetName().Name + " failed to load: " + message);
                }
                return ex.Types.Where(t => t != null);
            }
        }

        /// <summary>Waits in real time (independent of Time.timeScale); fails the result on timeout.</summary>
        internal static IEnumerator WaitFor(Func<bool> condition, float timeoutSeconds, string what, TestResult result)
        {
            float start = Time.realtimeSinceStartup;
            while (!SafeCheck(condition))
            {
                if (Time.realtimeSinceStartup - start > timeoutSeconds)
                {
                    result.Passed = false;
                    result.Failures.Add("Timed out after " + timeoutSeconds + "s waiting for " + what
                        + " (scene=" + SceneManager.GetActiveScene().name + ")");
                    yield break;
                }
                yield return null;
            }
        }

        private static bool SafeCheck(Func<bool> condition)
        {
            try
            {
                return condition();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
