using SimpleJSON;
using System.Collections.Generic;
using System.Linq;

namespace InGameTests
{
    /// <summary>Every value that differs between two save files, objects by key and lists by index.</summary>
    internal static class SaveDiff
    {
        internal struct Difference
        {
            public string Path;
            public string Before;
            public string After;

            public override string ToString() => Path + ": " + Short(Before) + " -> " + Short(After);

            private static string Short(string value) => value.Length > 80 ? value.Substring(0, 80) + "..." : value;
        }

        public static List<Difference> Compare(JSONNode before, JSONNode after)
        {
            var differences = new List<Difference>();
            Compare(before, after, "", differences);
            return differences;
        }

        private static void Compare(JSONNode a, JSONNode b, string path, List<Difference> differences)
        {
            if (a is JSONClass objA && b is JSONClass objB)
            {
                var keysA = new HashSet<string>(Keys(objA));
                var keysB = new HashSet<string>(Keys(objB));
                foreach (string key in keysA)
                {
                    Compare(objA[key], keysB.Contains(key) ? objB[key] : null, Join(path, key), differences);
                }
                foreach (string key in keysB.Where(k => !keysA.Contains(k)))
                {
                    Compare(null, objB[key], Join(path, key), differences);
                }
            }
            else if (a is JSONArray listA && b is JSONArray listB)
            {
                if (listA.Count != listB.Count)
                {
                    differences.Add(new Difference { Path = path, Before = listA.Count + " items", After = listB.Count + " items" });
                }
                for (int i = 0; i < System.Math.Min(listA.Count, listB.Count); i++)
                {
                    Compare(listA[i], listB[i], path + "[" + i + "]", differences);
                }
            }
            else
            {
                string textA = Describe(a);
                string textB = Describe(b);
                if (textA != textB)
                {
                    differences.Add(new Difference { Path = path, Before = textA, After = textB });
                }
            }
        }

        private static IEnumerable<string> Keys(JSONClass node)
        {
            foreach (KeyValuePair<string, JSONNode> pair in node)
            {
                yield return pair.Key;
            }
        }

        private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

        private static string Describe(JSONNode node)
        {
            if (ReferenceEquals(node, null))
            {
                return "(missing)";
            }
            if (node is JSONArray)
            {
                return "(list of " + node.Count + ")";
            }
            if (node is JSONClass)
            {
                return "(object)";
            }
            return node.Value;
        }
    }
}
