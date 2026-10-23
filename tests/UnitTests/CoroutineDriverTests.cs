using System;
using System.Collections;
using System.Collections.Generic;
using InGameTests;
using Xunit;

namespace InGameTests.UnitTests
{
    public class CoroutineDriverTests
    {
        private sealed class Frame
        {
        }

        private static readonly Frame WaitInstruction = new Frame();

        private static IEnumerator Inner(List<string> log, int frames)
        {
            try
            {
                for (int i = 0; i < frames; i++)
                {
                    log.Add("inner " + i);
                    yield return null;
                }
            }
            finally
            {
                log.Add("inner finally");
            }
        }

        private static IEnumerator Outer(List<string> log, int innerFrames)
        {
            try
            {
                log.Add("outer start");
                yield return Inner(log, innerFrames);
                log.Add("outer after inner");
                yield return WaitInstruction;
                log.Add("outer end");
            }
            finally
            {
                log.Add("outer finally");
            }
        }

        private static List<object> RunToEnd(CoroutineDriver driver)
        {
            var handed = new List<object>();
            while (driver.Step(out object instruction))
            {
                handed.Add(instruction);
            }
            return handed;
        }

        [Fact]
        public void NestedCoroutinesRunInsideTheDriverFrameByFrame()
        {
            var log = new List<string>();
            List<object> handed = RunToEnd(new CoroutineDriver(Outer(log, 2)));

            Assert.Equal(new object[] { null, null, WaitInstruction }, handed.ToArray());
            Assert.Equal(new[] { "outer start", "inner 0", "inner 1", "inner finally", "outer after inner", "outer end", "outer finally" }, log.ToArray());
        }

        [Fact]
        public void StopInsideANestedCoroutineRunsEveryFinallyInnermostFirst()
        {
            var log = new List<string>();
            var driver = new CoroutineDriver(Outer(log, 100));
            for (int i = 0; i < 3; i++)
            {
                Assert.True(driver.Step(out _));
            }

            Assert.Empty(driver.Stop());
            Assert.Equal(new[] { "outer start", "inner 0", "inner 1", "inner 2", "inner finally", "outer finally" }, log.ToArray());
            Assert.False(driver.Step(out _));
        }

        private static IEnumerator Throws()
        {
            yield return null;
            throw new InvalidOperationException("boom");
        }

        private static IEnumerator CallsThrows(List<string> log)
        {
            try
            {
                yield return Throws();
            }
            finally
            {
                log.Add("caller finally");
            }
        }

        [Fact]
        public void AnExceptionReachesTheCallerAndStopCleansUp()
        {
            var log = new List<string>();
            var driver = new CoroutineDriver(CallsThrows(log));
            Assert.True(driver.Step(out _));

            Assert.Throws<InvalidOperationException>(() => driver.Step(out _));
            driver.Stop();
            Assert.Equal(new[] { "caller finally" }, log.ToArray());
        }

        private static IEnumerator ThrowsInFinally()
        {
            try
            {
                yield return null;
            }
            finally
            {
                throw new InvalidOperationException("finally");
            }
        }

        [Fact]
        public void StopReportsWhatThrewAndStillStopsTheRest()
        {
            var log = new List<string>();
            IEnumerator Caller()
            {
                try
                {
                    yield return ThrowsInFinally();
                }
                finally
                {
                    log.Add("caller finally");
                }
            }
            var driver = new CoroutineDriver(Caller());
            Assert.True(driver.Step(out _));

            List<Exception> errors = driver.Stop();
            Assert.Single(errors);
            Assert.Equal(new[] { "caller finally" }, log.ToArray());
        }

        /// <summary>Like Unity's CustomYieldInstruction: an IEnumerator that waits while MoveNext returns true.</summary>
        private sealed class WaitFrames : IEnumerator
        {
            private int left;

            public WaitFrames(int frames) => left = frames;

            public object Current => null;

            public bool MoveNext() => left-- > 0;

            public void Reset()
            {
            }
        }

        private static IEnumerator WaitsThenEnds(List<string> log)
        {
            yield return new WaitFrames(3);
            log.Add("done");
        }

        [Fact]
        public void WaitInstructionsThatAreEnumeratorsWaitOneFrameAtATime()
        {
            var log = new List<string>();
            List<object> handed = RunToEnd(new CoroutineDriver(WaitsThenEnds(log)));
            Assert.Equal(3, handed.Count);
            Assert.Equal(new[] { "done" }, log.ToArray());
        }
    }
}
