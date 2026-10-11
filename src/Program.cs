using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using System.IO;
using System.Text;

[assembly: AssemblyTitle("AI Usage Manager")]
[assembly: AssemblyDescription("Windows tray usage monitor for Claude, Codex and Grok")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyProduct("AI Usage Manager")]
[assembly: AssemblyCopyright("Copyright (c) 2026 AI Usage Manager contributors")]
[assembly: TargetFramework(".NETFramework,Version=v4.8")]

namespace AIUsageManager
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if(args.Length==1&&args[0]=="--obsidian-data") {
                string json=UsageSnapshot.Disconnected();
                try {json=SnapshotClient.ReadAsync(SnapshotServer.CurrentName()).GetAwaiter().GetResult();}catch { }
                using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)))output.Write(json);
                return;
            }
            // This path is an anonymous-pipe worker, independent of the visible tray instance.
            if(args.Length==1&&(args[0]=="--widget-data"||args[0]=="--widget-sample"))
            {
                ServiceUsage[] values=WidgetCards.Unavailable();
                try {
                    ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
                    if(args[0]=="--widget-sample")values=WidgetCards.Sample(DateTimeOffset.Now);
                    else using(var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(25)))using(var http=UsageService.CreateHttpClient())
                        values=new UsageService(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),http).FetchAllAsync(cancellation.Token).GetAwaiter().GetResult();
                    using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)))output.Write(WidgetCards.Create(values,DateTimeOffset.Now));
                } catch { Environment.ExitCode=1; }
                return;
            }
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            string user;
            using(var identity=WindowsIdentity.GetCurrent())user=identity.User.Value;
            bool created;
            using(var mutex=new Mutex(true,"Local\\AIUsageManager.Instance."+user,out created))
            using(var signal=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\AIUsageManager.Show."+user))
            using(var codexLogin=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\AIUsageManager.Login.Codex."+user))
            using(var grokLogin=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\AIUsageManager.Login.Grok."+user))
            using(var refreshSignal=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\AIUsageManager.Refresh."+user))
            {
                bool loginCodex=args.Length==1&&args[0]=="--login-codex", loginGrok=args.Length==1&&args[0]=="--login-grok";
                if(loginCodex)codexLogin.Set();else if(loginGrok)grokLogin.Set();
                bool refresh=args.Length==1&&args[0]=="--refresh";
                if(!created){if(refresh)refreshSignal.Set();else if(!loginCodex&&!loginGrok)signal.Set();return;}
                try {using(var snapshots=new SnapshotServer(SnapshotServer.CurrentName()))Application.Run(new TrayContext(args.Contains("--show"),signal,null,ThemeSettings.Default(),new LoginLauncher(),new[]{codexLogin,grokLogin},null,snapshots,refreshSignal));}
                finally {mutex.ReleaseMutex();}
            }
        }
    }
}
