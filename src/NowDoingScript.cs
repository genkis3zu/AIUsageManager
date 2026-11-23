using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace AIUsageManager
{
    // How long a script may run and how much it may print.
    internal sealed class NowDoingScriptLimits
    {
        public int TimeoutMs, MaxBytes, MaxLines;
        // The after script (now_doing_after) and a button's copy: 10 s. The list script (now_doing_list): 3 s.
        public static readonly NowDoingScriptLimits After = new NowDoingScriptLimits { TimeoutMs = 10000, MaxBytes = 4096, MaxLines = 20 };
        public static readonly NowDoingScriptLimits List = new NowDoingScriptLimits { TimeoutMs = 3000, MaxBytes = 4096, MaxLines = 20 };
        // A request text is longer than 20 lines, so copy only has a size limit.
        public static readonly NowDoingScriptLimits Copy = new NowDoingScriptLimits { TimeoutMs = 10000, MaxBytes = 32768, MaxLines = int.MaxValue };
    }

    internal sealed class NowDoingScriptResult
    {
        public bool Ok;
        // Standard output as UTF-8, with LF line breaks and no line break at the end.
        public string Output = "";
        // Why it failed, for the window.
        public string Reason;
        public string FirstLine { get { int lf = (Output ?? "").IndexOf('\n'); return lf < 0 ? Output ?? "" : Output.Substring(0, lf); } }
    }

    // Runs a command from settings.ini without a shell and without a window. The program gets the arguments exactly
    // as given (the typed text is one argument, never part of the command text).
    internal static class NowDoingScript
    {
        const int MaxErrorBytes = 4096, MaxReason = 100;

        static Task<NowDoingScriptResult> Fail(string reason)
        {
            var done = new TaskCompletionSource<NowDoingScriptResult>();
            done.SetResult(new NowDoingScriptResult { Reason = reason });
            return done.Task;
        }

        // Never throws: every problem comes back as a result that is not Ok, with the reason.
        public static Task<NowDoingScriptResult> RunAsync(string command, IList<string> arguments, string workdir, NowDoingScriptLimits limits, CancellationToken cancel)
        {
            string[] words = NowDoingCommand.Split(command);
            if (words == null) return Fail("命令の書き方が違います");
            try { if (string.IsNullOrEmpty(workdir) || !Directory.Exists(workdir)) return Fail("作業フォルダーがありません"); }
            catch (ArgumentException) { return Fail("作業フォルダーがありません"); }
            var info = new ProcessStartInfo(words[0], NowDoingCommand.Join(words.Skip(1).Concat(arguments ?? new string[0])))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workdir,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            Process process;
            try { process = Process.Start(info); }
            catch (Win32Exception) { process = null; }
            catch (InvalidOperationException) { process = null; }
            catch (IOException) { process = null; }
            catch (ArgumentException) { process = null; }
            if (process == null) return Fail("起動できません（" + words[0] + "）");
            return Task.Run(() => Wait(process, limits, cancel));
        }

        // The process handle, to wait for the process to end together with other handles.
        sealed class ExitHandle : WaitHandle
        {
            public ExitHandle(Process process) { SafeWaitHandle = new SafeWaitHandle(process.Handle, false); }
        }

        static NowDoingScriptResult Wait(Process process, NowDoingScriptLimits limits, CancellationToken cancel)
        {
            using (process)
            using (var tooMuch = new ManualResetEvent(false))
            using (var exited = new ExitHandle(process))
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { }
                var output = new MemoryStream();
                var error = new MemoryStream();
                Task reading = Task.Run(() => Read(process.StandardOutput.BaseStream, output, limits.MaxBytes, tooMuch));
                Task readingErrors = Task.Run(() => Read(process.StandardError.BaseStream, error, MaxErrorBytes, null));
                var clock = Stopwatch.StartNew();
                Func<int> left = () => (int)Math.Max(0, limits.TimeoutMs - clock.ElapsedMilliseconds);
                int which = WaitHandle.WaitAny(new[] { exited, tooMuch, cancel.WaitHandle }, left());
                string reason = null;
                if (which == 1) reason = "出力が多すぎます";
                else if (which == 2) reason = "中止しました";
                else if (which == WaitHandle.WaitTimeout) reason = TimedOut(limits);
                // A program that started another one and left it holding the output can end before the output does.
                else if (!Task.WaitAll(new[] { reading, readingErrors }, left())) reason = TimedOut(limits);
                else if (tooMuch.WaitOne(0)) reason = "出力が多すぎます";
                if (reason != null)
                {
                    EndTree(process);
                    Task.WaitAll(new[] { reading, readingErrors }, 2000);
                    return new NowDoingScriptResult { Reason = reason };
                }
                string text = Clean(output.ToArray());
                if (text.Length > 0 && text.Split('\n').Length > limits.MaxLines) return new NowDoingScriptResult { Reason = "出力が多すぎます" };
                int code = process.ExitCode;
                if (code != 0) return new NowDoingScriptResult { Reason = FirstError(error.ToArray()) ?? "終了コード " + code };
                return new NowDoingScriptResult { Ok = true, Output = text };
            }
        }

        static string TimedOut(NowDoingScriptLimits limits)
        {
            return "時間切れ（" + ((limits.TimeoutMs + 999) / 1000) + "秒）";
        }

        // Reads to the end. Up to `max` bytes are kept; past that the rest is read and dropped, and `tooMuch` is set.
        static void Read(Stream from, MemoryStream into, int max, ManualResetEvent tooMuch)
        {
            var buffer = new byte[4096];
            try
            {
                int n;
                while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
                {
                    int keep = (int)Math.Max(0, Math.Min(n, max - into.Length));
                    into.Write(buffer, 0, keep);
                    if (keep < n && tooMuch != null) tooMuch.Set();
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        static string Decode(byte[] bytes)
        {
            return new UTF8Encoding(false, false).GetString(bytes).TrimStart('﻿');
        }

        static string Clean(byte[] bytes)
        {
            return Decode(bytes).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        }

        // The first line of the error output that is not blank, at most 100 characters; null when there is none.
        static string FirstError(byte[] bytes)
        {
            foreach (string raw in Clean(bytes).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.Length > MaxReason) line = line.Substring(0, char.IsHighSurrogate(line[MaxReason - 1]) ? MaxReason - 1 : MaxReason);
                return line;
            }
            return null;
        }

        // Ends the process and the processes it started (and theirs), as far as they still run. Processes that
        // started before it are never touched, even when Windows has given one of them a reused id.
        static void EndTree(Process root)
        {
            DateTime started;
            try { started = root.StartTime; }
            catch (InvalidOperationException) { started = DateTime.MaxValue; }
            catch (Win32Exception) { started = DateTime.MaxValue; }
            var ids = new List<int> { root.Id };
            IDictionary<int, ProcessEntry> table = ProcessTree.List();
            for (int i = 0; i < ids.Count; i++)
                foreach (KeyValuePair<int, ProcessEntry> entry in table)
                    if (entry.Value.Parent == ids[i] && !ids.Contains(entry.Key)) ids.Add(entry.Key);
            try { if (!root.HasExited) root.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            foreach (int id in ids.Skip(1))
            {
                try
                {
                    using (Process p = Process.GetProcessById(id))
                        if (p.StartTime >= started) p.Kill();
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
        }
    }
}
