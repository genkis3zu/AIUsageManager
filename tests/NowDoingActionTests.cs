using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using AIUsageManager;

internal static partial class Tests
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

    // What Windows (and the C runtime of most programs) gives a program for this command line.
    static string[] WindowsArguments(string commandLine)
    {
        int count;
        IntPtr list = CommandLineToArgvW(commandLine, out count);
        if (list == IntPtr.Zero) throw new Exception("CommandLineToArgvW failed");
        try
        {
            var result = new string[count];
            for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, i * IntPtr.Size));
            return result;
        }
        finally { LocalFree(list); }
    }

    // This test program run as a script by the script tests: `--child <mode> …`.
    static int Child(string[] args)
    {
        string mode = args.Length > 1 ? args[1] : "";
        using (Stream output = Console.OpenStandardOutput())
        using (Stream error = Console.OpenStandardError())
        {
            Action<Stream, string> write = (stream, text) => { byte[] b = new UTF8Encoding(false).GetBytes(text); stream.Write(b, 0, b.Length); stream.Flush(); };
            switch (mode)
            {
                case "args": write(output, new JavaScriptSerializer().Serialize(args.Skip(2).ToArray())); return 0;
                case "cwd": write(output, Environment.CurrentDirectory); return 0;
                case "crlf": write(output, "一行目\r\n二行目\r\n\r\n"); return 0;
                case "badutf8": output.WriteByte(0xFF); write(output, "ok"); return 0;
                case "big": write(output, new string('x', int.Parse(args[2], CultureInfo.InvariantCulture))); return 0;
                case "lines": write(output, string.Concat(Enumerable.Repeat("l\n", int.Parse(args[2], CultureInfo.InvariantCulture)))); return 0;
                case "fail": write(error, "\n  理由の1行目\n2行目"); return 3;
                case "exit": return int.Parse(args[2], CultureInfo.InvariantCulture);
                case "sleep": Thread.Sleep(60000); return 0;
                case "spawn-sleep":
                    // The started child's id, then this script's own id.
                    using (Process child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--child sleep") { UseShellExecute = false, CreateNoWindow = true }))
                        File.WriteAllText(args[2], child.Id.ToString(CultureInfo.InvariantCulture) + "\n" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                    Thread.Sleep(60000);
                    return 0;
                case "start-and-exit":
                    // Starts a script that keeps running, then ends this program without stopping it (as the window does when it closes).
                    NowDoingScript.RunAsync(NowDoingCommand.Quote(Assembly.GetExecutingAssembly().Location) + " --child spawn-sleep " + NowDoingCommand.Quote(args[2]), null,
                        Path.GetDirectoryName(args[2]), new NowDoingScriptLimits { TimeoutMs = 60000, MaxBytes = 4096, MaxLines = 20 }, CancellationToken.None);
                    for (var clock = Stopwatch.StartNew(); !File.Exists(args[2]) && clock.ElapsedMilliseconds < 10000; ) Thread.Sleep(50);
                    Thread.Sleep(200);
                    return 0;
                default: return 99;
            }
        }
    }

    // The process ids spawn-sleep wrote: the started child's, then the script's own.
    static int[] Pids(string file)
    {
        return File.ReadAllText(file).Split('\n').Select(p => int.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray();
    }

    static bool Ended(int pid, int withinMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < withinMs)
        {
            try { using (Process p = Process.GetProcessById(pid)) if (p.HasExited) return true; }
            catch (ArgumentException) { return true; }
            Thread.Sleep(50);
        }
        return false;
    }

    static void RunNowDoingScriptTests()
    {
        string self = Assembly.GetExecutingAssembly().Location;
        Func<string, string> child = mode => NowDoingCommand.Quote(self) + " --child " + mode;
        string temp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "now-doing-script-test");
        Directory.CreateDirectory(temp);
        Func<string, IList<string>, NowDoingScriptLimits, NowDoingScriptResult> run = (command, arguments, limits) =>
            NowDoingScript.RunAsync(command, arguments, temp, limits, CancellationToken.None).Result;
        Check("Arguments reach the script as they were given", () => {
            var given = new[] { "--note", @"C:\Notes\2026年_日誌\2026-11-24 - 日誌.md", "--text", "a \"b\" \\ & | %PATH%\n二行目" };
            NowDoingScriptResult r = run(child("args"), given, NowDoingScriptLimits.After);
            Assert(r.Ok && new JavaScriptSerializer().Deserialize<string[]>(r.Output).SequenceEqual(given));
        });
        Check("The script runs in the working folder it is given", () => {
            NowDoingScriptResult r = run(child("cwd"), new string[0], NowDoingScriptLimits.After);
            Assert(r.Ok && string.Equals(r.Output.TrimEnd('\\'), temp.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        });
        Check("Output is read as UTF-8 with LF line breaks and no line break at the end", () => {
            NowDoingScriptResult r = run(child("crlf"), null, NowDoingScriptLimits.After);
            Assert(r.Ok && r.Output == "一行目\n二行目" && r.FirstLine == "一行目");
            r = run(child("badutf8"), null, NowDoingScriptLimits.After);
            Assert(r.Ok && r.Output == "\uFFFDok");
        });
        Check("Too much output fails", () => {
            Assert(run(child("big 4096"), null, NowDoingScriptLimits.After).Ok);
            NowDoingScriptResult r = run(child("big 4097"), null, NowDoingScriptLimits.After);
            Assert(!r.Ok && r.Reason == "出力が多すぎます");
            Assert(run(child("lines 20"), null, NowDoingScriptLimits.After).Ok && !run(child("lines 21"), null, NowDoingScriptLimits.List).Ok);
            Assert(run(child("big 32768"), null, NowDoingScriptLimits.Copy).Ok && !run(child("big 32769"), null, NowDoingScriptLimits.Copy).Ok);
            Assert(run(child("lines 100"), null, NowDoingScriptLimits.Copy).Ok);
        });
        Check("A script that runs too long is ended with its children", () => {
            string file = Path.Combine(temp, "timeout.pid");
            File.Delete(file);
            NowDoingScriptResult r = run(child("spawn-sleep " + NowDoingCommand.Quote(file)), null, new NowDoingScriptLimits { TimeoutMs = 1000, MaxBytes = 4096, MaxLines = 20 });
            Assert(!r.Ok && r.Reason == "時間切れ（1秒）");
            Assert(Pids(file).All(pid => Ended(pid, 2000)));
        });
        Check("A script is ended when the program that started it ends", () => {
            string file = Path.Combine(temp, "orphan.pid");
            File.Delete(file);
            using (Process starter = Process.Start(new ProcessStartInfo(self, "--child start-and-exit " + NowDoingCommand.Quote(file)) { UseShellExecute = false, CreateNoWindow = true }))
                Assert(starter.WaitForExit(15000));
            Assert(File.Exists(file) && Pids(file).All(pid => Ended(pid, 3000)));
        });
        Check("A cancelled script ends even when its cancellation is disposed at once", () => {
            string file = Path.Combine(temp, "disposed.pid");
            File.Delete(file);
            var cancel = new CancellationTokenSource();
            Task<NowDoingScriptResult> task = NowDoingScript.RunAsync(child("spawn-sleep " + NowDoingCommand.Quote(file)), null, temp, new NowDoingScriptLimits { TimeoutMs = 60000, MaxBytes = 4096, MaxLines = 20 }, cancel.Token);
            cancel.Cancel(); cancel.Dispose();
            Assert(task.Wait(10000) && !task.IsFaulted && !task.Result.Ok && task.Result.Reason == "中止しました");
            if (File.Exists(file)) Assert(Pids(file).All(pid => Ended(pid, 2000)));
        });
        Check("Batch files are refused, since Windows runs them through cmd", () => {
            foreach (string batch in new[] { "x.bat --a", "\"C:\\My Scripts\\after.CMD\"", @"C:\s\a.Bat" })
            {
                NowDoingScriptResult r = run(batch, new[] { "--text", "Q&A" }, NowDoingScriptLimits.After);
                Assert(!r.Ok && r.Reason == "バッチファイル（.bat・.cmd）は使えません");
            }
        });
        Check("Only processes started after the script are ended with it, even when Windows reuses a process id", () => {
            var start = new DateTime(2026, 11, 24, 10, 0, 0);
            var table = new Dictionary<int, ProcessEntry> {
                { 100, new ProcessEntry { Parent = 1 } }, { 200, new ProcessEntry { Parent = 100 } }, { 300, new ProcessEntry { Parent = 100 } },
                { 400, new ProcessEntry { Parent = 300 } }, { 500, new ProcessEntry { Parent = 200 } }, { 600, new ProcessEntry { Parent = 100 } } };
            var started = new Dictionary<int, DateTime> { { 200, start.AddSeconds(1) }, { 300, start.AddSeconds(-10) }, { 400, start.AddSeconds(5) }, { 500, start.AddSeconds(2) } };
            List<int> found = NowDoingScript.Descendants(100, start, table, id => started.ContainsKey(id) ? started[id] : (DateTime?)null);
            Assert(found.OrderBy(i => i).SequenceEqual(new[] { 200, 500 }));
        });
        Check("Failures say why", () => {
            Assert(run(child("fail"), null, NowDoingScriptLimits.After).Reason == "理由の1行目");
            Assert(run(child("exit 4"), null, NowDoingScriptLimits.After).Reason == "終了コード 4");
            NowDoingScriptResult r = run("no-such-program-x --a", null, NowDoingScriptLimits.After);
            Assert(!r.Ok && r.Reason == "起動できません（no-such-program-x）");
            Assert(run("\"a", null, NowDoingScriptLimits.After).Reason == "命令の書き方が違います");
            r = NowDoingScript.RunAsync(child("cwd"), null, Path.Combine(temp, "none"), NowDoingScriptLimits.After, CancellationToken.None).Result;
            Assert(!r.Ok && r.Reason == "作業フォルダーがありません");
        });
        Check("Cancelling ends the script and its children", () => {
            string file = Path.Combine(temp, "cancel.pid");
            File.Delete(file);
            using (var cancel = new CancellationTokenSource())
            {
                Task<NowDoingScriptResult> task = NowDoingScript.RunAsync(child("spawn-sleep " + NowDoingCommand.Quote(file)), null, temp, new NowDoingScriptLimits { TimeoutMs = 60000, MaxBytes = 4096, MaxLines = 20 }, cancel.Token);
                var clock = Stopwatch.StartNew();
                while (!File.Exists(file) && clock.ElapsedMilliseconds < 10000) Thread.Sleep(50);
                Thread.Sleep(100);
                cancel.Cancel();
                Assert(task.Wait(5000) && !task.Result.Ok && task.Result.Reason == "中止しました");
                Assert(Pids(file).All(pid => Ended(pid, 2000)));
            }
        });
    }

    // Feeds key events to a detector: L/R = left/right Ctrl, S/T = left/right Shift, X = any other key; + is down,
    // - is up, the number is the time in milliseconds. Returns how many double presses were found.
    static int Taps(DoubleTapKey key, string events)
    {
        var detector = new DoubleTapDetector(key);
        int found = 0;
        foreach (string e in events.Split(' '))
        {
            TapKey k = e[0] == 'L' ? TapKey.LeftCtrl : e[0] == 'R' ? TapKey.RightCtrl : e[0] == 'S' ? TapKey.LeftShift : e[0] == 'T' ? TapKey.RightShift
                : e[0] == 'A' ? TapKey.LeftAlt : e[0] == 'B' ? TapKey.RightAlt : TapKey.Other;
            if (detector.Observe(k, e[1] == '+', uint.Parse(e.Substring(2), CultureInfo.InvariantCulture))) found++;
        }
        return found;
    }

    static void RunDoubleTapTests()
    {
        Check("Each double-press setting finds its own key pressed twice", () => {
            Assert(Taps(DoubleTapKey.Ctrl, "L+0 L-100 L+500 L-600") == 1 && Taps(DoubleTapKey.LeftCtrl, "L+0 L-100 L+500 L-600") == 1);
            Assert(Taps(DoubleTapKey.RightCtrl, "R+0 R-100 R+500 R-600") == 1);
            Assert(Taps(DoubleTapKey.Shift, "S+0 S-100 S+500 S-600") == 1 && Taps(DoubleTapKey.LeftShift, "S+0 S-100 S+500 S-600") == 1);
            Assert(Taps(DoubleTapKey.RightShift, "T+0 T-100 T+500 T-600") == 1 && Taps(DoubleTapKey.Shift, "T+0 T-100 T+500 T-600") == 1);
            Assert(Taps(DoubleTapKey.Ctrl, "S+0 S-100 S+500 S-600") == 0 && Taps(DoubleTapKey.Shift, "L+0 L-100 L+500 L-600") == 0 && Taps(DoubleTapKey.None, "L+0 L-100 L+500 L-600") == 0);
        });
        Check("Alt pressed twice is found for the Alt settings, telling left and right apart only when asked", () => {
            Assert(Taps(DoubleTapKey.RightAlt, "B+0 B-100 B+500 B-600") == 1 && Taps(DoubleTapKey.RightAlt, "A+0 A-100 A+500 A-600") == 0);
            Assert(Taps(DoubleTapKey.LeftAlt, "A+0 A-100 A+500 A-600") == 1 && Taps(DoubleTapKey.Alt, "A+0 A-100 B+200 B-300") == 1);
            // Alt+Tab and Ctrl or Shift with Alt are not a double press.
            Assert(Taps(DoubleTapKey.RightAlt, "B+0 X+50 B-100 B+200 B-300") == 0 && Taps(DoubleTapKey.Ctrl, "L+0 L-100 B+150 L+200 L-300") == 0);
        });
        Check("Presses up to 300 ms and a gap up to 400 ms count", () => {
            Assert(Taps(DoubleTapKey.Ctrl, "L+0 L-300 L+700 L-1000") == 1);
            Assert(Taps(DoubleTapKey.Ctrl, "L+0 L-301 L+701 L-801") == 0);
            Assert(Taps(DoubleTapKey.Ctrl, "L+0 L-100 L+200 L-501") == 0);
            Assert(Taps(DoubleTapKey.Ctrl, "L+0 L-100 L+501 L-600") == 0);
        });
        Check("Another key in between or while held starts again", () => {
            Assert(Taps(DoubleTapKey.Ctrl, "L+0 L-100 X+200 L+300 L-400") == 0);
            Assert(Taps(DoubleTapKey.Ctrl, "L+0 X+50 L-100 L+200 L-300 L+400 L-500") == 1);
            Assert(Taps(DoubleTapKey.Shift, "S+0 X+20 S-60 S+100 S-150") == 0);
            // Letting go of another key is not pressing one.
            Assert(Taps(DoubleTapKey.Ctrl, "L+0 L-100 X-150 L+200 L-300") == 1);
        });
        Check("Key repeat while held counts as one press", () => {
            Assert(Taps(DoubleTapKey.Ctrl, "L+0 L+30 L+60 L-100 L+300 L+330 L-400") == 1);
        });
        Check("Left and right are told apart only when the setting says so", () => {
            Assert(Taps(DoubleTapKey.Ctrl, "L+0 L-100 R+200 R-300") == 1);
            Assert(Taps(DoubleTapKey.LeftCtrl, "L+0 L-100 R+200 R-300") == 0 && Taps(DoubleTapKey.LeftCtrl, "R+0 R-100 R+200 R-300") == 0);
            Assert(Taps(DoubleTapKey.Ctrl, "L+0 R+50 R-80 L-100 L+200 L-300") == 0);
        });
        Check("Three presses find it once, and the clock wrapping around does not matter", () => {
            Assert(Taps(DoubleTapKey.Ctrl, "L+0 L-50 L+100 L-150 L+200 L-250") == 1);
            Assert(Taps(DoubleTapKey.Ctrl, "L+4294967200 L-4294967250 L+4294967290 L-44") == 1);
        });
        Check("Only the left and right Ctrl, Shift and Alt keys are told apart", () => {
            Assert(DoubleTapHook.Classify(0xA2) == TapKey.LeftCtrl && DoubleTapHook.Classify(0xA3) == TapKey.RightCtrl);
            Assert(DoubleTapHook.Classify(0xA0) == TapKey.LeftShift && DoubleTapHook.Classify(0xA1) == TapKey.RightShift);
            Assert(DoubleTapHook.Classify(0xA4) == TapKey.LeftAlt && DoubleTapHook.Classify(0xA5) == TapKey.RightAlt);
            Assert(DoubleTapHook.Classify(0x41) == TapKey.Other && DoubleTapHook.Classify(0x11) == TapKey.Other && DoubleTapHook.Classify(0x12) == TapKey.Other && DoubleTapHook.Classify(0x5B) == TapKey.Other);
        });
    }

    static void RunNowDoingOffTests()
    {
        var opened = new DateTime(2026, 11, 24, 10, 0, 0);
        var page = new NowDoingView { Path = @"C:\Notes\2026-11-24.md" };
        string full = "now_doing_claudian=on\nnow_doing_after=python a.py\nnow_doing_list=python l.py\nnow_doing_button1=x|text:y\nnow_doing_button2=bad\n";
        Check("Without the settings the window runs no script, sends nothing and shows no buttons", () => {
            NowDoingActions none = NowDoingForm.Build(NowDoingSettings.Parse("now_doing_note=C:\\Notes\\{yyyy-MM-dd}.md\n"), page, opened, @"C:\b.md", @"C:\m.json", @"C:\App");
            Assert(none.Write != null && none.After == null && none.List == null && none.Send == null && none.Buttons.All(b => b == null) && none.Notice == null);
        });
        Check("Each setting turns on only its own part of the window", () => {
            NowDoingActions all = NowDoingForm.Build(NowDoingSettings.Parse(full), page, opened, @"C:\b.md", @"C:\m.json", @"C:\App");
            Assert(all.After != null && all.List != null && all.Send != null && all.Buttons[0] != null && all.Notice == "settings.ini の now_doing_button2 の書き方が違います");
            NowDoingActions after = NowDoingForm.Build(NowDoingSettings.Parse("now_doing_after=python a.py\n"), page, opened, @"C:\b.md", @"C:\m.json", @"C:\App");
            Assert(after.After != null && after.List == null && after.Send == null);
        });
        Check("Without a diary page no script runs and nothing is sent, even when they are set", () => {
            NowDoingActions nopage = NowDoingForm.Build(NowDoingSettings.Parse(full), new NowDoingView(), opened, @"C:\b.md", @"C:\m.json", @"C:\App");
            Assert(nopage.After == null && nopage.List == null && nopage.Send == null);
        });
    }

    static void RunNowDoingActionTests()
    {
        RunNowDoingOffTests();
        RunDoubleTapTests();
        RunNowDoingScriptTests();
        Check("Buttons are read in their three forms", () => {
            NowDoingButton b = NowDoingButton.Parse(1, "🚶 散歩|record:🚶 散歩 ✅");
            Assert(b.Number == 1 && b.Kind == NowDoingButtonKind.Record && b.Label == "🚶 散歩" && b.Value == "🚶 散歩 ✅" && b.App == null);
            b = NowDoingButton.Parse(5, "⏱️ 作業|text:⏱️ ");
            Assert(b.Kind == NowDoingButtonKind.Text && b.Value == "⏱️ ");
            b = NowDoingButton.Parse(7, @"📈 依頼文|copy:python C:\Notes\scripts\request.py|open:claude://");
            Assert(b.Kind == NowDoingButtonKind.Copy && b.Value == @"python C:\Notes\scripts\request.py" && b.App == "claude");
            b = NowDoingButton.Parse(7, @"📈 依頼文|copy:python C:\Notes\scripts\request.py");
            Assert(b.Kind == NowDoingButtonKind.Copy && b.Value == @"python C:\Notes\scripts\request.py" && b.App == null);
            Assert(NowDoingButton.Parse(2, "x|copy:a|open:grokbot://").App == "grokbot");
        });
        Check("Buttons written wrongly are not made", () => {
            foreach (string bad in new[] { "名前だけ", "|text:x", "  |text:x", "x|run:y", "x|record:", "x|record:  ", "x|text:", "x|copy:", "x|copy:a|open:https://example.com", "x|copy:a|open:", "x|copy:\"a", "", null })
                Assert(NowDoingButton.Parse(1, bad) == null);
        });
        Check("settings.ini keeps the trailing space of a text button and names the wrong buttons", () => {
            var s = NowDoingSettings.Parse("now_doing_button5=⏱️ 作業|text:⏱️ \r\nnow_doing_button2=だめ\nnow_doing_button10=x|text:y\nnow_doing_button0=x|text:y\n");
            Assert(s.Buttons.Length == 9 && s.Buttons[4].Value == "⏱️ " && s.Buttons[4].Number == 5 && s.Buttons[1] == null);
            Assert(s.Buttons.Count(x => x != null) == 1 && s.BadButtons.SequenceEqual(new[] { 2 }));
            Assert(NowDoingButton.Problem(new[] { 2 }) == "settings.ini の now_doing_button2 の書き方が違います");
            Assert(NowDoingButton.Problem(new[] { 2, 5 }) == "settings.ini の now_doing_button2・now_doing_button5 の書き方が違います");
            Assert(NowDoingButton.Problem(new int[0]) == null && NowDoingButton.Problem(null) == null);
        });
        Check("Long button names are cut at 8 characters", () => {
            Assert(NowDoingButton.Parse(1, "🗒️ 議事の記録をする|text:x").ShortLabel == "🗒️ 議事の記録を");
            Assert(NowDoingButton.Parse(1, "作業|text:x").ShortLabel == "作業");
        });
        Check("Script settings and the AI done keys are read", () => {
            var s = NowDoingSettings.Parse("now_doing_after=python -X utf8 scripts\\now_doing\\after.py\nnow_doing_list=python list.py\nnow_doing_workdir=D:\\Vault\nnow_doing_ai_done=on\nnow_doing_ai_done_exclude= D:\\a; D:\\b ;\n");
            Assert(s.After == "python -X utf8 scripts\\now_doing\\after.py" && s.List == "python list.py" && s.Workdir == "D:\\Vault");
            Assert(s.AiDone && s.AiDoneExclude.SequenceEqual(new[] { "D:\\a", "D:\\b" }));
            var none = NowDoingSettings.Parse("now_doing_ai_done=yes\n");
            Assert(!none.AiDone && none.AiDoneExclude.Length == 0 && none.After == null && none.List == null && none.Workdir == null);
        });
        Check("Commands are split into the program and its arguments", () => {
            Assert(NowDoingCommand.Split("python -X utf8 \"C:\\My Notes\\after.py\"").SequenceEqual(new[] { "python", "-X", "utf8", @"C:\My Notes\after.py" }));
            Assert(NowDoingCommand.Split(@"C:\x\a.exe").SequenceEqual(new[] { @"C:\x\a.exe" }));
            Assert(NowDoingCommand.Split("\t\"C:\\Program Files\\py.exe\"\t-3  x ").SequenceEqual(new[] { @"C:\Program Files\py.exe", "-3", "x" }));
            Assert(NowDoingCommand.Split("  ") == null && NowDoingCommand.Split("") == null && NowDoingCommand.Split(null) == null && NowDoingCommand.Split("\"a b") == null);
        });
        Check("Arguments survive Windows command-line parsing unchanged", () => {
            var args = new List<string> { "", "a b", "a\"b", "a\\\"b", @"C:\dir\", @"C:\dir with space\", "一行目\n二行目", "& | < > ^ %PATH%", "🚶 散歩 ✅", @"\\server\share", "\t", "\"", "a\\\\" };
            string[] back = WindowsArguments("x " + NowDoingCommand.Join(args));
            Assert(back.Skip(1).SequenceEqual(args));
            Assert(NowDoingCommand.Quote("plain") == "plain" && NowDoingCommand.Quote("") == "\"\"");
        });
    }
}
