using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace InGameTests
{
    /// <summary>
    /// The probe session's patches on the methods it watches or waits for: one per method, under
    /// its own Harmony ID. The prefix runs first, so it sees the arguments as the caller passed
    /// them; the finalizer runs after every postfix, so it sees the final result, or the exception.
    /// They only record; they change no argument, result or exception.
    /// </summary>
    internal static class SessionHooks
    {
        private const string HarmonyId = "im.ingametests.session";
        private const int RecentKept = 1000;
        private const int UntilCallsKept = 20;

        private static readonly Harmony Harmony = new Harmony(HarmonyId);
        private static readonly object Gate = new object();
        private static readonly HashSet<MethodBase> Hooked = new HashSet<MethodBase>();
        private static readonly HashSet<MethodBase> Watched = new HashSet<MethodBase>();
        private static readonly Queue<string> Recent = new Queue<string>();
        private static readonly StringBuilder Pending = new StringBuilder();

        private static HashSet<MethodBase> untilMethods = new HashSet<MethodBase>();
        private static int untilWanted;
        private static int untilSeen;
        private static readonly List<string> UntilCalls = new List<string>();

        [ThreadStatic]
        private static bool recording;

        /// <summary>Watched calls so far in the session.</summary>
        public static int WatchedCalls { get; private set; }

        /// <summary>Whether the running until has seen its calls.</summary>
        public static bool UntilReached { get; private set; }

        public static int UntilSeen { get { lock (Gate) { return untilSeen; } } }

        public static IEnumerable<MethodBase> WatchedMethods { get { lock (Gate) { return Watched.ToList(); } } }

        public static void Watch(IEnumerable<MethodInfo> methods)
        {
            foreach (MethodInfo method in methods)
            {
                Hook(method);
                lock (Gate)
                {
                    Watched.Add(method);
                }
            }
        }

        /// <summary>Stops watching these methods, or every method when null. Returns how many were watched.</summary>
        public static int Unwatch(IEnumerable<MethodInfo> methods)
        {
            List<MethodBase> stop;
            lock (Gate)
            {
                stop = methods == null ? Watched.ToList() : methods.Cast<MethodBase>().Where(Watched.Contains).ToList();
                foreach (MethodBase method in stop)
                {
                    Watched.Remove(method);
                }
            }
            foreach (MethodBase method in stop)
            {
                UnhookIfUnused(method);
            }
            return stop.Count;
        }

        public static void StartUntil(IEnumerable<MethodInfo> methods, int calls)
        {
            var set = new HashSet<MethodBase>(methods.Cast<MethodBase>());
            foreach (MethodBase method in set)
            {
                Hook(method);
            }
            lock (Gate)
            {
                untilMethods = set;
                untilWanted = calls;
                untilSeen = 0;
                UntilCalls.Clear();
                UntilReached = false;
            }
        }

        /// <summary>Ends the until and returns the calls it saw (the first ones, up to 20).</summary>
        public static List<string> EndUntil()
        {
            List<MethodBase> methods;
            List<string> calls;
            lock (Gate)
            {
                methods = untilMethods.ToList();
                untilMethods = new HashSet<MethodBase>();
                calls = UntilCalls.ToList();
            }
            foreach (MethodBase method in methods)
            {
                UnhookIfUnused(method);
            }
            return calls;
        }

        /// <summary>The last n watched calls, oldest first.</summary>
        public static List<string> Last(int n)
        {
            lock (Gate)
            {
                return Recent.Skip(Math.Max(0, Recent.Count - n)).ToList();
            }
        }

        /// <summary>Appends the watched calls recorded since the last flush to the log file.</summary>
        public static void Flush(string path)
        {
            string text;
            lock (Gate)
            {
                if (Pending.Length == 0)
                {
                    return;
                }
                text = Pending.ToString();
                Pending.Length = 0;
            }
            File.AppendAllText(path, text);
        }

        public static string Describe(MethodBase method) =>
            ValueText.TypeName(method.DeclaringType) + "." + method.Name
            + "(" + string.Join(", ", method.GetParameters().Select(p => ValueText.TypeName(p.ParameterType))) + ")";

        private static void Hook(MethodBase method)
        {
            lock (Gate)
            {
                if (Hooked.Contains(method))
                {
                    return;
                }
            }
            bool returnsValue = method is MethodInfo info && info.ReturnType != typeof(void);
            Harmony.Patch(method,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(SessionHooks), nameof(Prefix))) { priority = Priority.First },
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(SessionHooks), returnsValue ? nameof(FinalizerWithResult) : nameof(Finalizer)))
                {
                    priority = Priority.Last,
                });
            lock (Gate)
            {
                Hooked.Add(method);
            }
        }

        private static void UnhookIfUnused(MethodBase method)
        {
            lock (Gate)
            {
                if (!Hooked.Contains(method) || Watched.Contains(method) || untilMethods.Contains(method))
                {
                    return;
                }
                Hooked.Remove(method);
            }
            Harmony.Unpatch(method, HarmonyPatchType.All, HarmonyId);
        }

        private static void Prefix(object[] __args, out string __state)
        {
            __state = null;
            if (recording)
            {
                return;
            }
            recording = true;
            try
            {
                __state = string.Join(", ", (__args ?? new object[0]).Select(ValueText.Short).ToArray()) + "\u0000" + Caller();
            }
            catch (Exception ex)
            {
                __state = "(arguments unreadable: " + ex.GetType().Name + ")";
            }
            finally
            {
                recording = false;
            }
        }

        /// <summary>
        /// The method that called the hooked one: the first frame above this prefix that isn't the
        /// patch's replacement method (Harmony's DMD&lt;...&gt; wrapper).
        /// </summary>
        private static string Caller()
        {
            foreach (System.Diagnostics.StackFrame frame in new System.Diagnostics.StackTrace(1, false).GetFrames() ?? new System.Diagnostics.StackFrame[0])
            {
                MethodBase method = frame.GetMethod();
                if (method == null || method.DeclaringType == null || method.DeclaringType == typeof(SessionHooks)
                    || method.Name.StartsWith("DMD<", StringComparison.Ordinal))
                {
                    continue;
                }
                return SourceName(method);
            }
            return "?";
        }

        /// <summary>
        /// The method as written in source: a coroutine's or a lambda's compiler-made code
        /// (Game.&lt;Advance&gt;d__2.MoveNext, Game.&lt;&gt;c.&lt;Find&gt;b__3_0) is named after the method it's in (Game.Advance).
        /// </summary>
        private static string SourceName(MethodBase method)
        {
            Type type = method.DeclaringType;
            string name = method.Name;
            while (type.DeclaringType != null && type.Name.StartsWith("<", StringComparison.Ordinal))
            {
                // MoveNext, or a helper with no source name such as Mono's <>m__Finally1: the class's name says the method.
                if (!name.StartsWith("<", StringComparison.Ordinal) || name.StartsWith("<>", StringComparison.Ordinal))
                {
                    name = type.Name;
                }
                type = type.DeclaringType;
            }
            if (name.StartsWith("<", StringComparison.Ordinal) && name.IndexOf('>') > 1)
            {
                name = name.Substring(1, name.IndexOf('>') - 1);
            }
            return ValueText.TypeName(type) + "." + name;
        }

        private static void Finalizer(MethodBase __originalMethod, string __state, Exception __exception) =>
            Record(__originalMethod, __state, __exception == null ? "" : " threw " + __exception.GetType().Name + ": " + __exception.Message);

        private static void FinalizerWithResult(MethodBase __originalMethod, string __state, Exception __exception, object __result) =>
            Record(__originalMethod, __state, __exception == null ? " -> " + ValueText.Short(__result)
                : " threw " + __exception.GetType().Name + ": " + __exception.Message);

        private static void Record(MethodBase method, string args, string outcome)
        {
            if (recording || args == null)
            {
                return;
            }
            recording = true;
            try
            {
                int split = args.IndexOf('\u0000');
                string caller = split < 0 ? "?" : args.Substring(split + 1);
                string line = ValueText.Short(staticVars.dateTime) + "  " + ValueText.TypeName(method.DeclaringType) + "." + method.Name
                              + "(" + (split < 0 ? args : args.Substring(0, split)) + ")" + outcome + "   from " + caller;
                bool reached = false;
                lock (Gate)
                {
                    if (Watched.Contains(method))
                    {
                        WatchedCalls++;
                        Recent.Enqueue(line);
                        if (Recent.Count > RecentKept)
                        {
                            Recent.Dequeue();
                        }
                        Pending.Append(line).Append('\n');
                    }
                    if (untilMethods.Contains(method) && !UntilReached)
                    {
                        untilSeen++;
                        if (UntilCalls.Count < UntilCallsKept)
                        {
                            UntilCalls.Add(line);
                        }
                        reached = UntilReached = untilSeen >= untilWanted;
                    }
                }
                if (reached)
                {
                    // Stop the clock at the end of this frame; the session's loop pauses it properly
                    // on the next one. Time is main-thread only, so a call on another thread leaves it.
                    try
                    {
                        UnityEngine.Time.timeScale = 0f;
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
                // Never let a recording problem reach the game's code.
            }
            finally
            {
                recording = false;
            }
        }
    }
}
