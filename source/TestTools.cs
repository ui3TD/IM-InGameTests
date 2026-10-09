using HarmonyLib;
using System;
using System.Collections;
using System.Reflection;

namespace InGameTests
{
    /// <summary>
    /// Instruments for tests: they wait on, observe or wrap game code without changing what it
    /// computes, and take their target as a parameter. Game holds the game primitives.
    /// </summary>
    public static class TestTools
    {
        /// <summary>Waits in real time (independent of Time.timeScale) until the condition holds; fails the test on timeout.</summary>
        public static IEnumerator WaitFor(TestContext ctx, Func<bool> condition, float timeoutSeconds, string what)
            => Runner.WaitFor(condition, timeoutSeconds, what, ctx.Result);

        /// <summary>
        /// Applies a temporary patch that runs before every other patch of its kind, so a prefix
        /// sees the caller's arguments and a postfix sees the game's own result. Dispose removes it.
        /// </summary>
        public static IDisposable Spy(MethodBase original, MethodInfo prefix = null, MethodInfo postfix = null)
        {
            var harmony = new Harmony("im.ingametests.spy");
            harmony.Patch(original,
                prefix: prefix == null ? null : new HarmonyMethod(prefix) { priority = Priority.First },
                postfix: postfix == null ? null : new HarmonyMethod(postfix) { priority = Priority.First });
            return new Disposer(() => harmony.Unpatch(original, HarmonyPatchType.All, harmony.Id));
        }

        /// <summary>Runs the action on Dispose: <c>using (TestTools.Restore(() => x = before)) { ... }</c>.</summary>
        public static IDisposable Restore(Action restore) => new Disposer(restore);

        private sealed class Disposer : IDisposable
        {
            private Action action;

            public Disposer(Action action) => this.action = action;

            public void Dispose()
            {
                action?.Invoke();
                action = null;
            }
        }
    }
}
