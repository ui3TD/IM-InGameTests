using System;
using System.Collections;
using System.Collections.Generic;

namespace InGameTests
{
    /// <summary>
    /// Steps a coroutine and the coroutines it yields itself, frame by frame, instead of handing
    /// nested ones to Unity. Unity runs a yielded IEnumerator to its end before the outer one
    /// resumes, so the outer one could never stop it; this lets the caller check a limit every
    /// frame and stop the whole stack, running each level's finally blocks.
    /// </summary>
    internal sealed class CoroutineDriver
    {
        private readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();

        public CoroutineDriver(IEnumerator root)
        {
            stack.Push(root);
        }

        /// <summary>
        /// Runs until something has to be handed to Unity (null for one frame, a wait instruction,
        /// a Coroutine) and returns true with it, or returns false when the coroutine has finished.
        /// An exception from any level propagates; call <see cref="Stop"/> after one.
        /// </summary>
        public bool Step(out object yieldToUnity)
        {
            while (stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                if (!top.MoveNext())
                {
                    stack.Pop();
                    continue;
                }
                if (top.Current is IEnumerator nested)
                {
                    // Includes CustomYieldInstruction (WaitForSecondsRealtime), which waits by
                    // MoveNext returning true with a null Current.
                    stack.Push(nested);
                    continue;
                }
                yieldToUnity = top.Current;
                return true;
            }
            yieldToUnity = null;
            return false;
        }

        /// <summary>Disposes every level, innermost first, so their finally blocks run. Returns what threw on the way.</summary>
        public List<Exception> Stop()
        {
            var errors = new List<Exception>();
            while (stack.Count > 0)
            {
                try
                {
                    (stack.Pop() as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
            return errors;
        }
    }
}
