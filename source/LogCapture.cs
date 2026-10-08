using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace InGameTests
{
    internal sealed class LogEntry
    {
        public string Source;
        public string Level;
        public string Message;
        public string StackTrace;
        public float Time;
        public string Phase;
    }

    /// <summary>
    /// Records errors from Unity (Debug.LogError, uncaught exceptions) and from
    /// BepInEx log sources (mod loaders, HarmonyX). Unity messages are
    /// taken from Unity directly so stack traces survive; BepInEx's forwarded
    /// copy of them is ignored.
    /// </summary>
    internal static class LogCapture
    {
        private const string UnityForwardedSource = "Unity Log";

        private static readonly object Gate = new object();
        private static readonly List<LogEntry> Entries = new List<LogEntry>();

        /// <summary>Label stored on each entry so results say when an error happened.</summary>
        public static string Phase = "startup";

        /// <summary>
        /// Errors the unmodded game logs on its own, so they are not mod failures. Errors
        /// from your own setup (e.g. other installed mods) belong in ignore.local.txt.
        /// Keep each pattern narrow. Patterns match "message\nstack trace".
        /// </summary>
        public static readonly List<Regex> Ignored = new List<Regex>
        {
            // SaveManager.IsFileSmallEnough logs this for every story slot that has no autosave.
            new Regex(@"auto_save\.json DOESN'T EXIST$", RegexOptions.Multiline),
            // Vanilla language loader reports a normal condition with Debug.LogError.
            new Regex(@"^LANGUAGE: CONFIG FILE EXISTS"),
        };

        /// <summary>
        /// Adds one regex per line from the run's ignore.txt (staged by the host script
        /// from ignore.txt and ignore.local.txt). Blank lines and # comments are skipped.
        /// </summary>
        public static void LoadIgnoreFile(string path)
        {
            if (!System.IO.File.Exists(path))
            {
                return;
            }
            foreach (string raw in System.IO.File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                Ignored.Add(new Regex(line));
            }
        }

        public static void Start()
        {
            Application.logMessageReceivedThreaded += OnUnityLog;
            BepInEx.Logging.Logger.Listeners.Add(new Listener());
        }

        public static List<LogEntry> Snapshot()
        {
            lock (Gate)
            {
                return new List<LogEntry>(Entries);
            }
        }

        /// <summary>Errors since <paramref name="fromIndex"/> that are not on the ignore list.</summary>
        public static List<LogEntry> Failures(int fromIndex = 0)
        {
            var result = new List<LogEntry>();
            lock (Gate)
            {
                for (int i = fromIndex; i < Entries.Count; i++)
                {
                    LogEntry e = Entries[i];
                    if (e.Level == "Warning" || IsIgnored(e.Message + "\n" + e.StackTrace))
                    {
                        continue;
                    }
                    result.Add(e);
                }
            }
            return result;
        }

        public static int Count
        {
            get { lock (Gate) { return Entries.Count; } }
        }

        private static bool IsIgnored(string text)
        {
            foreach (Regex r in Ignored)
            {
                if (r.IsMatch(text))
                {
                    return true;
                }
            }
            return false;
        }

        private static void OnUnityLog(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Log || type == LogType.Warning)
            {
                return;
            }
            Add(new LogEntry
            {
                Source = "Unity",
                Level = type.ToString(),
                Message = message,
                StackTrace = stackTrace,
            });
        }

        private static void Add(LogEntry entry)
        {
            entry.Phase = Phase;
            try
            {
                entry.Time = UnityEngine.Time.realtimeSinceStartup;
            }
            catch (Exception)
            {
                // realtimeSinceStartup is main-thread only; off-thread entries keep 0.
            }
            lock (Gate)
            {
                Entries.Add(entry);
            }
        }

        private sealed class Listener : ILogListener
        {
            public void LogEvent(object sender, LogEventArgs e)
            {
                if (e.Source.SourceName == UnityForwardedSource)
                {
                    return;
                }
                string level;
                if ((e.Level & (LogLevel.Fatal | LogLevel.Error)) != 0)
                {
                    level = "Error";
                }
                else if ((e.Level & LogLevel.Warning) != 0)
                {
                    level = "Warning";
                }
                else
                {
                    return;
                }
                Add(new LogEntry
                {
                    Source = e.Source.SourceName,
                    Level = level,
                    Message = Convert.ToString(e.Data),
                });
            }

            public void Dispose()
            {
            }
        }
    }
}
