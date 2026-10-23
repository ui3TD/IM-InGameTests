using System;
using InGameTests;
using Xunit;

namespace InGameTests.UnitTests
{
    public class SessionCommandTests
    {
        [Theory]
        [InlineData("step 3d", 3, 'd')]
        [InlineData("step 2w", 2, 'w')]
        [InlineData("step 6h", 6, 'h')]
        [InlineData("STEP 1m", 1, 'm')]
        [InlineData("step 1y", 1, 'y')]
        public void StepTakesASpan(string line, int amount, char unit)
        {
            SessionCommand cmd = SessionCommand.Parse(line);
            Assert.Equal("step", cmd.Verb);
            Assert.Equal(amount, cmd.Span.Value.Amount);
            Assert.Equal(unit, cmd.Span.Value.Unit);
            Assert.True(cmd.Runs);
        }

        [Fact]
        public void StepToTakesADateWithOrWithoutTime()
        {
            Assert.Equal(new DateTime(2024, 5, 1), SessionCommand.Parse("step to 2024-05-01").Date);
            Assert.Equal(new DateTime(2024, 5, 1, 9, 30, 0), SessionCommand.Parse("step to 2024-05-01 09:30").Date);
        }

        [Theory]
        [InlineData("step")]
        [InlineData("step 3")]
        [InlineData("step 3x")]
        [InlineData("step 0d")]
        [InlineData("step -1d")]
        [InlineData("step 3d 4d")]
        [InlineData("step to 2024-13-01")]
        [InlineData("step to tomorrow")]
        public void BadStepsAreRejected(string line)
        {
            Assert.Throws<FormatException>(() => SessionCommand.Parse(line));
        }

        [Fact]
        public void SpansAddCalendarUnits()
        {
            var start = new DateTime(2024, 1, 31, 8, 0, 0);
            Assert.Equal(start.AddHours(6), GameSpan.Parse("6h").AddTo(start));
            Assert.Equal(start.AddDays(14), GameSpan.Parse("2w").AddTo(start));
            Assert.Equal(new DateTime(2024, 2, 29, 8, 0, 0), GameSpan.Parse("1m").AddTo(start));
            Assert.Equal(start.AddYears(1), GameSpan.Parse("1y").AddTo(start));
        }

        [Fact]
        public void UntilDefaultsToOneCallWithinAYear()
        {
            SessionCommand cmd = SessionCommand.Parse("until mainScript.Time_SetState");
            Assert.Equal("mainScript.Time_SetState", cmd.Target);
            Assert.Equal(1, cmd.Count);
            Assert.Equal("1y", cmd.Within.ToString());
        }

        [Theory]
        [InlineData("until a.B 3", 3, "1y")]
        [InlineData("until a.B within 3m", 1, "3m")]
        [InlineData("until a.B 2 within 1d", 2, "1d")]
        [InlineData("until error within 2w", 1, "2w")]
        public void UntilTakesACountAndALimit(string line, int count, string within)
        {
            SessionCommand cmd = SessionCommand.Parse(line);
            Assert.Equal(count, cmd.Count);
            Assert.Equal(within, cmd.Within.ToString());
        }

        [Theory]
        [InlineData("until")]
        [InlineData("until a.B 0")]
        [InlineData("until a.B within")]
        [InlineData("until a.B within 3m extra")]
        [InlineData("until a.B 2 3")]
        [InlineData("until error 2")]
        public void BadUntilsAreRejected(string line)
        {
            Assert.Throws<FormatException>(() => SessionCommand.Parse(line));
        }

        [Fact]
        public void QuotesKeepSpacesInOneWord()
        {
            SessionCommand cmd = SessionCommand.Parse("run \"C:\\My Probes\\probe.dll\" Probes.Count");
            Assert.Equal("C:\\My Probes\\probe.dll", cmd.Target);
            Assert.Equal("Probes.Count", cmd.Second);
            Assert.Throws<FormatException>(() => SessionCommand.Parse("run \"C:\\x.dll Probes.Count"));
        }

        [Fact]
        public void DiffTakesTwoSnapsAndAnOptionalFilter()
        {
            SessionCommand cmd = SessionCommand.Parse("diff start now ^data_girls");
            Assert.Equal("start", cmd.Target);
            Assert.Equal("now", cmd.Second);
            Assert.Equal("^data_girls", cmd.Filter);
            Assert.Null(SessionCommand.Parse("diff a b").Filter);
        }

        [Theory]
        [InlineData("snap now")]
        [InlineData("restore now")]
        [InlineData("snap ../escape")]
        [InlineData("diff a b/c")]
        public void SnapNamesAreFileSafeAndNotNow(string line)
        {
            Assert.Throws<FormatException>(() => SessionCommand.Parse(line));
        }

        [Theory]
        [InlineData("get staticVars.dateTime", false)]
        [InlineData("watch a.B", false)]
        [InlineData("unwatch", false)]
        [InlineData("calls", false)]
        [InlineData("patches a.B", false)]
        [InlineData("state", false)]
        [InlineData("help", false)]
        [InlineData("quit", false)]
        [InlineData("snap a", true)]
        [InlineData("restore a", true)]
        [InlineData("diff a b", true)]
        [InlineData("run p.dll A.B", true)]
        [InlineData("until error", true)]
        [InlineData("buttons", false)]
        [InlineData("click #3", true)]
        [InlineData("key F5", true)]
        public void OnlyCommandsThatRunGameCodeAreLimited(string line, bool runs)
        {
            Assert.Equal(runs, SessionCommand.Parse(line).Runs);
        }

        [Fact]
        public void ClickTakesTheWholeLabel()
        {
            Assert.Equal("Free Play", SessionCommand.Parse("click Free Play").Target);
            Assert.Equal("#12", SessionCommand.Parse("click #12").Target);
            Assert.Equal("F5", SessionCommand.Parse("key F5").Target);
            Assert.Equal("Hire|Staff", SessionCommand.Parse("buttons Hire|Staff").Target);
            Assert.Null(SessionCommand.Parse("buttons").Target);
            Assert.False(SessionCommand.Parse("status").Runs);
        }

        [Fact]
        public void DragSplitsAtTheLastTo()
        {
            SessionCommand cmd = SessionCommand.Parse("drag Hatanaka Minami to Office(Clone)");
            Assert.Equal("Hatanaka Minami", cmd.Target);
            Assert.Equal("Office(Clone)", cmd.Second);
            Assert.Equal("Go to", SessionCommand.Parse("drag Go to to #3").Target);
            Assert.True(cmd.Runs);
        }

        [Theory]
        [InlineData("drag #1")]
        [InlineData("drag #1 to")]
        [InlineData("drag to #2")]
        [InlineData("click")]
        [InlineData("key")]
        [InlineData("key F5 F9")]
        [InlineData("buttons a b")]
        [InlineData("buttons [unclosed")]
        [InlineData("status now")]
        public void BadInputCommandsAreRejected(string line)
        {
            Assert.Throws<FormatException>(() => SessionCommand.Parse(line));
        }

        [Fact]
        public void CallsDefaultsToTwenty()
        {
            Assert.Equal(20, SessionCommand.Parse("calls").Count);
            Assert.Equal(5, SessionCommand.Parse("calls 5").Count);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("jump 3d")]
        [InlineData("get")]
        [InlineData("get a b")]
        [InlineData("state now")]
        public void UnknownOrMalformedCommandsAreRejected(string line)
        {
            Assert.Throws<FormatException>(() => SessionCommand.Parse(line));
        }
    }
}
