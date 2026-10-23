using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace InGameTests
{
    /// <summary>How a control is named in a probe session's 'buttons' list: plain text a person reads on it, or else its object's name.</summary>
    internal static class LabelText
    {
        private const int MaxLabel = 40;

        /// <summary>Object names that say what a control is, not which one; the label climbs to a parent's name instead.</summary>
        private static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Icon", "Container", "Bar", "Button", "Text", "Image", "Background", "BG", "Content", "Panel",
        };

        /// <summary>
        /// The text without markup, icon-font glyphs (the Unicode private use area, either as characters
        /// or written out as \uXXXX) and runs of spaces, cut to 40 characters. Empty if nothing readable is left.
        /// </summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            text = Regex.Replace(text, "<[^>]*>", "");
            text = Regex.Replace(text, @"\\u[0-9a-fA-F]{4}", "");
            text = Regex.Replace(text, "[-]", "");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length > MaxLabel ? text.Substring(0, MaxLabel) + "..." : text;
        }

        /// <summary>
        /// A name for a control with no readable text: its own object name, or, when that's a generic
        /// one such as Icon, the first non-generic name among its parents (nearest first).
        /// </summary>
        public static string FromNames(IList<string> selfThenParents)
        {
            if (selfThenParents.Count == 0)
            {
                return "?";
            }
            string own = selfThenParents[0];
            if (!Generic.Contains(own))
            {
                return own;
            }
            string parent = selfThenParents.Skip(1).FirstOrDefault(n => !Generic.Contains(n));
            return parent ?? own;
        }

        /// <summary>A text field as 'Name: "value"', or 'Name: (empty)'.</summary>
        public static string Field(string name, string value) =>
            name + ": " + (string.IsNullOrEmpty(value) ? "(empty)" : "\"" + Clean(value) + "\"");
    }
}
