using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("AI Usage Manager")]
[assembly: AssemblyDescription("Windows tray usage monitor for Claude, Codex and Grok")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
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
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            string user;
            using(var identity=WindowsIdentity.GetCurrent())user=identity.User.Value;
            bool created;
            using(var mutex=new Mutex(true,"Local\\AIUsageManager.Instance."+user,out created))
            using(var signal=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\AIUsageManager.Show."+user))
            {
                if(!created){signal.Set();return;}
                try {Application.Run(new TrayContext(args.Contains("--show"),signal));}
                finally {mutex.ReleaseMutex();}
            }
        }
    }
}
