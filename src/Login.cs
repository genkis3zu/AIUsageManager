using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace AIUsageManager
{
    internal interface ILoginLauncher
    { Task<string> LoginAsync(string service); }

    internal sealed class LoginLauncher : ILoginLauncher
    {
        internal static ProcessStartInfo StartInfo(string service, string profile, string localAppData, string searchPath)
        {
            if (service != "Codex" && service != "Grok") throw new ArgumentException("Unsupported login service");
            string executable = service == "Grok" ? "grok.exe" : "codex.exe";
            var candidates = new List<string>();
            candidates.Add(service == "Grok" ? Path.Combine(profile, ".grok", "bin", executable) : Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", executable));
            foreach (string item in (searchPath ?? "").Split(';'))
            {
                string directory = item.Trim().Trim('"');
                // Do not search the current project or relative PATH entries for executables.
                if (directory.Length < 3 || !Path.IsPathRooted(directory) || directory[1] != ':' || directory[2] != '\\') continue;
                try {
                    candidates.Add(Path.Combine(directory, executable));
                    if(service=="Codex") {
                        foreach(string architecture in new[]{"x64","arm64"}) {
                            string triple=architecture=="x64"?"x86_64-pc-windows-msvc":"aarch64-pc-windows-msvc";
                            string package="codex-win32-"+architecture;
                            foreach(string packageRoot in new[]{Path.Combine(directory,"node_modules","@openai",package),Path.Combine(directory,"node_modules","@openai","codex","node_modules","@openai",package),Path.Combine(directory,"node_modules","@openai","codex")})
                                foreach(string bin in new[]{"bin","codex"})candidates.Add(Path.Combine(packageRoot,"vendor",triple,bin,executable));
                        }
                    }
                } catch (ArgumentException) { }
            }
            foreach (string path in candidates)
            {
                if (!File.Exists(path)) continue;
                return new ProcessStartInfo(Path.GetFullPath(path), service == "Grok" ? "login --oauth" : "login") {
                    UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal, WorkingDirectory = profile
                };
            }
            return null;
        }

        public Task<string> LoginAsync(string service)
        {
            return Task.Run(() => {
                try {
                    var start = StartInfo(service, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetEnvironmentVariable("PATH"));
                    if (start == null) return service + " CLI が見つかりません。公式CLIをインストールしてから、もう一度ログインしてください。";
                    // Interactive OAuth belongs to the official CLI. Never capture its output or tokens.
                    using (var process = Process.Start(start)) {
                        if (process == null) return "ログイン画面を起動できませんでした。";
                        process.WaitForExit();
                        return process.ExitCode == 0 ? null : "ログインが完了しませんでした。もう一度ログインしてください。";
                    }
                } catch { return "ログイン画面を起動できませんでした。公式CLIのインストール状態を確認してください。"; }
            });
        }
    }
}
