using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InGameTests
{
    /// <summary>
    /// A span of game time, as typed in a probe session: a whole number and a unit, h (hours),
    /// d (days), w (weeks), m (months) or y (years), such as 3d or 2w.
    /// </summary>
    internal struct GameSpan
    {
        public int Amount;
        public char Unit;

        public DateTime AddTo(DateTime date)
        {
            switch (Unit)
            {
                case 'h': return date.AddHours(Amount);
                case 'd': return date.AddDays(Amount);
                case 'w': return date.AddDays(7 * Amount);
                case 'm': return date.AddMonths(Amount);
                default: return date.AddYears(Amount);
            }
        }

        public override string ToString() => Amount.ToString(CultureInfo.InvariantCulture) + Unit;

        public static GameSpan Parse(string text)
        {
            if (text != null && text.Length >= 2 && "hdwmy".IndexOf(text[text.Length - 1]) >= 0
                && int.TryParse(text.Substring(0, text.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int amount)
                && amount > 0)
            {
                return new GameSpan { Amount = amount, Unit = text[text.Length - 1] };
            }
            throw new FormatException("'" + text + "' isn't a game time span: use a whole number and h, d, w, m (months) or y, such as 3d");
        }
    }

    /// <summary>One command of a probe session, parsed from the line the launcher sent.</summary>
    internal sealed class SessionCommand
    {
        /// <summary>Until a game year has passed, unless the command says "within".</summary>
        public static readonly GameSpan DefaultWithin = new GameSpan { Amount = 1, Unit = 'y' };

        public const string Help =
            "step 3d | step 2w | step 6h | step to 2024-05-01 [09:00]   run the clock, then pause\n"
            + "until <Type.Method> [n] [within 3m]   run until the method has been called n times (default 1, within 1y)\n"
            + "until error [within 3m]               run until an error is logged\n"
            + "get <path>                            read a value, e.g. staticVars.dateTime, data_girls.girl[id=3], data_girls.girl[*].id\n"
            + "watch <Type.Method> | unwatch [<Type.Method>] | calls [n]   log calls; show the last n (default 20)\n"
            + "patches <Type.Method>                 list the Harmony patches on a method\n"
            + "snap <name> | restore <name> | diff <a> <b> [regex]   quicksave, quickload, compare saves ('now' = a fresh quicksave)\n"
            + "run <probe.dll> <Class.Method>        run a probe: static IEnumerator Method(TestContext ctx)\n"
            + "buttons [regex] | click #n | click <label> | key <key>   what a player could click now; click it; press a key (F5, Space, Escape, 1)\n"
            + "drag <from> to <to>                   press on one, move to the other, release (each #n or a label)\n"
            + "type <text>                           type into the text field that has focus, then Enter\n"
            + "status                                date, money, fans, fame, buzz, scandal points, idols: each with the game method it comes from\n"
            + "state | help | quit";

        public string Verb;

        /// <summary>The method, path, snap name or probe DLL the command names.</summary>
        public string Target;

        /// <summary>diff: the second snap; run: the probe's Class.Method.</summary>
        public string Second;

        /// <summary>diff: the path filter.</summary>
        public string Filter;

        /// <summary>until: calls to wait for; calls: lines to show.</summary>
        public int Count;

        /// <summary>step: how far.</summary>
        public GameSpan? Span;

        /// <summary>step to: the date.</summary>
        public DateTime? Date;

        /// <summary>until: the game time it may run at most.</summary>
        public GameSpan Within = DefaultWithin;

        /// <summary>Commands that run game code over frames; the session limits these in real time.</summary>
        public bool Runs => Verb == "step" || Verb == "until" || Verb == "snap" || Verb == "restore" || Verb == "diff" || Verb == "run"
                            || Verb == "click" || Verb == "drag" || Verb == "key" || Verb == "type";

        public static SessionCommand Parse(string line)
        {
            List<string> words = Split(line);
            if (words.Count == 0)
            {
                throw new FormatException("Empty command. Commands:\n" + Help);
            }
            var cmd = new SessionCommand { Verb = words[0].ToLowerInvariant() };
            int n = words.Count - 1;
            switch (cmd.Verb)
            {
                case "step":
                    if (n >= 2 && words[1] == "to")
                    {
                        cmd.Date = ParseDate(string.Join(" ", words.GetRange(2, n - 1).ToArray()));
                    }
                    else
                    {
                        Expect(cmd, n == 1, "step 3d, or step to 2024-05-01");
                        cmd.Span = GameSpan.Parse(words[1]);
                    }
                    break;
                case "until":
                    Expect(cmd, n >= 1, "until <Type.Method> [n] [within 3m], or until error [within 3m]");
                    cmd.Target = words[1];
                    cmd.Count = 1;
                    int i = 2;
                    if (i <= n && words[i] != "within")
                    {
                        Expect(cmd, cmd.Target != "error", "until error [within 3m]");
                        cmd.Count = PositiveInt(words[i]);
                        i++;
                    }
                    if (i <= n)
                    {
                        Expect(cmd, words[i] == "within" && i + 1 == n, "until <Type.Method> [n] [within 3m]");
                        cmd.Within = GameSpan.Parse(words[i + 1]);
                    }
                    break;
                case "get":
                case "watch":
                case "patches":
                case "snap":
                case "restore":
                    Expect(cmd, n == 1, cmd.Verb + " <" + (cmd.Verb == "get" ? "path" : cmd.Verb == "snap" || cmd.Verb == "restore" ? "name" : "Type.Method") + ">");
                    cmd.Target = words[1];
                    if (cmd.Verb == "snap" || cmd.Verb == "restore")
                    {
                        CheckSnapName(cmd.Target, allowNow: false);
                    }
                    break;
                case "unwatch":
                    Expect(cmd, n <= 1, "unwatch [<Type.Method>]");
                    cmd.Target = n == 1 ? words[1] : null;
                    break;
                case "calls":
                    Expect(cmd, n <= 1, "calls [n]");
                    cmd.Count = n == 1 ? PositiveInt(words[1]) : 20;
                    break;
                case "diff":
                    Expect(cmd, n == 2 || n == 3, "diff <a> <b> [regex]");
                    cmd.Target = CheckSnapName(words[1], allowNow: true);
                    cmd.Second = CheckSnapName(words[2], allowNow: true);
                    cmd.Filter = n == 3 ? words[3] : null;
                    break;
                case "run":
                    Expect(cmd, n == 2, "run <probe.dll> <Class.Method>");
                    cmd.Target = words[1];
                    cmd.Second = words[2];
                    break;
                case "click":
                    Expect(cmd, n >= 1, "click #n, or click <label>");
                    cmd.Target = string.Join(" ", words.GetRange(1, n).ToArray());
                    break;
                case "drag":
                {
                    int to = words.LastIndexOf("to");
                    Expect(cmd, to >= 2 && to < n, "drag <from> to <to>, each #n or a label");
                    cmd.Target = string.Join(" ", words.GetRange(1, to - 1).ToArray());
                    cmd.Second = string.Join(" ", words.GetRange(to + 1, n - to).ToArray());
                    break;
                }
                case "type":
                    Expect(cmd, n >= 1, "type <text>");
                    cmd.Target = string.Join(" ", words.GetRange(1, n).ToArray());
                    break;
                case "key":
                    Expect(cmd, n == 1, "key <key>, such as key F5");
                    cmd.Target = words[1];
                    break;
                case "buttons":
                    Expect(cmd, n <= 1, "buttons [regex]");
                    cmd.Target = n == 1 ? words[1] : null;
                    if (cmd.Target != null)
                    {
                        try
                        {
                            new System.Text.RegularExpressions.Regex(cmd.Target);
                        }
                        catch (ArgumentException ex)
                        {
                            throw new FormatException("'" + cmd.Target + "' isn't a valid regex: " + ex.Message);
                        }
                    }
                    break;
                case "status":
                case "state":
                case "help":
                case "quit":
                    Expect(cmd, n == 0, cmd.Verb);
                    break;
                default:
                    throw new FormatException("Unknown command '" + words[0] + "'. Commands:\n" + Help);
            }
            return cmd;
        }

        /// <summary>Words separated by spaces; double quotes keep spaces in one word.</summary>
        internal static List<string> Split(string line)
        {
            var words = new List<string>();
            var word = new StringBuilder();
            bool quoted = false;
            bool any = false;
            foreach (char c in line ?? "")
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    any = true;
                }
                else if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (any)
                    {
                        words.Add(word.ToString());
                        word.Length = 0;
                        any = false;
                    }
                }
                else
                {
                    word.Append(c);
                    any = true;
                }
            }
            if (quoted)
            {
                throw new FormatException("A double quote isn't closed");
            }
            if (any)
            {
                words.Add(word.ToString());
            }
            return words;
        }

        private static void Expect(SessionCommand cmd, bool ok, string usage)
        {
            if (!ok)
            {
                throw new FormatException("Usage: " + usage);
            }
        }

        private static int PositiveInt(string text)
        {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0)
            {
                return value;
            }
            throw new FormatException("'" + text + "' isn't a whole number above 0");
        }

        private static DateTime ParseDate(string text)
        {
            if (DateTime.TryParseExact(text, new[] { "yyyy-MM-dd", "yyyy-MM-dd HH:mm" }, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime date))
            {
                return date;
            }
            throw new FormatException("'" + text + "' isn't a date: use 2024-05-01 or 2024-05-01 09:00");
        }

        /// <summary>Snap names become file names: letters, digits, - and _ only. "now" means a fresh quicksave.</summary>
        private static string CheckSnapName(string name, bool allowNow)
        {
            foreach (char c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                {
                    throw new FormatException("Snap name '" + name + "': use letters, digits, - and _ only");
                }
            }
            if (!allowNow && name == "now")
            {
                throw new FormatException("'now' stands for a fresh quicksave in diff; pick another snap name");
            }
            return name;
        }
    }
}
