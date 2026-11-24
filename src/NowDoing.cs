using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AIUsageManager
{
    // settings.ini keys for the いまやってること window. The diary location is only ever read from settings.ini.
    internal sealed class NowDoingSettings
    {
        public const string DefaultHeading = "## 📝 日中ログ";
        public string Note;
        public string Heading = DefaultHeading;
        public string Before;
        // now_doing_claudian=on: Ctrl+Enter also hands the line to Claudian through the Obsidian plugin.
        public bool Claudian;
        // now_doing_hotkey=Ctrl+Alt+N: the tray app opens the window with this shortcut key.
        public string Hotkey;
        // now_doing_button1 … 9: the window's buttons, at Number-1; the numbers written wrongly are in BadButtons.
        public NowDoingButton[] Buttons = new NowDoingButton[NowDoingButton.Count];
        public List<int> BadButtons = new List<int>();
        // The user's programs: run after a line is written (now_doing_after), and when the window opens for the
        // まだのこと list (now_doing_list), in now_doing_workdir (the app's folder when not set).
        public string After, List, Workdir;
        // now_doing_ai_done=on: the tray app writes finished AI work to the 日中ログ, except work in these folders.
        public bool AiDone;
        public string[] AiDoneExclude = new string[0];
        // now_doing_ai_done_detail=on: each entry also says what was asked, what came back and the commits made (read from the local records).
        public bool AiDoneDetail;

        public static NowDoingSettings Parse(string ini)
        {
            var settings = new NowDoingSettings();
            foreach (string raw in (ini ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string key = line.Substring(0, equals).Trim(), value = line.Substring(equals + 1).Trim();
                int number = ButtonNumber(key);
                if (number > 0)
                {
                    // A text button may end with a space on purpose (text:⏱️ ), so only the front is trimmed.
                    string written = line.Substring(equals + 1).TrimStart();
                    settings.BadButtons.Remove(number);
                    settings.Buttons[number - 1] = null;
                    if (written.Length == 0) continue;
                    settings.Buttons[number - 1] = NowDoingButton.Parse(number, written);
                    if (settings.Buttons[number - 1] == null) { settings.BadButtons.Add(number); settings.BadButtons.Sort(); }
                    continue;
                }
                if (value.Length == 0) continue;
                if (key == "now_doing_note") settings.Note = value;
                else if (key == "now_doing_heading") settings.Heading = value;
                else if (key == "now_doing_before") settings.Before = value;
                else if (key == "now_doing_claudian") settings.Claudian = value == "on";
                else if (key == "now_doing_hotkey") settings.Hotkey = value;
                else if (key == "now_doing_after") settings.After = value;
                else if (key == "now_doing_list") settings.List = value;
                else if (key == "now_doing_workdir") settings.Workdir = value;
                else if (key == "now_doing_ai_done") settings.AiDone = value == "on";
                else if (key == "now_doing_ai_done_detail") settings.AiDoneDetail = value == "on";
                else if (key == "now_doing_ai_done_exclude")
                    settings.AiDoneExclude = value.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            }
            return settings;
        }

        // 1 … 9 for now_doing_button1 … now_doing_button9, otherwise 0.
        static int ButtonNumber(string key)
        {
            const string prefix = "now_doing_button";
            if (key.Length != prefix.Length + 1 || !key.StartsWith(prefix, StringComparison.Ordinal)) return 0;
            char digit = key[prefix.Length];
            return digit >= '1' && digit <= '9' ? digit - '0' : 0;
        }
        public static NowDoingSettings Load(string path)
        {
            try { return Parse(File.ReadAllText(path)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            return Parse("");
        }
    }

    internal enum NowDoingFailure { None, NoSetting, BadSetting, NoNote, NoHeading, BadEncoding, Busy, NoBackup, WriteFailed }

    // One 📝 record of the 日中ログ: the start time (HH:MM) and the text after 📝.
    internal sealed class NowDoingRecord
    {
        public string Time, Text;
    }

    // What the window shows when it opens: the diary file it writes to, whether that file can take a line now,
    // and the 📝 records already in its 日中ログ (in file order).
    internal sealed class NowDoingView
    {
        public string Path, Heading;
        public NowDoingFailure Failure;
        public List<NowDoingRecord> Records = new List<NowDoingRecord>();
    }

    // Adds a two-line record (`[HH:MM]`, then `📝 …`) to the 日中ログ section of the day's diary. Nothing else in the file changes.
    internal static class NowDoingNote
    {
        public const string Argument = "--now-doing";
        // The same window, opened by the tray app's double press: it may send one unused key to come to the front.
        public const string TapArgument = "--now-doing-tap";
        public const int MaxLength = 200;
        // Ctrl+Enter: the whole text goes to Claudian (at most 4000 characters) and the 日中ログ gets the first 80.
        public const int MaxSendLength = 4000, SendHeadLength = 80;
        public const string Mark = "📝";
        static readonly Regex Record = new Regex(@"^\[(\d{2}):(\d{2})", RegexOptions.CultureInvariant);
        static readonly Regex Stamp = new Regex(@"^\[(\d{2}:\d{2})(?:-\d{2}:\d{2})?\]\s*(.*)$", RegexOptions.CultureInvariant);
        static readonly Regex Field = new Regex(@"\{([^{}]+)\}", RegexOptions.CultureInvariant);

        public static string Message(NowDoingFailure failure, NowDoingSettings settings)
        {
            switch (failure)
            {
                case NowDoingFailure.NoSetting: return "settings.ini に now_doing_note がありません";
                case NowDoingFailure.BadSetting: return "settings.ini の now_doing_note の書き方が違います";
                case NowDoingFailure.NoNote: return "今日の日誌がまだありません";
                case NowDoingFailure.NoHeading: return "「" + (settings == null ? NowDoingSettings.DefaultHeading : settings.Heading) + "」が見つかりません";
                case NowDoingFailure.BadEncoding: return "日誌の文字コードを読めません";
                case NowDoingFailure.Busy: return "日誌が使用中です。もう一度 Enter を押してください";
                case NowDoingFailure.NoBackup: return "書く前の控えを残せないため、書きませんでした";
                case NowDoingFailure.WriteFailed: return "書き込みが途中で失敗しました。日誌を確かめてください（書く前の中身は now-doing-last.md）";
                default: return "";
            }
        }

        // Line breaks and tabs become one space each, outer spaces go, and at most `max` (200) characters remain.
        public static string Clean(string text, int max = MaxLength)
        {
            string value = (text ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            if (value.Length > max) value = value.Substring(0, char.IsHighSurrogate(value[max - 1]) ? max - 1 : max).TrimEnd();
            return value;
        }

        // The text as it goes to Claudian: LF line breaks, no blank space around it.
        public static string ForClaudian(string typed)
        {
            return (typed ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }

        // The 日中ログ line for a text sent to Claudian: the first line that is not blank, at most 80 characters, and
        // （Claudian へ・N行） when the text has two lines or more.
        public static string SendLine(string text)
        {
            string[] lines = (text ?? "").Split('\n');
            string first = lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            if (first.Length > SendHeadLength) first = first.Substring(0, char.IsHighSurrogate(first[SendHeadLength - 1]) ? SendHeadLength - 1 : SendHeadLength);
            return lines.Length >= 2 ? first + "（Claudian へ・" + lines.Length.ToString(CultureInfo.InvariantCulture) + "行）" : first;
        }

        // `{…}` holds a .NET date format, e.g. C:\Notes\{yyyy}年_日誌\{yyyy-MM-dd} - 日誌.md. Null when the pattern is unusable,
        // including a format that leaves `:` or `?` in a folder or file name (such as {HH:mm}).
        public static string NotePath(string pattern, DateTime date)
        {
            try
            {
                string path = Field.Replace(pattern, m => date.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture));
                // Only a full path: a drive with a separator (C:\…) or a network share (\\server\…), not C:notes or \notes.
                bool full = (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) || path.StartsWith(@"\\", StringComparison.Ordinal);
                if (!full || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
                foreach (string part in path.Substring(Path.GetPathRoot(path).Length).Split('\\', '/'))
                    if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                if (Path.GetFileName(path).Length == 0) return null;
                Path.GetFullPath(path);
                return path;
            }
            catch (FormatException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
            catch (SecurityException) { return null; }
        }

        sealed class Line { public string Text, End; }

        static List<Line> Split(string content)
        {
            var lines = new List<Line>();
            int start = 0;
            while (start < content.Length)
            {
                int lf = content.IndexOf('\n', start);
                if (lf < 0) { lines.Add(new Line { Text = content.Substring(start), End = "" }); break; }
                bool crlf = lf > start && content[lf - 1] == '\r';
                lines.Add(new Line { Text = content.Substring(start, (crlf ? lf - 1 : lf) - start), End = crlf ? "\r\n" : "\n" });
                start = lf + 1;
            }
            return lines;
        }
        static bool Blank(Line line) { return line.Text.Trim().Length == 0; }

        // The level of a Markdown heading line (`## x` is 2), or 0 for any other line.
        static int Level(string line)
        {
            int n = 0;
            while (n < line.Length && line[n] == '#') n++;
            return n > 0 && n < line.Length && line[n] == ' ' ? n : 0;
        }

        // Lines of fenced code blocks (``` or ~~~, fence lines included) are text, not headings or records: a `# comment`
        // in a shell snippet must not end the section. A fence that is never closed does not count.
        static bool[] Fenced(List<Line> lines)
        {
            var inside = new bool[lines.Count];
            int open = -1;
            string marker = null;
            for (int i = 0; i < lines.Count; i++)
            {
                string text = lines[i].Text.TrimStart(' ');
                if (open < 0) { if (text.StartsWith("```", StringComparison.Ordinal) || text.StartsWith("~~~", StringComparison.Ordinal)) { open = i; marker = text.Substring(0, 3); } }
                else if (text.StartsWith(marker, StringComparison.Ordinal)) { for (int j = open; j <= i; j++) inside[j] = true; open = -1; }
            }
            return inside;
        }

        // The heading line and the line after the section. The section ends before the next heading of the same or a
        // higher level (for `## 📝 日中ログ`, the next `# ` or `## ` line) or the configured end line, whichever comes first.
        static bool Section(List<Line> lines, NowDoingSettings settings, out int heading, out int end, out bool[] fenced)
        {
            bool[] code = Fenced(lines);
            fenced = code;
            heading = -1;
            for (int i = 0; i < lines.Count; i++) if (!code[i] && lines[i].Text == settings.Heading) { heading = i; break; }
            end = lines.Count;
            if (heading < 0) return false;
            int level = Level(settings.Heading);
            if (level == 0) level = 2;
            for (int i = heading + 1; i < lines.Count; i++)
            {
                if (code[i]) continue;
                int other = Level(lines[i].Text);
                if ((other > 0 && other <= level) || (!string.IsNullOrEmpty(settings.Before) && lines[i].Text.StartsWith(settings.Before, StringComparison.Ordinal))) { end = i; break; }
            }
            return true;
        }

        // The 📝 records of the section, in file order: `[HH:MM]` with `📝 …` on the next line, or both on one line
        // (the form the first version wrote). Null with NoHeading when the section is missing.
        public static List<NowDoingRecord> Records(string content, NowDoingSettings settings, out NowDoingFailure failure)
        {
            failure = NowDoingFailure.None;
            List<Line> lines = Split(content ?? "");
            int heading, end;
            bool[] fenced;
            if (!Section(lines, settings, out heading, out end, out fenced)) { failure = NowDoingFailure.NoHeading; return null; }
            var records = new List<NowDoingRecord>();
            for (int i = heading + 1; i < end; i++)
            {
                if (fenced[i]) continue;
                Match m = Stamp.Match(lines[i].Text);
                if (!m.Success) continue;
                string rest = m.Groups[2].Value;
                if (rest.Length == 0 && i + 1 < end) rest = lines[i + 1].Text;
                if (rest.StartsWith(Mark, StringComparison.Ordinal))
                    records.Add(new NowDoingRecord { Time = m.Groups[1].Value, Text = rest.Substring(Mark.Length).Trim() });
            }
            return records;
        }

        // The new diary text, or null with the reason when the record cannot be added.
        // The same two-line form as the other records: the time on its own line, the text on the next.
        public static string Insert(string content, DateTime time, string text, NowDoingSettings settings, out NowDoingFailure failure, string mark = Mark)
        {
            return InsertEntry(content, time, TwoLines(time, text, mark), settings, out failure);
        }
        static string[] TwoLines(DateTime time, string text, string mark)
        {
            return new[] { "[" + time.ToString("HH:mm", CultureInfo.InvariantCulture) + "]", mark + " " + text };
        }

        // Adds an entry of any number of lines (the first being its time line) where a record at `time` goes.
        public static string InsertEntry(string content, DateTime time, IList<string> entry, NowDoingSettings settings, out NowDoingFailure failure)
        {
            failure = NowDoingFailure.None;
            List<Line> lines = Split(content ?? "");
            int heading, end;
            bool[] fenced;
            if (!Section(lines, settings, out heading, out end, out fenced)) { failure = NowDoingFailure.NoHeading; return null; }
            // Before the first record that starts later than this one; a record at the same minute stays above.
            int minutes = time.Hour * 60 + time.Minute, next = end;
            for (int i = heading + 1; i < end; i++)
            {
                if (fenced[i]) continue;
                Match m = Record.Match(lines[i].Text);
                if (m.Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) > minutes) { next = i; break; }
            }
            // The blank lines right above that point are reused, so the new record has exactly one blank line on each side.
            int blanks = next;
            while (blanks > heading + 1 && Blank(lines[blanks - 1])) blanks--;
            int run = next - blanks;
            int at = run == 0 ? next : blanks + 1;
            // New lines use the line break of the lines around them (files can mix LF and CRLF); otherwise the first
            // one in the file, and LF for a file without any.
            string newline = null;
            if (at > 0 && lines[at - 1].End.Length > 0) newline = lines[at - 1].End;
            else if (at < lines.Count && lines[at].End.Length > 0) newline = lines[at].End;
            if (newline == null) { newline = "\n"; foreach (Line l in lines) if (l.End.Length > 0) { newline = l.End; break; } }
            bool atEnd = next == lines.Count;
            var added = new List<Line>();
            if (run == 0) added.Add(new Line { Text = "", End = newline });
            foreach (string text in entry) added.Add(new Line { Text = text, End = newline });
            if (run <= 1 && !atEnd) added.Add(new Line { Text = "", End = newline });
            if (at == lines.Count && lines.Count > 0 && lines[lines.Count - 1].End.Length == 0)
            {
                // A file without a final line break keeps having none.
                lines[lines.Count - 1].End = newline;
                added[added.Count - 1].End = "";
            }
            lines.InsertRange(at, added);
            var result = new StringBuilder(content.Length + entry.Sum(t => t.Length + 2) + 16);
            foreach (Line l in lines) result.Append(l.Text).Append(l.End);
            return result.ToString();
        }

        static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

        // Strict UTF-8, with or without a BOM. False when the bytes are not UTF-8.
        static bool Decode(byte[] bytes, out bool bom, out string content)
        {
            bom = bytes.Length >= 3 && bytes[0] == Bom[0] && bytes[1] == Bom[1] && bytes[2] == Bom[2];
            try { content = new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)); return true; }
            catch (DecoderFallbackException) { content = null; return false; }
        }

        // Read when the window opens: which file the record goes to, whether it can go there now, and the 📝 records
        // already in the section. The file is only read (other apps may keep writing it meanwhile).
        public static NowDoingView Look(NowDoingSettings settings, DateTime openedAt)
        {
            var view = new NowDoingView { Heading = settings.Heading };
            if (string.IsNullOrEmpty(settings.Note)) { view.Failure = NowDoingFailure.NoSetting; return view; }
            view.Path = NotePath(settings.Note, openedAt);
            if (view.Path == null) { view.Failure = NowDoingFailure.BadSetting; return view; }
            byte[] bytes;
            try
            {
                using (var file = new FileStream(view.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bytes = new byte[file.Length];
                    int read = 0;
                    while (read < bytes.Length) { int n = file.Read(bytes, read, bytes.Length - read); if (n == 0) break; read += n; }
                    if (read != bytes.Length) { view.Failure = NowDoingFailure.Busy; return view; }
                }
            }
            catch (FileNotFoundException) { view.Failure = NowDoingFailure.NoNote; return view; }
            catch (DirectoryNotFoundException) { view.Failure = NowDoingFailure.NoNote; return view; }
            catch (IOException) { view.Failure = NowDoingFailure.Busy; return view; }
            catch (UnauthorizedAccessException) { view.Failure = NowDoingFailure.Busy; return view; }
            catch (SecurityException) { view.Failure = NowDoingFailure.Busy; return view; }
            bool bom;
            string content;
            if (!Decode(bytes, out bom, out content)) { view.Failure = NowDoingFailure.BadEncoding; return view; }
            List<NowDoingRecord> records = Records(content, settings, out view.Failure);
            if (records != null) view.Records = records;
            return view;
        }

        // Reads the diary just before writing, adds the line, and overwrites the same file in place (no rename: the
        // diary folder is synced and a rename can look like another file). The previous content is kept in `backupPath`.
        public static NowDoingFailure Write(NowDoingSettings settings, DateTime openedAt, string text, string backupPath, Action<int> sleep = null, string mark = Mark)
        {
            return WriteEntry(settings, openedAt, TwoLines(openedAt, text, mark), backupPath, sleep);
        }

        // Writes an entry of any number of lines to the diary of the day `openedAt` falls on, where a record at that time goes.
        public static NowDoingFailure WriteEntry(NowDoingSettings settings, DateTime openedAt, IList<string> entry, string backupPath, Action<int> sleep = null)
        {
            if (string.IsNullOrEmpty(settings.Note)) return NowDoingFailure.NoSetting;
            string path = NotePath(settings.Note, openedAt);
            if (path == null) return NowDoingFailure.BadSetting;
            if (!File.Exists(path)) return NowDoingFailure.NoNote;
            if (string.IsNullOrEmpty(backupPath)) return NowDoingFailure.NoBackup;
            for (int attempt = 1; ; attempt++)
            {
                FileStream file = null;
                byte[] old = null;
                try
                {
                    file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    old = new byte[file.Length];
                    int read = 0;
                    while (read < old.Length) { int n = file.Read(old, read, old.Length - read); if (n == 0) break; read += n; }
                    if (read != old.Length) { Close(file); return NowDoingFailure.Busy; }
                }
                catch (FileNotFoundException) { Close(file); return NowDoingFailure.NoNote; }
                catch (DirectoryNotFoundException) { Close(file); return NowDoingFailure.NoNote; }
                catch (IOException) { Close(file); file = null; }
                catch (UnauthorizedAccessException) { Close(file); file = null; }
                catch (SecurityException) { Close(file); file = null; }
                if (file != null)
                {
                    try { return Change(file, old, settings, openedAt, entry, backupPath); }
                    finally { Close(file); }
                }
                // Another app may be writing the diary. Nothing has been changed yet, so it is tried again:
                // up to three tries, 0.3 s apart.
                if (attempt >= 3) return NowDoingFailure.Busy;
                if (sleep != null) sleep(300); else Thread.Sleep(300);
            }
        }

        static void Close(FileStream file)
        {
            if (file == null) return;
            try { file.Dispose(); }
            catch (IOException) { }
        }

        // Adds the record to the open diary. The backup is written before the diary is touched; once writing the diary
        // has started, a failure is reported as it is and never retried, so the record is not added twice and the
        // backup keeps the content from before.
        static NowDoingFailure Change(FileStream file, byte[] old, NowDoingSettings settings, DateTime openedAt, IList<string> entry, string backupPath)
        {
            bool bom;
            string content;
            if (!Decode(old, out bom, out content)) return NowDoingFailure.BadEncoding;
            NowDoingFailure failure;
            string updated = InsertEntry(content, openedAt, entry, settings, out failure);
            if (updated == null) return failure;
            byte[] body = new UTF8Encoding(false).GetBytes(updated);
            var bytes = new byte[(bom ? 3 : 0) + body.Length];
            if (bom) Buffer.BlockCopy(Bom, 0, bytes, 0, 3);
            Buffer.BlockCopy(body, 0, bytes, bom ? 3 : 0, body.Length);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                File.WriteAllBytes(backupPath, old);
            }
            catch (IOException) { return NowDoingFailure.NoBackup; }
            catch (UnauthorizedAccessException) { return NowDoingFailure.NoBackup; }
            catch (SecurityException) { return NowDoingFailure.NoBackup; }
            try
            {
                // Only the bytes from the first change onward are rewritten.
                int same = 0;
                while (same < old.Length && same < bytes.Length && old[same] == bytes[same]) same++;
                file.Position = same;
                file.Write(bytes, same, bytes.Length - same);
                file.SetLength(bytes.Length);
                file.Flush(true);
                return NowDoingFailure.None;
            }
            catch (IOException) { return NowDoingFailure.WriteFailed; }
            catch (UnauthorizedAccessException) { return NowDoingFailure.WriteFailed; }
        }

        // A message for Claudian that nothing took (no plugin, another vault…) is removed the next time the window opens.
        public static void DropStaleMessage(string messagePath, DateTime nowUtc)
        {
            try
            {
                if (!string.IsNullOrEmpty(messagePath) && File.Exists(messagePath) && nowUtc - File.GetLastWriteTimeUtc(messagePath) > TimeSpan.FromMinutes(2))
                    File.Delete(messagePath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
        }

        // The window keeps now-doing-last.md; the tray app's AI done lines keep their own file, so neither replaces the other's.
        public static string BackupPath(string localAppData, string name = "now-doing-last.md")
        {
            return string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "AIUsageManager", name);
        }

        // ---- Handing a line to Claudian (Ctrl+Enter, only with now_doing_claudian=on) ----
        // The text is left in a file under %LOCALAPPDATA% with a one-time token, and obsidian://ai-usage-manager-claudian
        // is opened with only that token. The AIUsageManager Obsidian plugin takes the text (and removes the file) only
        // when the token matches, so a link elsewhere cannot make Claudian run a prompt.
        public const string ClaudianAction = "ai-usage-manager-claudian";

        public static string ClaudianMessagePath(string localAppData)
        {
            return string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "AIUsageManager", "claudian-message.json");
        }

        // The vault that holds the diary: the nearest folder above it with a .obsidian folder. Null when there is none.
        public static string VaultName(string notePath)
        {
            try
            {
                for (string dir = Path.GetDirectoryName(notePath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                    if (Directory.Exists(Path.Combine(dir, ".obsidian"))) { string name = Path.GetFileName(dir); return name.Length == 0 ? null : name; }
            }
            catch (ArgumentException) { }
            catch (PathTooLongException) { }
            catch (SecurityException) { }
            return null;
        }

        public static string ClaudianLink(string token, string vault)
        {
            return "obsidian://" + ClaudianAction + "?" + (string.IsNullOrEmpty(vault) ? "" : "vault=" + Uri.EscapeDataString(vault) + "&") + "token=" + token;
        }

        public static string NewToken()
        {
            var bytes = new byte[16];
            using (var random = System.Security.Cryptography.RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var hex = new StringBuilder(32);
            foreach (byte b in bytes) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return hex.ToString();
        }

        // False when the file cannot be written or Obsidian cannot be opened; the text is not left behind then.
        public static bool HandToClaudian(string text, string messagePath, string vault, Func<string, bool> open, string token = null, long? now = null, string note = null)
        {
            if (string.IsNullOrEmpty(messagePath)) return false;
            token = token ?? NewToken();
            var data = new Dictionary<string, object> { { "token", token }, { "text", text }, { "created", now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } };
            // The diary page the line was written to; the plugin opens it before sending when it is in the vault.
            if (note != null) data["note"] = note;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(messagePath));
                File.WriteAllText(messagePath, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(data), new UTF8Encoding(false));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (SecurityException) { return false; }
            if (open(ClaudianLink(token, vault))) return true;
            try { File.Delete(messagePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        // Opens the link with the app Windows has for it (Obsidian for obsidian://).
        public static bool OpenLink(string link)
        {
            try { using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true })) { } return true; }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (FileNotFoundException) { return false; }
        }
    }
}
