using System;
using System.IO;

namespace InGameTests
{
    internal sealed class TestOptions
    {
        public string Suite;
        public string RunId;
        public string OutDir;
        public string SavePath;
        public int Weeks = 4;
        public float TimeScale = 20f;
        public float TimeoutSeconds = 600f;
        public bool LoadVanilla;
        public bool AllowInput;
        public readonly System.Collections.Generic.List<string> Load = new System.Collections.Generic.List<string>();
        public bool Sound;

        /// <summary>A probe session: wait for commands instead of running tests.</summary>
        public bool Session;
        public bool NoSave;
        public float IdleMinutes = 30f;
        public float MaxMinutes = 120f;
        public float CommandMinutes = 10f;

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
                    case "-imtest-session":
                        Ensure(ref options).Session = true;
                        break;
                    case "-imtest-no-save":
                        Ensure(ref options).NoSave = true;
                        break;
                    case "-imtest-idle":
                        Ensure(ref options).IdleMinutes = Minutes(next);
                        break;
                    case "-imtest-max":
                        Ensure(ref options).MaxMinutes = Minutes(next);
                        break;
                    case "-imtest-command-limit":
                        Ensure(ref options).CommandMinutes = Minutes(next);
                        break;
                    case "-imtest-load":
                        Ensure(ref options).Load.Add(next);
                        break;
                    case "-imtest-allow-input":
                        Ensure(ref options).AllowInput = true;
                        break;
                    case "-imtest-sound":
                        Ensure(ref options).Sound = true;
                        break;
                    case "-imtest-load-vanilla":
                        Ensure(ref options).LoadVanilla = true;
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

            if (options == null || (options.Suite == null && !options.Session))
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

        private static float Minutes(string text) => float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        private static TestOptions Ensure(ref TestOptions options)
        {
            return options = options ?? new TestOptions();
        }
    }
}
