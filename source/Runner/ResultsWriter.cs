using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace InGameTests
{
    /// <summary>Minimal JSON writer, so the plugin has no dependency beyond BepInEx and the game.</summary>
    internal static class ResultsWriter
    {
        public static void Write(string path, List<TestResult> results, List<LogEntry> log)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"passed\": ").Append(results.All(r => r.Passed) ? "true" : "false").Append(",\n");
            sb.Append("  \"suite\": ").Append(Str(Plugin.Options.Suite)).Append(",\n");
            sb.Append("  \"tests\": [\n");
            for (int i = 0; i < results.Count; i++)
            {
                TestResult r = results[i];
                sb.Append("    {\n");
                sb.Append("      \"name\": ").Append(Str(r.Name)).Append(",\n");
                sb.Append("      \"suite\": ").Append(Str(r.Suite)).Append(",\n");
                sb.Append("      \"mod\": ").Append(Str(r.Mod)).Append(",\n");
                sb.Append("      \"passed\": ").Append(r.Passed ? "true" : "false").Append(",\n");
                sb.Append("      \"seconds\": ").Append(r.Seconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"failures\": ").Append(Arr(r.Failures)).Append(",\n");
                sb.Append("      \"notes\": ").Append(Arr(r.Notes)).Append(",\n");
                sb.Append("      \"data\": {");
                sb.Append(string.Join(", ", r.Data.Select(kv => Str(kv.Key) + ": " + Str(kv.Value)).ToArray()));
                sb.Append("}\n");
                sb.Append(i + 1 < results.Count ? "    },\n" : "    }\n");
            }
            sb.Append("  ],\n");
            sb.Append("  \"log\": [\n");
            for (int i = 0; i < log.Count; i++)
            {
                LogEntry e = log[i];
                sb.Append("    {\"phase\": ").Append(Str(e.Phase))
                  .Append(", \"source\": ").Append(Str(e.Source))
                  .Append(", \"level\": ").Append(Str(e.Level))
                  .Append(", \"message\": ").Append(Str(e.Message))
                  .Append(", \"stack\": ").Append(Str(e.StackTrace))
                  .Append(i + 1 < log.Count ? "},\n" : "}\n");
            }
            sb.Append("  ]\n}\n");

            // Write then rename, so the host never reads a half-written file.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tmp, path);
        }

        private static string Arr(List<string> items)
        {
            return "[" + string.Join(", ", items.Select(Str).ToArray()) + "]";
        }

        private static string Str(string s)
        {
            if (s == null)
            {
                return "null";
            }
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
