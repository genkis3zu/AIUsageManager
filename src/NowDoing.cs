using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        public static NowDoingSettings Parse(string ini)
        {
            var settings = new NowDoingSettings();
            foreach (string raw in (ini ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string key = line.Substring(0, equals).Trim(), value = line.Substring(equals + 1).Trim();
                if (value.Length == 0) continue;
                if (key == "now_doing_note") settings.Note = value;
                else if (key == "now_doing_heading") settings.Heading = value;
                else if (key == "now_doing_before") settings.Before = value;
            }
            return settings;
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

    internal enum NowDoingFailure { None, NoSetting, BadSetting, NoNote, NoHeading, BadEncoding, Busy }

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
        public const int MaxLength = 200;
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
                default: return "";
            }
        }

        // Line breaks and tabs become one space each, outer spaces go, and at most 200 characters remain.
        public static string Clean(string text)
        {
            string value = (text ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            if (value.Length > MaxLength) value = value.Substring(0, char.IsHighSurrogate(value[MaxLength - 1]) ? MaxLength - 1 : MaxLength).TrimEnd();
            return value;
        }

        // `{…}` holds a .NET date format, e.g. C:\Notes\{yyyy}年_日誌\{yyyy-MM-dd} - 日誌.md. Null when the pattern is unusable.
        public static string NotePath(string pattern, DateTime date)
        {
            try
            {
                string path = Field.Replace(pattern, m => date.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture));
                return Path.IsPathRooted(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0 ? path : null;
            }
            catch (FormatException) { return null; }
            catch (ArgumentException) { return null; }
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

        // The heading line and the line after the section. The section ends before the next `## ` heading or the
        // configured end line, whichever comes first.
        static bool Section(List<Line> lines, NowDoingSettings settings, out int heading, out int end)
        {
            heading = lines.FindIndex(l => l.Text == settings.Heading);
            end = lines.Count;
            if (heading < 0) return false;
            for (int i = heading + 1; i < lines.Count; i++)
                if (lines[i].Text.StartsWith("## ", StringComparison.Ordinal) || (!string.IsNullOrEmpty(settings.Before) && lines[i].Text.StartsWith(settings.Before, StringComparison.Ordinal))) { end = i; break; }
            return true;
        }

        // The 📝 records of the section, in file order: `[HH:MM]` with `📝 …` on the next line, or both on one line
        // (the form the first version wrote). Null with NoHeading when the section is missing.
        public static List<NowDoingRecord> Records(string content, NowDoingSettings settings, out NowDoingFailure failure)
        {
            failure = NowDoingFailure.None;
            List<Line> lines = Split(content ?? "");
            int heading, end;
            if (!Section(lines, settings, out heading, out end)) { failure = NowDoingFailure.NoHeading; return null; }
            var records = new List<NowDoingRecord>();
            for (int i = heading + 1; i < end; i++)
            {
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
        public static string Insert(string content, DateTime time, string text, NowDoingSettings settings, out NowDoingFailure failure)
        {
            failure = NowDoingFailure.None;
            List<Line> lines = Split(content ?? "");
            int heading, end;
            if (!Section(lines, settings, out heading, out end)) { failure = NowDoingFailure.NoHeading; return null; }
            // Before the first record that starts later than this one; a record at the same minute stays above.
            int minutes = time.Hour * 60 + time.Minute, next = end;
            for (int i = heading + 1; i < end; i++)
            {
                Match m = Record.Match(lines[i].Text);
                if (m.Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) > minutes) { next = i; break; }
            }
            // The blank lines right above that point are reused, so the new record has exactly one blank line on each side.
            int blanks = next;
            while (blanks > heading + 1 && Blank(lines[blanks - 1])) blanks--;
            int run = next - blanks;
            // New lines use the file's own line break (the first one found; LF for a file without any).
            string newline = "\n";
            foreach (Line l in lines) if (l.End.Length > 0) { newline = l.End; break; }
            bool atEnd = next == lines.Count;
            var added = new List<Line>();
            if (run == 0) added.Add(new Line { Text = "", End = newline });
            // The same two-line form as the other records: the time on its own line, the text on the next.
            added.Add(new Line { Text = "[" + time.ToString("HH:mm", CultureInfo.InvariantCulture) + "]", End = newline });
            added.Add(new Line { Text = Mark + " " + text, End = newline });
            if (run <= 1 && !atEnd) added.Add(new Line { Text = "", End = newline });
            int at = run == 0 ? next : blanks + 1;
            if (at == lines.Count && lines.Count > 0 && lines[lines.Count - 1].End.Length == 0)
            {
                // A file without a final line break keeps having none.
                lines[lines.Count - 1].End = newline;
                added[added.Count - 1].End = "";
            }
            lines.InsertRange(at, added);
            var result = new StringBuilder(content.Length + text.Length + 16);
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
        public static NowDoingFailure Write(NowDoingSettings settings, DateTime openedAt, string text, string backupPath, Action<int> sleep = null)
        {
            if (string.IsNullOrEmpty(settings.Note)) return NowDoingFailure.NoSetting;
            string path = NotePath(settings.Note, openedAt);
            if (path == null) return NowDoingFailure.BadSetting;
            if (!File.Exists(path)) return NowDoingFailure.NoNote;
            if (string.IsNullOrEmpty(backupPath)) return NowDoingFailure.Busy;
            for (int attempt = 1; ; attempt++)
            {
                bool retry = false;
                try
                {
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    {
                        var old = new byte[file.Length];
                        int read = 0;
                        while (read < old.Length) { int n = file.Read(old, read, old.Length - read); if (n == 0) break; read += n; }
                        if (read != old.Length) return NowDoingFailure.Busy;
                        bool bom;
                        string content;
                        if (!Decode(old, out bom, out content)) return NowDoingFailure.BadEncoding;
                        NowDoingFailure failure;
                        string updated = Insert(content, openedAt, text, settings, out failure);
                        if (updated == null) return failure;
                        byte[] body = new UTF8Encoding(false).GetBytes(updated);
                        var bytes = new byte[(bom ? 3 : 0) + body.Length];
                        if (bom) Buffer.BlockCopy(Bom, 0, bytes, 0, 3);
                        Buffer.BlockCopy(body, 0, bytes, bom ? 3 : 0, body.Length);
                        Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                        File.WriteAllBytes(backupPath, old);
                        // Only the bytes from the first change onward are rewritten.
                        int same = 0;
                        while (same < old.Length && same < bytes.Length && old[same] == bytes[same]) same++;
                        file.Position = same;
                        file.Write(bytes, same, bytes.Length - same);
                        file.SetLength(bytes.Length);
                        file.Flush(true);
                        return NowDoingFailure.None;
                    }
                }
                catch (FileNotFoundException) { return NowDoingFailure.NoNote; }
                catch (DirectoryNotFoundException) { return NowDoingFailure.NoNote; }
                catch (IOException) { retry = true; }
                catch (UnauthorizedAccessException) { retry = true; }
                catch (SecurityException) { retry = true; }
                // Another app may be writing the diary: up to three tries, 0.3 s apart.
                if (!retry || attempt >= 3) return NowDoingFailure.Busy;
                if (sleep != null) sleep(300); else Thread.Sleep(300);
            }
        }

        public static string BackupPath(string localAppData)
        {
            return string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "AIUsageManager", "now-doing-last.md");
        }
    }
}
