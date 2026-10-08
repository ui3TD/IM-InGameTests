using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.IO;

namespace InGameTests
{
    /// <summary>
    /// Dev-only in-game test runner. Inert unless the game is launched with
    /// <c>-imtest &lt;suite&gt;</c>; then it blocks save/achievement writes, captures
    /// every error, loads a fixture save, runs the [InGameTest] methods, writes
    /// results.json and quits.
    /// </summary>
    [BepInPlugin(PluginGuid, "InGameTests", PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal const string PluginGuid = "im.ingametests";

        internal static Plugin Instance;
        internal static ManualLogSource Log;
        internal static TestOptions Options;

        private void Awake()
        {
            Options = TestOptions.Parse(Environment.GetCommandLineArgs());
            if (Options == null)
            {
                return;
            }

            Instance = this;
            Log = Logger;
            Directory.CreateDirectory(Options.OutDir);
            LogCapture.Start();
            LogCapture.LoadIgnoreFile(Path.Combine(Options.OutDir, "ignore.txt"));
            UnityEngine.Application.runInBackground = true;
            new Harmony(PluginGuid).PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo("In-game test mode: suite=" + Options.Suite + " out=" + Options.OutDir);
            StartCoroutine(Runner.Run());
        }
    }

    internal sealed class TestOptions
    {
        public string Suite;
        public string RunId;
        public string OutDir;
        public string SavePath;
        public int Weeks = 4;
        public float TimeScale = 20f;
        public float TimeoutSeconds = 600f;

        /// <summary>Returns null when the game was not launched in test mode.</summary>
        public static TestOptions Parse(string[] args)
        {
            TestOptions options = null;
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "-imtest":
                        options = options ?? new TestOptions();
                        options.Suite = next ?? "all";
                        break;
                    case "-imtest-run":
                        Ensure(ref options).RunId = next;
                        break;
                    case "-imtest-out":
                        Ensure(ref options).OutDir = next;
                        break;
                    case "-imtest-save":
                        Ensure(ref options).SavePath = next;
                        break;
                    case "-imtest-weeks":
                        Ensure(ref options).Weeks = int.Parse(next);
                        break;
                    case "-imtest-timescale":
                        Ensure(ref options).TimeScale = float.Parse(next, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                }
            }

            if (options == null || options.Suite == null)
            {
                return null;
            }

            // The host stages <persistentDataPath>/InGameTests/<run>/fixture.json and reads
            // results.json from the same folder, so no paths with spaces go on the command line.
            if (string.IsNullOrEmpty(options.OutDir))
            {
                options.OutDir = Path.Combine(
                    Path.Combine(UnityEngine.Application.persistentDataPath, "InGameTests"),
                    options.RunId ?? DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            }
            if (string.IsNullOrEmpty(options.SavePath))
            {
                options.SavePath = Path.Combine(options.OutDir, "fixture.json");
            }
            return options;
        }

        private static TestOptions Ensure(ref TestOptions options)
        {
            return options = options ?? new TestOptions();
        }
    }
}
