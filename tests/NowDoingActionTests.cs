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
                    using (Process child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--child sleep") { UseShellExecute = false, CreateNoWindow = true }))
                        File.WriteAllText(args[2], child.Id.ToString(CultureInfo.InvariantCulture));
                    Thread.Sleep(60000);
                    return 0;
                default: return 99;
            }
        }
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
            Assert(Ended(int.Parse(File.ReadAllText(file), CultureInfo.InvariantCulture), 2000));
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
                Assert(Ended(int.Parse(File.ReadAllText(file), CultureInfo.InvariantCulture), 2000));
            }
        });
    }

    static void RunNowDoingActionTests()
    {
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
