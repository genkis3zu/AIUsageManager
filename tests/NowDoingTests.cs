using System;
using System.IO;
using System.Text;
using AIUsageManager;

internal static partial class Tests
{
    static string AddAt(string content, int hour, int minute, string text, NowDoingSettings settings = null)
    {
        NowDoingFailure failure;
        string result = NowDoingNote.Insert(content, new DateTime(2026, 11, 24, hour, minute, 0), text, settings ?? new NowDoingSettings { Before = "📎 **今日の生成物**" }, out failure);
        Assert(result == null ? failure != NowDoingFailure.None : failure == NowDoingFailure.None);
        return result;
    }
    static string Lines(params string[] lines) { return string.Join("\n", lines) + "\n"; }

    static void RunNowDoingTests()
    {
        string note = Lines("# 日誌", "", "## 📝 日中ログ", "", "[10:00-10:30]", "🏋️ ジム", "", "[11:05]", "前の記録", "", "[12:00-13:00]", "昼食。", "",
            "📎 **今日の生成物**（自動挿入）", "", "## 😊今日の振り返り(21:00)", "", "- [ ] 振り返る");
        Check("A line goes before the first later record, with one blank line on each side", () => {
            Assert(AddAt(note, 10, 40, "見積の表を直している") == note.Replace("[11:05]\n", "[10:40] 📝 見積の表を直している\n\n[11:05]\n"));
        });
        Check("A record at the same minute stays above the new line", () => {
            Assert(AddAt(note, 11, 5, "x") == note.Replace("[12:00-13:00]\n", "[11:05] 📝 x\n\n[12:00-13:00]\n"));
        });
        Check("A [HH:MM-HH:MM] record is compared by its start", () => {
            Assert(AddAt(note, 9, 59, "x") == note.Replace("[10:00-10:30]\n", "[09:59] 📝 x\n\n[10:00-10:30]\n"));
            Assert(AddAt(note, 10, 0, "x") == note.Replace("[11:05]\n", "[10:00] 📝 x\n\n[11:05]\n"));
        });
        Check("Without a later record the line goes before the end line, above its blank lines", () => {
            Assert(AddAt(note, 18, 0, "x") == note.Replace("昼食。\n\n📎", "昼食。\n\n[18:00] 📝 x\n\n📎"));
        });
        Check("Without an end line the section ends at the next ## heading", () => {
            string plain = note.Replace("📎 **今日の生成物**（自動挿入）\n\n", "");
            Assert(AddAt(plain, 18, 0, "x", new NowDoingSettings()) == plain.Replace("昼食。\n\n## 😊", "昼食。\n\n[18:00] 📝 x\n\n## 😊"));
            // The end line counts only when it is set.
            Assert(AddAt(note, 18, 0, "x", new NowDoingSettings()) == note.Replace("（自動挿入）\n\n## 😊", "（自動挿入）\n\n[18:00] 📝 x\n\n## 😊"));
        });
        Check("A section that runs to the end of the file gets the line last", () => {
            Assert(AddAt(Lines("## 📝 日中ログ", "", "[09:00]", "朝"), 10, 0, "x") == Lines("## 📝 日中ログ", "", "[09:00]", "朝", "", "[10:00] 📝 x"));
            Assert(AddAt("## 📝 日中ログ\n\n[09:00]\n朝", 10, 0, "x") == "## 📝 日中ログ\n\n[09:00]\n朝\n\n[10:00] 📝 x");
            Assert(AddAt(Lines("## 📝 日中ログ", "[09:00]", "朝", ""), 10, 0, "x") == Lines("## 📝 日中ログ", "[09:00]", "朝", "", "[10:00] 📝 x"));
        });
        Check("An empty section gets the line with one blank line on each side", () => {
            Assert(AddAt(Lines("## 📝 日中ログ", "## 次"), 10, 0, "x") == Lines("## 📝 日中ログ", "", "[10:00] 📝 x", "", "## 次"));
            Assert(AddAt(Lines("## 📝 日中ログ", "", "", "", "## 次"), 10, 0, "x") == Lines("## 📝 日中ログ", "", "[10:00] 📝 x", "", "", "## 次"));
            Assert(AddAt(Lines("## 📝 日中ログ", "", "[11:00]"), 10, 0, "x") == Lines("## 📝 日中ログ", "", "[10:00] 📝 x", "", "[11:00]"));
        });
        Check("Times in quotes and lines that start with [[ are not records", () => {
            string quoted = Lines("## 📝 日中ログ", "", "[09:00]", "朝", "> [12:00] 引用", "[[12:00 メモ]]", "", "## 次");
            Assert(AddAt(quoted, 10, 0, "x") == Lines("## 📝 日中ログ", "", "[09:00]", "朝", "> [12:00] 引用", "[[12:00 メモ]]", "", "[10:00] 📝 x", "", "## 次"));
        });
        Check("Records outside the section are not used", () => {
            string other = Lines("## 🎯 今日やること", "[08:00] 予定", "## 📝 日中ログ", "", "[09:00]", "", "## 次", "[09:30] 別");
            Assert(AddAt(other, 9, 15, "x") == Lines("## 🎯 今日やること", "[08:00] 予定", "## 📝 日中ログ", "", "[09:00]", "", "[09:15] 📝 x", "", "## 次", "[09:30] 別"));
        });
        Check("CRLF files get CRLF lines and keep every other byte", () => {
            string crlf = note.Replace("\n", "\r\n");
            Assert(AddAt(crlf, 10, 40, "x") == crlf.Replace("[11:05]\r\n", "[10:40] 📝 x\r\n\r\n[11:05]\r\n"));
        });
        Check("A missing heading writes nothing and names the heading", () => {
            NowDoingFailure failure;
            var custom = new NowDoingSettings { Heading = "## ログ" };
            Assert(NowDoingNote.Insert(note, DateTime.Now, "x", custom, out failure) == null && failure == NowDoingFailure.NoHeading);
            Assert(NowDoingNote.Message(failure, custom) == "「## ログ」が見つかりません");
            Assert(NowDoingNote.Insert(note.Replace("## 📝 日中ログ", "## 📝 日中ログ（下書き）"), DateTime.Now, "x", new NowDoingSettings(), out failure) == null && failure == NowDoingFailure.NoHeading);
        });
        Check("settings.ini keys for the window are read, with the default heading", () => {
            var s = NowDoingSettings.Parse("theme=dark\r\nnow_doing_note=C:\\Notes\\{yyyy-MM-dd}.md\r\nnow_doing_before=📎 **今日の生成物**\r\n");
            Assert(s.Note == "C:\\Notes\\{yyyy-MM-dd}.md" && s.Before == "📎 **今日の生成物**" && s.Heading == "## 📝 日中ログ");
            Assert(NowDoingSettings.Parse("now_doing_heading=## ログ\n").Heading == "## ログ" && NowDoingSettings.Parse("").Note == null);
        });
        Check("Date fields in the diary path are filled in", () => {
            Assert(NowDoingNote.NotePath(@"C:\Notes\{yyyy}年_日誌\{yyyy-MM-dd} - 日誌.md", new DateTime(2026, 11, 24, 23, 59, 0)) == @"C:\Notes\2026年_日誌\2026-11-24 - 日誌.md");
            Assert(NowDoingNote.NotePath(@"notes\{yyyy}.md", DateTime.Now) == null && NowDoingNote.NotePath(@"C:\{yyyy", DateTime.Now) == @"C:\{yyyy");
        });
        Check("Line breaks and tabs become spaces and the text is cut at 200 characters", () => {
            Assert(NowDoingNote.Clean("  見積\r\nの\t表\n直し\r中  ") == "見積 の 表 直し 中");
            Assert(NowDoingNote.Clean(new string('あ', 250)).Length == 200 && NowDoingNote.Clean(" \t\n ") == "");
            Assert(NowDoingNote.Clean(new string('a', 199) + "😀").Length == 199);
        });

        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "now-doing-test");
        Directory.CreateDirectory(dir);
        string backup = Path.Combine(dir, "now-doing-last.md");
        Func<string, NowDoingSettings> at = file => new NowDoingSettings { Note = Path.Combine(dir, file), Before = "📎 **今日の生成物**" };
        var opened = new DateTime(2026, 11, 24, 10, 40, 0);
        Check("Writing keeps the BOM and line breaks of the file, and keeps the old content as the backup", () => {
            foreach (bool bom in new[] { false, true })
                foreach (string newline in new[] { "\n", "\r\n" })
                {
                    string file = Path.Combine(dir, "{yyyy-MM-dd}.md"), real = Path.Combine(dir, "2026-11-24.md");
                    string text = note.Replace("\n", newline);
                    byte[] before = new UTF8Encoding(bom).GetPreamble();
                    byte[] body = new UTF8Encoding(false).GetBytes(text), old = new byte[before.Length + body.Length];
                    before.CopyTo(old, 0); body.CopyTo(old, before.Length);
                    File.WriteAllBytes(real, old);
                    Assert(NowDoingNote.Write(new NowDoingSettings { Note = file, Before = "📎 **今日の生成物**" }, opened, "見積", backup) == NowDoingFailure.None);
                    byte[] now = File.ReadAllBytes(real);
                    Assert((now.Length >= 3 && now[0] == 0xEF && now[1] == 0xBB && now[2] == 0xBF) == bom);
                    Assert(Encoding.UTF8.GetString(now, bom ? 3 : 0, now.Length - (bom ? 3 : 0)) == text.Replace("[11:05]" + newline, "[10:40] 📝 見積" + newline + newline + "[11:05]" + newline));
                    Assert(Convert.ToBase64String(File.ReadAllBytes(backup)) == Convert.ToBase64String(old));
                }
        });
        Check("Nothing is written without now_doing_note, without the diary, or with a diary that is not UTF-8", () => {
            File.Delete(backup);
            Assert(NowDoingNote.Write(new NowDoingSettings(), opened, "x", backup) == NowDoingFailure.NoSetting);
            Assert(NowDoingNote.Message(NowDoingFailure.NoSetting, null) == "settings.ini に now_doing_note がありません");
            Assert(NowDoingNote.Write(at("none-{yyyy}.md"), opened, "x", backup) == NowDoingFailure.NoNote);
            Assert(NowDoingNote.Message(NowDoingFailure.NoNote, null) == "今日の日誌がまだありません");
            string bad = Path.Combine(dir, "bad.md");
            byte[] invalid = { 0x23, 0x20, 0xFF, 0xFE, 0x0A };
            File.WriteAllBytes(bad, invalid);
            Assert(NowDoingNote.Write(at("bad.md"), opened, "x", backup) == NowDoingFailure.BadEncoding && Convert.ToBase64String(File.ReadAllBytes(bad)) == Convert.ToBase64String(invalid));
            File.WriteAllText(bad, "# no heading\n");
            Assert(NowDoingNote.Write(at("bad.md"), opened, "x", backup) == NowDoingFailure.NoHeading && File.ReadAllText(bad) == "# no heading\n");
            Assert(!File.Exists(backup));
        });
        Check("A diary held by another app is tried three times, 0.3 s apart, then reported as busy", () => {
            string held = Path.Combine(dir, "held.md");
            File.WriteAllText(held, note);
            var waits = new System.Collections.Generic.List<int>();
            using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert(NowDoingNote.Write(at("held.md"), opened, "x", backup, waits.Add) == NowDoingFailure.Busy);
            Assert(waits.Count == 2 && waits[0] == 300 && waits[1] == 300 && File.ReadAllText(held) == note);
            Assert(NowDoingNote.Message(NowDoingFailure.Busy, null) == "日誌が使用中です。もう一度 Enter を押してください");
        });
        Check("Saving the theme, notice and waiting choices keeps the now_doing_ lines", () => {
            string ini = Path.Combine(dir, "settings.ini");
            File.WriteAllText(ini, "theme=light\r\nnow_doing_note=C:\\N\\{yyyy-MM-dd} - 日誌.md\r\nnow_doing_heading=## 📝 日中ログ\r\nnow_doing_before=📎 **今日の生成物**\r\n", new UTF8Encoding(false));
            var themes = new ThemeSettings(ini);
            Assert(themes.Save(ThemeMode.Dark) && themes.SaveNotifications(NotificationMode.Off) && themes.SaveWaiting(WaitingNoticeMode.Frequent));
            Assert(themes.Load() == ThemeMode.Dark && themes.LoadNotifications() == NotificationMode.Off && themes.LoadWaiting() == WaitingNoticeMode.Frequent);
            var s = NowDoingSettings.Load(ini);
            Assert(s.Note == "C:\\N\\{yyyy-MM-dd} - 日誌.md" && s.Heading == "## 📝 日中ログ" && s.Before == "📎 **今日の生成物**");
            string saved = File.ReadAllText(ini);
            Assert(saved.Split(new[] { "theme=" }, StringSplitOptions.None).Length == 2 && saved.Split(new[] { "now_doing_note=" }, StringSplitOptions.None).Length == 2);
        });
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
