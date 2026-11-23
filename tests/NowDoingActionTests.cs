using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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

    static void RunNowDoingActionTests()
    {
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
