using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.IO;

namespace InGameTests
{
    /// <summary>
    /// Dev-only in-game test runner. Inert unless the game is launched with
    /// <c>-imtest &lt;suite&gt;</c>; then it moves saves into the run's folder, blocks
    /// settings and achievement writes, captures every error, loads a fixture save,
    /// runs the [InGameTest] methods, writes results.json and quits. With <c>-imtest-session</c>
    /// it does the same setup and then waits for probe session commands instead (see Session).
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
            // Tells the host script the runner is alive (it gives up early without this).
            File.WriteAllText(Path.Combine(Options.OutDir, "started.txt"), DateTime.Now.ToString("o"));
            LogCapture.Start();
            Logger.LogInfo("In-game test mode: " + (Options.Session ? "probe session" : "suite=" + Options.Suite) + (Options.LoadVanilla ? " (load vanilla)" : "")
                + (Options.Load.Count > 0 ? " (load: " + string.Join(", ", Options.Load.ToArray()) + ")" : "") + " out=" + Options.OutDir);

            // Every step is guarded: the game ships a stripped Unity, so an API that compiles
            // can still be missing at runtime. A failure is reported in results.json, and the
            // runner then refuses to load the save, since the safety patches may be missing.
            Try("ignore list", () => LogCapture.LoadIgnoreFile(Path.Combine(Options.OutDir, "ignore.txt")));
            Try("scope list", () => ModScope.Load(Path.Combine(Options.OutDir, "scope.txt")));
            Try("run in background", () => UnityEngine.Application.runInBackground = true);

            var harmony = new Harmony(PluginGuid);
            foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0)
                {
                    Try("patch " + type.Name, () => harmony.CreateClassProcessor(type).Patch());
                }
            }
            if (Options.LoadVanilla || Options.Load.Count > 0)
            {
                // Mod selection for this session only; settings writes are blocked.
                Try("mod filter", () => harmony.Patch(
                    AccessTools.Method(typeof(staticVars._settings), nameof(staticVars._settings.IsModEnabled)),
                    prefix: new HarmonyMethod(typeof(ModFilter), nameof(ModFilter.Prefix))));
            }

            StartCoroutine(Options.Session ? Session.Run() : Runner.Run());
        }

        /// <summary>Startup steps that failed; non-empty means the run must not load a save.</summary>
        internal static readonly System.Collections.Generic.List<string> StartupErrors =
            new System.Collections.Generic.List<string>();

        private void Try(string step, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                StartupErrors.Add("Startup step '" + step + "' failed: " + ex);
                Logger.LogError("Startup step '" + step + "' failed: " + ex.Message);
            }
        }
    }
}
