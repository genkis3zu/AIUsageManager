using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using AIUsageManager;

internal static partial class Tests
{
    static void RunSnapshotTests()
    {
        Check("App location is versioned JSON and the latest path wins",()=> {
            string dir=Path.Combine(Path.GetTempPath(),"aum-loc-"+Guid.NewGuid().ToString("N")),file=Path.Combine(dir,"app-location.json");
            try {
                Assert(AppLocation.Save(file,@"C:\Old\AIUsageManager.exe") && AppLocation.Save(file,@"D:\Apps\AIUsageManager.exe"));
                var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
                Assert((int)data["schemaVersion"]==1 && (string)data["path"]==@"D:\Apps\AIUsageManager.exe" && data.Count==2 && !File.Exists(file+".tmp"));
            } finally {try{Directory.Delete(dir,true);}catch{}}
        });
        Check("App location failure is reported, not thrown",()=> {
            string blocker=Path.GetTempFileName();
            try {Assert(!AppLocation.Save(Path.Combine(blocker,"app-location.json"),@"C:\Apps\AIUsageManager.exe"));}
            finally {File.Delete(blocker);}
        });
        Check("App location default is under the user's local app data",()=> {
            Assert(AppLocation.DefaultPath()==Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AIUsageManager","app-location.json"));
        });
        Check("Snapshot carries a recent pace basis and drops it when stale",()=> {
            var values=WidgetCards.Sample(T);values[1].Source="オンライン";values[1].Windows[0].Percent=36;
            values[1].Windows[0].Pace=Forecast(Routine(T.AddDays(-5),120,t=>0.3));
            var current=UsageSnapshot.Create(values,T,false);var stale=UsageSnapshot.Create(values,T.AddMinutes(16),false);
            Assert(current.Contains("90%まで約") && current.Contains("平均誤差") && !stale.Contains("90%まで約") && !stale.Contains("平均誤差"));
        });
        Check("Snapshot preserves missing, zero, timestamps and warning display fields",()=> {
            var values=WidgetCards.Sample(T);values[0].Source="オンライン";values[0].Windows[0].Percent=0;values[0].Windows[1].Percent=100;values[1].Windows[0].Percent=null;
            string json=UsageSnapshot.Create(values,T,false);
            var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
            Assert((int)data["schemaVersion"]==2 && (bool)data["connected"]);
            Assert(json.Contains("\"percent\":0") && json.Contains("\"percent\":null") && json.Contains("2026-10-10T09:00:00") && json.Contains("上限に達しています"));
            Assert(!json.Contains("data:image") && !json.Contains("auth") && !json.Contains("token"));
        });
        Check("Snapshot v2 carries span and short labels for every returned window",()=> {
            var values=WidgetCards.Sample(T);
            values[0].Windows.Clear();values[0].Windows.Add(UsageWindow.Week(40,T.AddDays(3)));values[0].Windows.Add(UsageWindow.Scoped("Fable",70,T.AddDays(3)));
            values[1].Windows.Insert(0,UsageWindow.FiveHour(12,T.AddHours(3)));
            string json=UsageSnapshot.Create(values,T,false);
            Assert(json.Contains("\"schemaVersion\":2") && json.Contains("\"label\":\"週（Fable）\"") && json.Contains("\"shortLabel\":\"Fable\""));
            Assert(json.Contains("\"span\":\"week\"") && json.Contains("\"span\":\"5h\"") && json.Contains("\"shortLabel\":\"5h\""));
            var claude=((object[])((Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(json))["services"])[0] as Dictionary<string,object>;
            Assert(((object[])claude["windows"]).Length==2 && UsageSnapshot.Disconnected().Contains("\"schemaVersion\":2"));
        });
        Check("Snapshot drops expired warning messages while retaining original usage",()=> {
            var values=WidgetCards.Sample(T);values[0].Source="オンライン";values[0].Windows[1].Percent=100;
            new UsageAlerts().Observe(values,T,NotificationMode.Off);
            string json=UsageSnapshot.Create(values,T.AddMinutes(16),false);
            Assert(json.Contains("\"percent\":100") && !json.Contains("上限に達しています"));
        });
        Check("Snapshot pipe ACL grants current user and denies network logons",()=> {
            var acl=SnapshotServer.CreateSecurity();bool owner=false,network=false;
            using(var identity=WindowsIdentity.GetCurrent())foreach(PipeAccessRule rule in acl.GetAccessRules(true,false,typeof(SecurityIdentifier))) {
                if(rule.IdentityReference.Equals(identity.User)&&rule.AccessControlType==AccessControlType.Allow)owner=true;
                if(rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid,null))&&rule.AccessControlType==AccessControlType.Deny)network=true;
            }
            Assert(owner&&network);
        });
        Check("Snapshot pipe delivers changing in-memory values and stops without hanging",()=> {
            string name="AIUsageManager.Tests."+Guid.NewGuid().ToString("N");
            using(var server=new SnapshotServer(name)) {
                server.Publish(UsageSnapshot.Create(WidgetCards.Sample(T),T,false));
                var first=SnapshotClient.ReadAsync(name).GetAwaiter().GetResult();Assert(first.Contains("\"connected\":true"));
                server.Publish(UsageSnapshot.Disconnected());
                Assert(SnapshotClient.ReadAsync(name).GetAwaiter().GetResult().Contains("\"connected\":false"));
            }
            Assert(SnapshotClient.ReadAsync(name).GetAwaiter().GetResult()==UsageSnapshot.Disconnected());
        });
        Check("Snapshot client rejects oversized output from a pipe",()=> {
            string name="AIUsageManager.Tests."+Guid.NewGuid().ToString("N");
            using(var pipe=new NamedPipeServerStream(name,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous)) {
                var writer=Task.Run(async()=> {await pipe.WaitForConnectionAsync();try {byte[] bytes=Encoding.UTF8.GetBytes(new string('x',70000));await pipe.WriteAsync(bytes,0,bytes.Length);}catch(System.IO.IOException){} });
                Assert(SnapshotClient.ReadAsync(name).GetAwaiter().GetResult()==UsageSnapshot.Disconnected());
                Assert(writer.Wait(3000));
            }
        });
        Check("Snapshot client bounds a connected but silent server",()=> {
            string name="AIUsageManager.Tests."+Guid.NewGuid().ToString("N");
            using(var pipe=new NamedPipeServerStream(name,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous)) {
                var connected=pipe.WaitForConnectionAsync();var before=DateTime.UtcNow;
                Assert(SnapshotClient.ReadAsync(name).GetAwaiter().GetResult()==UsageSnapshot.Disconnected());
                Assert(connected.IsCompleted && DateTime.UtcNow-before<TimeSpan.FromSeconds(5));
            }
        });
    }
}
