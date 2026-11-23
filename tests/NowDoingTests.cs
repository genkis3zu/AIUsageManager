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
    static string Shown(System.Collections.Generic.IEnumerable<NowDoingRecord> records)
    {
        var text = new StringBuilder();
        foreach (NowDoingRecord r in records) text.Append(r.Time).Append(' ').Append(r.Text).Append('|');
        return text.ToString();
    }

    static void RunNowDoingTests()
    {
        string note = Lines("# 日誌", "", "## 📝 日中ログ", "", "[10:00-10:30]", "🏋️ ジム", "", "[11:05]", "前の記録", "", "[12:00-13:00]", "昼食。", "",
            "📎 **今日の生成物**（自動挿入）", "", "## 😊今日の振り返り(21:00)", "", "- [ ] 振り返る");
        Check("A record goes before the first later record as two lines, with one blank line on each side", () => {
            Assert(AddAt(note, 10, 40, "見積の表を直している") == note.Replace("[11:05]\n", "[10:40]\n📝 見積の表を直している\n\n[11:05]\n"));
        });
        Check("A record at the same minute stays above the new one", () => {
            Assert(AddAt(note, 11, 5, "x") == note.Replace("[12:00-13:00]\n", "[11:05]\n📝 x\n\n[12:00-13:00]\n"));
        });
        Check("A [HH:MM-HH:MM] record is compared by its start", () => {
            Assert(AddAt(note, 9, 59, "x") == note.Replace("[10:00-10:30]\n", "[09:59]\n📝 x\n\n[10:00-10:30]\n"));
            Assert(AddAt(note, 10, 0, "x") == note.Replace("[11:05]\n", "[10:00]\n📝 x\n\n[11:05]\n"));
        });
        Check("Without a later record the record goes before the end line, above its blank lines", () => {
            Assert(AddAt(note, 18, 0, "x") == note.Replace("昼食。\n\n📎", "昼食。\n\n[18:00]\n📝 x\n\n📎"));
        });
        Check("Without an end line the section ends at the next ## heading", () => {
            string plain = note.Replace("📎 **今日の生成物**（自動挿入）\n\n", "");
            Assert(AddAt(plain, 18, 0, "x", new NowDoingSettings()) == plain.Replace("昼食。\n\n## 😊", "昼食。\n\n[18:00]\n📝 x\n\n## 😊"));
            // The end line counts only when it is set.
            Assert(AddAt(note, 18, 0, "x", new NowDoingSettings()) == note.Replace("（自動挿入）\n\n## 😊", "（自動挿入）\n\n[18:00]\n📝 x\n\n## 😊"));
        });
        Check("A section that runs to the end of the file gets the record last", () => {
            Assert(AddAt(Lines("## 📝 日中ログ", "", "[09:00]", "朝"), 10, 0, "x") == Lines("## 📝 日中ログ", "", "[09:00]", "朝", "", "[10:00]", "📝 x"));
            Assert(AddAt("## 📝 日中ログ\n\n[09:00]\n朝", 10, 0, "x") == "## 📝 日中ログ\n\n[09:00]\n朝\n\n[10:00]\n📝 x");
            Assert(AddAt(Lines("## 📝 日中ログ", "[09:00]", "朝", ""), 10, 0, "x") == Lines("## 📝 日中ログ", "[09:00]", "朝", "", "[10:00]", "📝 x"));
        });
        Check("An empty section gets the record with one blank line on each side", () => {
            Assert(AddAt(Lines("## 📝 日中ログ", "## 次"), 10, 0, "x") == Lines("## 📝 日中ログ", "", "[10:00]", "📝 x", "", "## 次"));
            Assert(AddAt(Lines("## 📝 日中ログ", "", "", "", "## 次"), 10, 0, "x") == Lines("## 📝 日中ログ", "", "[10:00]", "📝 x", "", "", "## 次"));
            Assert(AddAt(Lines("## 📝 日中ログ", "", "[11:00]"), 10, 0, "x") == Lines("## 📝 日中ログ", "", "[10:00]", "📝 x", "", "[11:00]"));
        });
        Check("Times in quotes and lines that start with [[ are not records", () => {
            string quoted = Lines("## 📝 日中ログ", "", "[09:00]", "朝", "> [12:00] 引用", "[[12:00 メモ]]", "", "## 次");
            Assert(AddAt(quoted, 10, 0, "x") == Lines("## 📝 日中ログ", "", "[09:00]", "朝", "> [12:00] 引用", "[[12:00 メモ]]", "", "[10:00]", "📝 x", "", "## 次"));
        });
        Check("Records outside the section are not used", () => {
            string other = Lines("## 🎯 今日やること", "[08:00] 予定", "## 📝 日中ログ", "", "[09:00]", "", "## 次", "[09:30] 別");
            Assert(AddAt(other, 9, 15, "x") == Lines("## 🎯 今日やること", "[08:00] 予定", "## 📝 日中ログ", "", "[09:00]", "", "[09:15]", "📝 x", "", "## 次", "[09:30] 別"));
        });
        Check("CRLF files get CRLF lines and keep every other byte", () => {
            string crlf = note.Replace("\n", "\r\n");
            Assert(AddAt(crlf, 10, 40, "x") == crlf.Replace("[11:05]\r\n", "[10:40]\r\n📝 x\r\n\r\n[11:05]\r\n"));
        });
        Check("A missing heading writes nothing and names the heading", () => {
            NowDoingFailure failure;
            var custom = new NowDoingSettings { Heading = "## ログ" };
            Assert(NowDoingNote.Insert(note, DateTime.Now, "x", custom, out failure) == null && failure == NowDoingFailure.NoHeading);
            Assert(NowDoingNote.Message(failure, custom) == "「## ログ」が見つかりません");
            Assert(NowDoingNote.Insert(note.Replace("## 📝 日中ログ", "## 📝 日中ログ（下書き）"), DateTime.Now, "x", new NowDoingSettings(), out failure) == null && failure == NowDoingFailure.NoHeading);
        });
        Check("The 📝 records of the section are read in both forms, and nothing else is", () => {
            string log = Lines("## 🎯 今日やること", "[08:00]", "📝 外の記録", "## 📝 日中ログ", "", "[09:00-09:30]", "🏃 散歩", "", "[10:00]", "📝  二行の記録 ",
                "", "[11:00] 📝 一行の記録", "", "> [11:30] 📝 引用", "[12:00]", "", "📝 空行の後", "", "[13:00-14:00]", "📝 幅のある記録", "", "📎 **今日の生成物**", "[15:00]", "📝 終わりの後");
            NowDoingFailure failure;
            var records = NowDoingNote.Records(log, new NowDoingSettings { Before = "📎 **今日の生成物**" }, out failure);
            Assert(failure == NowDoingFailure.None && Shown(records) == "10:00 二行の記録|11:00 一行の記録|13:00 幅のある記録|");
            Assert(NowDoingNote.Records(log, new NowDoingSettings { Heading = "## ログ" }, out failure) == null && failure == NowDoingFailure.NoHeading);
            // A record this window wrote is read back.
            Assert(Shown(NowDoingNote.Records(AddAt(note, 10, 40, "見積"), new NowDoingSettings(), out failure)) == "10:40 見積|");
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
                    Assert(Encoding.UTF8.GetString(now, bom ? 3 : 0, now.Length - (bom ? 3 : 0)) == text.Replace("[11:05]" + newline, "[10:40]" + newline + "📝 見積" + newline + newline + "[11:05]" + newline));
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
        Check("Opening the window finds the page, says why it cannot be written, and reads today's records without changing the file", () => {
            NowDoingView view = NowDoingNote.Look(new NowDoingSettings(), opened);
            Assert(view.Failure == NowDoingFailure.NoSetting && view.Path == null && view.Records.Count == 0);
            Assert(NowDoingNote.Look(new NowDoingSettings { Note = @"notes\{yyyy}.md" }, opened).Failure == NowDoingFailure.BadSetting);
            view = NowDoingNote.Look(at("none-{yyyy-MM-dd}.md"), opened);
            Assert(view.Failure == NowDoingFailure.NoNote && view.Path == Path.Combine(dir, "none-2026-11-24.md"));
            Assert(NowDoingNote.Look(at("missing-folder\\{yyyy}.md"), opened).Failure == NowDoingFailure.NoNote);
            string bad = Path.Combine(dir, "bad.md");
            File.WriteAllBytes(bad, new byte[] { 0x23, 0xFF });
            Assert(NowDoingNote.Look(at("bad.md"), opened).Failure == NowDoingFailure.BadEncoding);
            File.WriteAllText(bad, "# no heading\n");
            view = NowDoingNote.Look(at("bad.md"), opened);
            Assert(view.Failure == NowDoingFailure.NoHeading && view.Heading == "## 📝 日中ログ");
            string today = Path.Combine(dir, "today.md");
            string text = note.Replace("[11:05]\n前の記録", "[11:05]\n📝 前の記録");
            byte[] bytes = new UTF8Encoding(true).GetPreamble();
            byte[] all = new byte[bytes.Length + Encoding.UTF8.GetByteCount(text)];
            bytes.CopyTo(all, 0); Encoding.UTF8.GetBytes(text, 0, text.Length, all, bytes.Length);
            File.WriteAllBytes(today, all);
            // Another app keeping the diary open for writing does not stop the window from reading it.
            using (new FileStream(today, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                view = NowDoingNote.Look(at("today.md"), opened);
            Assert(view.Failure == NowDoingFailure.None && view.Path == today && Shown(view.Records) == "11:05 前の記録|");
            Assert(Convert.ToBase64String(File.ReadAllBytes(today)) == Convert.ToBase64String(all));
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
        Check("Sending to Claudian is on only with now_doing_claudian=on", () => {
            Assert(NowDoingSettings.Parse("now_doing_claudian=on\n").Claudian && !NowDoingSettings.Parse("now_doing_claudian=off\n").Claudian && !NowDoingSettings.Parse("").Claudian);
        });
        Check("The vault is the nearest folder above the diary with a .obsidian folder", () => {
            string vault = Path.Combine(dir, "My Vault");
            Directory.CreateDirectory(Path.Combine(vault, ".obsidian"));
            Directory.CreateDirectory(Path.Combine(vault, "notes", "2026"));
            Assert(NowDoingNote.VaultName(Path.Combine(vault, "notes", "2026", "2026-11-24.md")) == "My Vault");
            Assert(NowDoingNote.VaultName(Path.Combine(dir, "no-vault", "note.md")) == null);
        });
        Check("The Obsidian link carries only the vault and the token", () => {
            Assert(NowDoingNote.ClaudianLink("0123456789abcdef0123456789abcdef", "My Vault") == "obsidian://ai-usage-manager-claudian?vault=My%20Vault&token=0123456789abcdef0123456789abcdef");
            Assert(NowDoingNote.ClaudianLink("ab", null) == "obsidian://ai-usage-manager-claudian?token=ab");
            string a = NowDoingNote.NewToken(), b = NowDoingNote.NewToken();
            Assert(a.Length == 32 && a != b && System.Text.RegularExpressions.Regex.IsMatch(a, "^[0-9a-f]{32}$"));
        });
        Check("The text is left with its token for the plugin, and removed again when Obsidian cannot be opened", () => {
            string message = Path.Combine(dir, "local", "AIUsageManager", "claudian-message.json");
            var links = new System.Collections.Generic.List<string>();
            Assert(NowDoingNote.HandToClaudian("見積の表を直している", message, "My Vault", link => { links.Add(link); return true; }, "0123456789abcdef0123456789abcdef", 1800000000000));
            Assert(links.Count == 1 && links[0] == "obsidian://ai-usage-manager-claudian?vault=My%20Vault&token=0123456789abcdef0123456789abcdef");
            var data = (System.Collections.Generic.Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(message, Encoding.UTF8));
            Assert((string)data["token"] == "0123456789abcdef0123456789abcdef" && (string)data["text"] == "見積の表を直している" && Convert.ToInt64(data["created"]) == 1800000000000);
            Assert(!NowDoingNote.HandToClaudian("x", message, null, link => false) && !File.Exists(message));
            Assert(!NowDoingNote.HandToClaudian("x", null, null, link => { links.Add(link); return true; }) && links.Count == 1);
            Assert(NowDoingNote.ClaudianMessagePath(@"C:\Users\a\AppData\Local") == @"C:\Users\a\AppData\Local\AIUsageManager\claudian-message.json" && NowDoingNote.ClaudianMessagePath("") == null);
            Assert(!data.ContainsKey("note"));
        });
        Check("The message for Claudian carries the diary page", () => {
            string message = Path.Combine(dir, "local", "AIUsageManager", "claudian-message.json");
            Assert(NowDoingNote.HandToClaudian("見積", message, "V", link => true, note: @"C:\V\日誌.md"));
            var data = (System.Collections.Generic.Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(message, Encoding.UTF8));
            Assert((string)data["note"] == @"C:\V\日誌.md" && (string)data["text"] == "見積");
        });
        Check("The line for the 日中ログ when sending is the first line, cut at 80, with the line count", () => {
            Assert(NowDoingNote.SendLine("見積の表") == "見積の表");
            Assert(NowDoingNote.SendLine(NowDoingNote.ForClaudian("\n\n  一行目  \n二行目\n\n四行目")) == "一行目（Claudian へ・4行）");
            Assert(NowDoingNote.SendLine(new string('あ', 100)) == new string('あ', 80));
            Assert(NowDoingNote.SendLine(new string('あ', 100) + "\n次") == new string('あ', 80) + "（Claudian へ・2行）");
            Assert(NowDoingNote.SendLine(new string('a', 79) + "💊") == new string('a', 79));
            Assert(NowDoingNote.MaxSendLength == 4000 && NowDoingNote.SendHeadLength == 80);
        });
        Check("Text for Claudian keeps its line breaks as LF", () => {
            Assert(NowDoingNote.ForClaudian(" a\r\nb\rc \n") == "a\nb\nc" && NowDoingNote.ForClaudian(null) == "");
        });
        Check("A line with another mark goes where a 📝 line would, and is not one of today's records", () => {
            NowDoingFailure failure;
            var settings = new NowDoingSettings { Before = "📎 **今日の生成物**" };
            string done = NowDoingNote.Insert(note, new DateTime(2026, 11, 24, 10, 40, 0), "Claude 完了: x", settings, out failure, "🤖");
            Assert(failure == NowDoingFailure.None && done == note.Replace("[11:05]\n", "[10:40]\n🤖 Claude 完了: x\n\n[11:05]\n"));
            Assert(Shown(NowDoingNote.Records(done, settings, out failure)) == "");
            Assert(NowDoingNote.BackupPath(@"C:\L", "ai-done-last.md") == @"C:\L\AIUsageManager\ai-done-last.md" && NowDoingNote.BackupPath(@"C:\L") == @"C:\L\AIUsageManager\now-doing-last.md");
            string real = Path.Combine(dir, "2026-11-24.md");
            File.WriteAllText(real, note, new UTF8Encoding(false));
            Assert(NowDoingNote.Write(at("{yyyy-MM-dd}.md"), new DateTime(2026, 11, 24, 10, 40, 0), "Codex 完了: y", backup, null, "🤖") == NowDoingFailure.None);
            Assert(File.ReadAllText(real, Encoding.UTF8) == note.Replace("[11:05]\n", "[10:40]\n🤖 Codex 完了: y\n\n[11:05]\n"));
        });
        Check("The shortcut key is read from now_doing_hotkey and needs Ctrl, Alt or Win with one key", () => {
            Assert(NowDoingSettings.Parse("now_doing_hotkey=Ctrl+Alt+N\n").Hotkey == "Ctrl+Alt+N" && NowDoingSettings.Parse("").Hotkey == null);
            NowDoingHotkey k = NowDoingHotkey.Parse(" ctrl + alt + n ");
            Assert(k != null && k.Modifiers == (NowDoingHotkey.Control | NowDoingHotkey.Alt) && k.Key == System.Windows.Forms.Keys.N && k.Text == "ctrl + alt + n");
            Assert(NowDoingHotkey.Parse("Win+Shift+F12").Key == System.Windows.Forms.Keys.F12 && NowDoingHotkey.Parse("Win+Shift+F12").Modifiers == (NowDoingHotkey.Win | NowDoingHotkey.Shift));
            Assert(NowDoingHotkey.Parse("Alt+1").Key == System.Windows.Forms.Keys.D1 && NowDoingHotkey.Parse("Ctrl+Space").Key == System.Windows.Forms.Keys.Space);
            foreach (string bad in new[] { null, "", "N", "Shift+N", "Ctrl+Alt", "Ctrl+N+M", "Ctrl+F25", "Ctrl+F0", "Ctrl++", "Ctrl+Enter", "Ctrl+あ", "Hyper+N" })
                Assert(NowDoingHotkey.Parse(bad) == null);
        });
        Check("The tray says when the shortcut key is written wrongly or taken by another app", () => {
            Assert(NowDoingHotkey.Problem(null, null, false) == null && NowDoingHotkey.Problem("  ", null, false) == null);
            Assert(NowDoingHotkey.Problem("Shift+N", null, false) == "settings.ini の now_doing_hotkey「Shift+N」の書き方が違います（例: Ctrl+Alt+N）");
            Assert(NowDoingHotkey.Problem("Ctrl+Alt+N", NowDoingHotkey.Parse("Ctrl+Alt+N"), false) == "ショートカット「Ctrl+Alt+N」は、ほかのアプリが使っているため使えません");
            Assert(NowDoingHotkey.Problem("Ctrl+Alt+N", NowDoingHotkey.Parse("Ctrl+Alt+N"), true) == null);
        });
        Check("A date format or name that makes an unusable path is a bad setting, not a crash", () => {
            var day = new DateTime(2026, 11, 24, 10, 40, 0);
            foreach (string bad in new[] { @"C:\N\{HH:mm}.md", @"C:\N\{t}.md", @"C:\N\a?.md", @"C:\N\a*.md", @"C:\N\{yyyy}\", @"C:\N\x:y\a.md" })
                Assert(NowDoingNote.NotePath(bad, day) == null);
            Assert(NowDoingNote.NotePath(@"C:\N\{yyyy-MM-dd HH.mm}.md", day) == @"C:\N\2026-11-24 10.40.md");
            NowDoingView view = NowDoingNote.Look(new NowDoingSettings { Note = @"C:\N\{HH:mm}.md" }, day);
            Assert(view.Failure == NowDoingFailure.BadSetting && view.Path == null);
            Assert(NowDoingNote.Write(new NowDoingSettings { Note = @"C:\N\a?.md" }, day, "x", backup) == NowDoingFailure.BadSetting);
        });
        Check("The section ends at the next heading of the same or a higher level", () => {
            string top = Lines("## 📝 日中ログ", "", "[09:00]", "朝", "", "# 次の大見出し", "[10:30] 外");
            Assert(AddAt(top, 10, 0, "x", new NowDoingSettings()) == Lines("## 📝 日中ログ", "", "[09:00]", "朝", "", "[10:00]", "📝 x", "", "# 次の大見出し", "[10:30] 外"));
            string deep = Lines("### A", "[09:00]", "m", "### B", "other", "#### C");
            Assert(AddAt(deep, 10, 40, "x", new NowDoingSettings { Heading = "### A" }) == Lines("### A", "[09:00]", "m", "", "[10:40]", "📝 x", "", "### B", "other", "#### C"));
            // A deeper heading inside the section does not end it, and #tags are not headings.
            string inner = Lines("## 📝 日中ログ", "", "[09:00]", "#タグ", "### 午前", "[11:00]", "昼前", "", "## 次");
            Assert(AddAt(inner, 10, 0, "x", new NowDoingSettings()) == Lines("## 📝 日中ログ", "", "[09:00]", "#タグ", "### 午前", "", "[10:00]", "📝 x", "", "[11:00]", "昼前", "", "## 次"));
        });
        Check("Headings and records inside fenced code blocks are text", () => {
            string code = Lines("## 📝 日中ログ", "", "[10:00]", "setup", "```bash", "# install", "[12:00] ログの行", "npm i", "```", "", "## Next");
            Assert(AddAt(code, 11, 0, "x", new NowDoingSettings()) == Lines("## 📝 日中ログ", "", "[10:00]", "setup", "```bash", "# install", "[12:00] ログの行", "npm i", "```", "", "[11:00]", "📝 x", "", "## Next"));
            string tilde = Lines("~~~", "## 📝 日中ログ", "~~~", "## 📝 日中ログ", "", "[09:00]", "朝", "", "~~~", "[09:30]", "📝 コードの中", "~~~", "", "## 次");
            NowDoingFailure failure;
            Assert(Shown(NowDoingNote.Records(tilde + "", new NowDoingSettings(), out failure)) == "" && failure == NowDoingFailure.None);
            Assert(AddAt(tilde, 10, 0, "x", new NowDoingSettings()) == tilde.Replace("~~~\n\n## 次", "~~~\n\n[10:00]\n📝 x\n\n## 次"));
            // A fence that is never closed does not count.
            string open = Lines("## 📝 日中ログ", "", "[09:00]", "```", "", "## 次");
            Assert(AddAt(open, 10, 0, "x", new NowDoingSettings()) == Lines("## 📝 日中ログ", "", "[09:00]", "```", "", "[10:00]", "📝 x", "", "## 次"));
        });
        Check("Only full paths are accepted for the diary", () => {
            var day = new DateTime(2026, 11, 24);
            Assert(NowDoingNote.NotePath(@"C:notes\{yyyy}.md", day) == null && NowDoingNote.NotePath(@"\notes\{yyyy}.md", day) == null);
            Assert(NowDoingNote.NotePath(@"\\server\share\{yyyy}.md", day) == @"\\server\share\2026.md" && NowDoingNote.NotePath(@"D:/notes/{yyyy}.md", day) == @"D:/notes/2026.md");
        });
        Check("New lines take the line break of the lines around them", () => {
            string mixed = "---\ntags: [x]\n---\r\n## 📝 日中ログ\r\n\r\n[09:00]\r\n朝\r\n\r\n## 次\r\n";
            Assert(AddAt(mixed, 10, 0, "x", new NowDoingSettings()) == mixed.Replace("朝\r\n\r\n## 次", "朝\r\n\r\n[10:00]\r\n📝 x\r\n\r\n## 次"));
        });
        Check("Without a place for the backup nothing is written", () => {
            string diary = Path.Combine(dir, "nobackup.md");
            File.WriteAllText(diary, note);
            string blocker = Path.Combine(dir, "blocker.txt");
            File.WriteAllText(blocker, "a file where the backup folder would be");
            Assert(NowDoingNote.Write(at("nobackup.md"), opened, "x", Path.Combine(blocker, "now-doing-last.md")) == NowDoingFailure.NoBackup && File.ReadAllText(diary) == note);
            Assert(NowDoingNote.Write(at("nobackup.md"), opened, "x", null) == NowDoingFailure.NoBackup && File.ReadAllText(diary) == note);
            Assert(NowDoingNote.Message(NowDoingFailure.NoBackup, null) == "書く前の控えを残せないため、書きませんでした");
            Assert(NowDoingNote.Message(NowDoingFailure.WriteFailed, null) == "書き込みが途中で失敗しました。日誌を確かめてください（書く前の中身は now-doing-last.md）");
        });
        Check("A Claudian message nothing took is removed when the window opens after two minutes", () => {
            string message = Path.Combine(dir, "stale", "claudian-message.json");
            Directory.CreateDirectory(Path.GetDirectoryName(message));
            File.WriteAllText(message, "{}");
            DateTime written = File.GetLastWriteTimeUtc(message);
            NowDoingNote.DropStaleMessage(message, written.AddSeconds(119));
            Assert(File.Exists(message));
            NowDoingNote.DropStaleMessage(message, written.AddSeconds(121));
            Assert(!File.Exists(message));
            NowDoingNote.DropStaleMessage(message, written.AddSeconds(121));
            NowDoingNote.DropStaleMessage(null, DateTime.UtcNow);
        });
        Check("A settings.ini that is not UTF-8 is never rewritten", () => {
            string ini = Path.Combine(dir, "sjis.ini");
            byte[] sjis = { 0x6E, 0x6F, 0x77, 0x5F, 0x64, 0x6F, 0x69, 0x6E, 0x67, 0x5F, 0x6E, 0x6F, 0x74, 0x65, 0x3D, 0x43, 0x3A, 0x5C, 0x93, 0xFA, 0x8E, 0x8F, 0x0D, 0x0A };
            File.WriteAllBytes(ini, sjis);
            Assert(!new ThemeSettings(ini).Save(ThemeMode.Dark) && Convert.ToBase64String(File.ReadAllBytes(ini)) == Convert.ToBase64String(sjis));
        });
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
