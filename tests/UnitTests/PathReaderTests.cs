using System;
using System.Collections.Generic;
using InGameTests;
using Xunit;

// Stand-ins for game types, which have no namespace and nest their classes.
internal static class FakeGirls
{
    public static List<Girl> girl = new List<Girl>
    {
        new Girl { id = 7, name = "Aoi", status = Status.normal },
        new Girl { id = 3, name = "Mei", status = Status.graduated },
        new Girl { id = 9, name = "Rin", status = Status.normal },
    };

    public static Dictionary<string, int> fansByType = new Dictionary<string, int> { { "casual", 10 }, { "hardcore", 4 } };

    public static DateTime date = new DateTime(2024, 3, 24, 9, 0, 0);

    public static int Count => girl.Count;

    public static Girl nobody = null;

    public enum Status { normal, graduated }

    public class Girl
    {
        public int id;
        public string name;
        public Status status;
        public List<int> scores = new List<int> { 1, 2 };
#pragma warning disable CS0414 // read by reflection
        private int secret = 42;
#pragma warning restore CS0414

        public int Twice => id * 2;

        public override string ToString() => "never used";
    }

    public static class Nested
    {
        public static string label = "inner";
    }
}

namespace InGameTests.UnitTests
{
    public class PathReaderTests
    {
        private static readonly TypeIndex Types = new TypeIndex(new[] { typeof(PathReaderTests).Assembly });

        private static string Read(string path) => PathReader.Read(Types, path);

        [Fact]
        public void ReadsStaticFieldsAndProperties()
        {
            Assert.Equal("2024-03-24 09:00", Read("FakeGirls.date"));
            Assert.Equal("3", Read("FakeGirls.Count"));
            Assert.Equal("null", Read("FakeGirls.nobody"));
        }

        [Fact]
        public void NestedTypesAreWrittenWithDots()
        {
            Assert.Equal("inner", Read("FakeGirls.Nested.label"));
        }

        [Fact]
        public void ListIndexThenMembers()
        {
            Assert.Equal("Mei", Read("FakeGirls.girl[1].name"));
            Assert.Equal("graduated", Read("FakeGirls.girl[1].status"));
            Assert.Equal("6", Read("FakeGirls.girl[1].Twice"));
            Assert.Equal("2", Read("FakeGirls.girl[0].scores[1]"));
        }

        [Fact]
        public void PrivateFieldsCanBeRead()
        {
            Assert.Equal("42", Read("FakeGirls.girl[0].secret"));
        }

        [Fact]
        public void SelectorPicksByMemberAndShowsTheOneMatchInFull()
        {
            string text = Read("FakeGirls.girl[id=3]");
            Assert.StartsWith("1 value: [1]\n(FakeGirls.Girl)", text);
            Assert.Contains("\nname = Mei", text);
            Assert.Contains("\nsecret = 42", text);
            Assert.DoesNotContain("never used", text);
        }

        [Fact]
        public void SelectorThenMemberGivesOneLinePerMatch()
        {
            Assert.Equal("2 values\n[0] = Aoi\n[2] = Rin", Read("FakeGirls.girl[status=normal].name"));
        }

        [Fact]
        public void StarMapsOverEveryItem()
        {
            Assert.Equal("3 values\n[0] = 7\n[1] = 3\n[2] = 9", Read("FakeGirls.girl[*].id"));
        }

        [Fact]
        public void DictionariesTakeKeysAndStar()
        {
            Assert.Equal("4", Read("FakeGirls.fansByType[hardcore]"));
            Assert.Equal("2 values\n[casual] = 10\n[hardcore] = 4", Read("FakeGirls.fansByType[*]"));
        }

        [Fact]
        public void ListsShowCountAndItems()
        {
            Assert.Equal("(2 items)\n[0] = 1\n[1] = 2", Read("FakeGirls.girl[0].scores"));
            Assert.Equal("(2 items)\n[casual] = 10\n[hardcore] = 4", Read("FakeGirls.fansByType"));
        }

        [Fact]
        public void ATypeAloneSaysToNameAMember()
        {
            Assert.Contains("a type", Read("FakeGirls"));
        }

        [Theory]
        [InlineData("NoSuchType.x", "No loaded type")]
        [InlineData("FakeGirls.nothing", "no static field or property nothing")]
        [InlineData("FakeGirls.girl[0].nothing", "has no field or property nothing")]
        [InlineData("FakeGirls.girl[5]", "outside the list's 3 items")]
        [InlineData("FakeGirls.girl[x]", "a list takes a number")]
        [InlineData("FakeGirls.fansByType[none]", "no key none")]
        [InlineData("FakeGirls.nobody.id", "can't read id of null")]
        [InlineData("FakeGirls.date[0]", "needs a list or dictionary")]
        [InlineData("FakeGirls.girl[0", "isn't closed")]
        [InlineData("FakeGirls..date", "empty name")]
        [InlineData("FakeGirls[0].date", "A type can't take")]
        public void MistakesGetAClearMessage(string path, string message)
        {
            var ex = Assert.Throws<ArgumentException>(() => Read(path));
            Assert.Contains(message, ex.Message);
        }

        [Fact]
        public void SelectorMistakesNameTheItem()
        {
            var ex = Assert.Throws<ArgumentException>(() => Read("FakeGirls.girl[*].nothing"));
            Assert.Contains("[0]: ", ex.Message);
        }

        [Fact]
        public void MethodsAreFoundByTypeAndName()
        {
            Assert.Single(Types.Methods("PathReaderTests.Read"));
            Assert.Throws<ArgumentException>(() => Types.Methods("PathReaderTests.NoSuchMethod"));
            Assert.Throws<ArgumentException>(() => Types.Methods("NoSuchType.Read"));
            Assert.Throws<ArgumentException>(() => Types.Methods("Read"));
        }

        [Fact]
        public void TypesMatchFullOrNamespaceFreeNames()
        {
            Assert.Equal(typeof(PathReaderTests), Types.Find("InGameTests.UnitTests.PathReaderTests"));
            Assert.Equal(typeof(PathReaderTests), Types.Find("PathReaderTests"));
            Assert.Null(Types.Find("pathreadertests"));
        }

        [Fact]
        public void AmbiguousNamesSayWhichTypes()
        {
            var ex = Assert.Throws<ArgumentException>(() => Types.Find("Twin"));
            Assert.Contains("InGameTests.UnitTests.A.Twin", ex.Message);
            Assert.Contains("InGameTests.UnitTests.B.Twin", ex.Message);
            Assert.Equal(typeof(A.Twin), Types.Find("InGameTests.UnitTests.A.Twin"));
        }

        [Fact]
        public void ShortTextNeverCallsToString()
        {
            Assert.Equal("null", ValueText.Short(null));
            Assert.Equal("1.5", ValueText.Short(1.5));
            Assert.Equal("True", ValueText.Short(true));
            Assert.Equal("graduated", ValueText.Short(FakeGirls.Status.graduated));
            Assert.Equal("(1 item)", ValueText.Short(new List<int> { 1 }));
            Assert.Equal("(FakeGirls.Girl)", ValueText.Short(new FakeGirls.Girl()));
            Assert.EndsWith("...", ValueText.Short(new string('x', 500)));
            Assert.Equal("List<FakeGirls.Girl>", ValueText.TypeName(typeof(List<FakeGirls.Girl>)));
        }
    }
}

namespace InGameTests.UnitTests.A
{
    public class Twin
    {
    }
}

namespace InGameTests.UnitTests.B
{
    public class Twin
    {
    }
}
