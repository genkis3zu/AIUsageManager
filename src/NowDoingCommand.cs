using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AIUsageManager
{
    internal enum NowDoingButtonKind { Text, Record, Copy }

    // One button of the いまやってること window, from settings.ini (now_doing_button1 … now_doing_button9).
    // Written as `<名前>|<動き>:<中身>`: text:<文> puts the text in the box, record:<文> writes it like Enter, and
    // copy:<命令>[|open:<リンク>] runs the command, puts its output in the clipboard and may open one of the fixed app links.
    internal sealed class NowDoingButton
    {
        public const int Count = 9, LabelLength = 8;
        public int Number;
        public string Label;
        public NowDoingButtonKind Kind;
        // The text for text and record; the command for copy.
        public string Value;
        // For copy with |open:, the app whose fixed link is opened afterwards ("claude", "codex", "vscode", "grokbot").
        public string App;

        // The first eight characters of the name; an emoji with its variation selector counts as one.
        public string ShortLabel
        {
            get
            {
                var result = new StringBuilder();
                TextElementEnumerator e = StringInfo.GetTextElementEnumerator(Label ?? "");
                for (int n = 0; n < LabelLength && e.MoveNext(); n++) result.Append(e.GetTextElement());
                return result.ToString();
            }
        }

        // Null when the value is not written like that.
        public static NowDoingButton Parse(int number, string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            int bar = value.IndexOf('|');
            if (bar < 0) return null;
            string label = value.Substring(0, bar).Trim(), rest = value.Substring(bar + 1);
            if (label.Length == 0) return null;
            var button = new NowDoingButton { Number = number, Label = label };
            if (rest.StartsWith("text:", StringComparison.Ordinal))
            {
                button.Kind = NowDoingButtonKind.Text;
                button.Value = rest.Substring(5);
                return button.Value.Length == 0 ? null : button;
            }
            if (rest.StartsWith("record:", StringComparison.Ordinal))
            {
                button.Kind = NowDoingButtonKind.Record;
                button.Value = rest.Substring(7).Trim();
                return button.Value.Length == 0 ? null : button;
            }
            if (!rest.StartsWith("copy:", StringComparison.Ordinal)) return null;
            button.Kind = NowDoingButtonKind.Copy;
            string command = rest.Substring(5);
            int open = command.LastIndexOf("|open:", StringComparison.Ordinal);
            if (open >= 0)
            {
                string link = command.Substring(open + 6).Trim();
                button.App = new[] { "claude", "codex", "vscode", "grokbot" }.FirstOrDefault(app => AppLinks.For(app) == link);
                if (button.App == null) return null;
                command = command.Substring(0, open);
            }
            button.Value = command.Trim();
            return NowDoingCommand.Split(button.Value) == null ? null : button;
        }

        // What the window says about buttons written wrongly; null when there are none.
        public static string Problem(IList<int> bad)
        {
            if (bad == null || bad.Count == 0) return null;
            return "settings.ini の " + string.Join("・", bad.Select(n => "now_doing_button" + n.ToString(CultureInfo.InvariantCulture))) + " の書き方が違います";
        }
    }

    // A command from settings.ini, run without a shell: the first word is the program, the rest are its arguments.
    internal static class NowDoingCommand
    {
        // Spaces and tabs outside double quotes separate the words; the quotes themselves are dropped and a backslash
        // is an ordinary character (paths). Null for an empty command or a quote that is not closed.
        public static string[] Split(string command)
        {
            if (command == null) return null;
            var words = new List<string>();
            var word = new StringBuilder();
            bool quoted = false, started = false;
            foreach (char c in command)
            {
                if (c == '"') { quoted = !quoted; started = true; continue; }
                if (!quoted && (c == ' ' || c == '\t'))
                {
                    if (started) { words.Add(word.ToString()); word.Clear(); started = false; }
                    continue;
                }
                word.Append(c); started = true;
            }
            if (quoted) return null;
            if (started) words.Add(word.ToString());
            return words.Count == 0 ? null : words.ToArray();
        }

        // One argument written so that Windows (CommandLineToArgvW and the C runtime) gives the program exactly this text.
        public static string Quote(string argument)
        {
            argument = argument ?? "";
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '\v', '"' }) < 0) return argument;
            var result = new StringBuilder("\"");
            for (int i = 0; ; i++)
            {
                int backslashes = 0;
                while (i < argument.Length && argument[i] == '\\') { backslashes++; i++; }
                if (i == argument.Length) { result.Append('\\', backslashes * 2); break; }
                if (argument[i] == '"') result.Append('\\', backslashes * 2 + 1).Append('"');
                else result.Append('\\', backslashes).Append(argument[i]);
            }
            return result.Append('"').ToString();
        }

        public static string Join(IEnumerable<string> arguments)
        {
            return string.Join(" ", (arguments ?? new string[0]).Select(Quote));
        }
    }
}
