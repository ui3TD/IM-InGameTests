using System;

namespace InGameTests
{
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
}
