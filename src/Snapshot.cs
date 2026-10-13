using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AIUsageManager
{
    public static class UsageSnapshot
    {
        static Dictionary<string,object> Map(params object[] pairs)
        { var result=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)result.Add((string)pairs[i],pairs[i+1]);return result; }
        static string Date(DateTimeOffset? value) { return value.HasValue?value.Value.ToUniversalTime().ToString("o"):null; }
        public static string Disconnected() { return "{\"schemaVersion\":1,\"connected\":false,\"services\":[]}"; }
        public static string Create(ServiceUsage[] values,DateTimeOffset now,bool refreshing)
        {
            var services=new List<object>();
            foreach(string name in new[]{"Claude","Codex","Grok"}) {
                var value=values.FirstOrDefault(v=>v.Name==name)??ServiceUsage.Empty(name,"");var windows=new List<object>();
                foreach(var window in value.Windows) {
                    var alert=UsageAlerts.ForWindow(value,window,now);
                    var pace=UsageAlerts.PaceForWindow(value,window,now);
                    windows.Add(Map("label",window.Label,"percent",window.Percent,"resetsAt",Date(window.ResetsAt),"pace",pace==null?null:Map("summary",pace.Summary,"detail",pace.Detail),"alert",alert==null?null:Map(
                        "title",alert.Title,"detail",alert.Detail,"threshold",alert.Threshold,"rapidIncrease",alert.RapidIncrease,"forecastAt",Date(alert.ForecastAt))));
                }
                services.Add(Map("name",name,"plan",value.Plan,"source",value.Source,"status",value.Status,"needsLogin",value.NeedsLogin,
                    "recordedAt",Date(value.RecordedAt),"windows",windows));
            }
            return new JavaScriptSerializer().Serialize(Map("schemaVersion",1,"connected",true,"generatedAt",Date(now),"refreshing",refreshing,"services",services));
        }
    }
    public sealed class SnapshotServer : IDisposable
    {
        readonly object gate=new object();
        readonly string name;
        readonly CancellationTokenSource stop=new CancellationTokenSource();
        readonly Task worker;
        NamedPipeServerStream pipe;
        string latest=UsageSnapshot.Disconnected();
        public static string CurrentName()
        { using(var identity=WindowsIdentity.GetCurrent())return "AIUsageManager.Snapshot."+identity.User.Value; }
        public static PipeSecurity CreateSecurity()
        {
            var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
            using(var identity=WindowsIdentity.GetCurrent()) {
                security.SetOwner(identity.User);security.AddAccessRule(new PipeAccessRule(identity.User,PipeAccessRights.FullControl,AccessControlType.Allow));
            }
            return security;
        }
        public SnapshotServer(string pipeName) { name=pipeName;worker=Task.Run((Func<Task>)Run); }
        public void Publish(string json)
        { lock(gate)latest=json!=null&&Encoding.UTF8.GetByteCount(json)<=65536?json:UsageSnapshot.Disconnected(); }
        async Task Run()
        {
            while(!stop.IsCancellationRequested) {
                bool retry=false;
                try {
                    using(var listener=new NamedPipeServerStream(name,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,0,4096,CreateSecurity())) {
                        lock(gate) {if(stop.IsCancellationRequested)return;pipe=listener;}
                        await listener.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                        byte[] bytes;lock(gate)bytes=Encoding.UTF8.GetBytes(latest);
                        var write=listener.WriteAsync(bytes,0,bytes.Length,stop.Token);
                        if(await Task.WhenAny(write,Task.Delay(2000,stop.Token)).ConfigureAwait(false)!=write)listener.Dispose();
                        try {await write.ConfigureAwait(false);}catch(IOException){}catch(ObjectDisposedException){}
                    }
                } catch(OperationCanceledException){break;}catch(ObjectDisposedException){if(stop.IsCancellationRequested)break;}
                catch(IOException){retry=true;}catch(UnauthorizedAccessException){retry=true;}
                finally {lock(gate)pipe=null;}
                if(retry)await Backoff().ConfigureAwait(false);
            }
        }
        async Task Backoff() {try {await Task.Delay(1000,stop.Token).ConfigureAwait(false);}catch(OperationCanceledException){} }
        public void Dispose()
        {
            stop.Cancel();lock(gate)if(pipe!=null)pipe.Dispose();
            try {worker.Wait(3000);}catch(AggregateException){}stop.Dispose();
        }
    }
    public static class SnapshotClient
    {
        public static async Task<string> ReadAsync(string name)
        {
            using(var pipe=new NamedPipeClientStream(".",name,PipeDirection.In,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification)) {
                Task<string> transfer=ReadPipe(pipe);
                if(await Task.WhenAny(transfer,Task.Delay(2000)).ConfigureAwait(false)!=transfer)pipe.Dispose();
                try {return await transfer.ConfigureAwait(false);}catch(IOException){}catch(TimeoutException){}catch(UnauthorizedAccessException){}catch(ObjectDisposedException){}catch(OperationCanceledException){}catch(DecoderFallbackException){}
                return UsageSnapshot.Disconnected();
            }
        }
        static async Task<string> ReadPipe(NamedPipeClientStream pipe)
        {
            await pipe.ConnectAsync(800).ConfigureAwait(false);
            using(var data=new MemoryStream()) {
                byte[] buffer=new byte[4096];int count;
                while((count=await pipe.ReadAsync(buffer,0,buffer.Length).ConfigureAwait(false))>0) {
                    if(data.Length+count>65536)return UsageSnapshot.Disconnected();data.Write(buffer,0,count);
                }
                return data.Length==0?UsageSnapshot.Disconnected():new UTF8Encoding(false,true).GetString(data.ToArray());
            }
        }
    }
}
