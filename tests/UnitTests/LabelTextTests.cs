using InGameTests;
using Xunit;

namespace InGameTests.UnitTests
{
    public class LabelTextTests
    {
        [Theory]
        [InlineData("Free Play", "Free Play")]
        [InlineData("<b>Free</b>  <color=#fff>Play</color>", "Free Play")]
        [InlineData(" Details", "Details")]
        [InlineData("\\uf410", "")]
        [InlineData("\\uf329 View", "View")]
        [InlineData("  line\nbreak  ", "line break")]
        [InlineData(null, "")]
        public void CleanKeepsOnlyReadableText(string raw, string expected)
        {
            Assert.Equal(expected, LabelText.Clean(raw));
        }

        [Fact]
        public void CleanCutsLongText()
        {
            string label = LabelText.Clean(new string('a', 100));
            Assert.Equal(43, label.Length);
            Assert.EndsWith("...", label);
        }

        [Theory]
        [InlineData("Office(Clone)", "Office(Clone)")]
        [InlineData("Icon/Girls/Container", "Girls")]
        [InlineData("Container/Card Closed(Clone)/Cards", "Card Closed(Clone)")]
        [InlineData("Bar/Container/New Param(Clone)", "New Param(Clone)")]
        [InlineData("Icon/Container/Panel", "Icon")]
        public void GenericNamesClimbToAParent(string selfThenParents, string expected)
        {
            Assert.Equal(expected, LabelText.FromNames(selfThenParents.Split('/')));
        }

        [Fact]
        public void TextFieldsShowTheirValue()
        {
            Assert.Equal("First Name: \"Daichi\"", LabelText.Field("First Name", "Daichi"));
            Assert.Equal("First Name: (empty)", LabelText.Field("First Name", ""));
        }
    }
}
