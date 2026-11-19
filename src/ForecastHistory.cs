using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AIUsageManager
{
    internal sealed class ForecastSample
    {
        public DateTimeOffset At, Reset;
        public double Percent;
        public static bool SamePeriod(ForecastSample a, ForecastSample b)
        {
            return b.At>a.At && b.At<a.Reset && Math.Abs((a.Reset-b.Reset).TotalMinutes)<=2 &&
                b.Percent>=a.Percent;
        }
        public static bool Continuous(ForecastSample a, ForecastSample b)
        {return SamePeriod(a,b)&&a.Percent<100&&b.Percent<100;}
    }
    internal sealed class ForecastSeries
    {
        public string Plan;
        public readonly List<ForecastSample> Samples=new List<ForecastSample>();
    }
    internal sealed class ForecastHistory
    {
        const int MaxBytes=4194304, MaxSamples=6000, MaxSeries=12;
        // Windows every build knows; they are never evicted to make room for per-model windows.
        static readonly string[] FixedKeys={"Claude/5時間","Claude/週","Codex/5時間","Codex/週","Grok/週"};
        readonly string path, legacyPath;
        public string StorageStatus="";
        public readonly Dictionary<string,ForecastSeries> Series=new Dictionary<string,ForecastSeries>();
        static readonly DateTimeOffset Epoch=new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero);
        public ForecastHistory(string file,DateTimeOffset now) : this(file,now,null) { }
        // Older builds discard a history file that contains windows they do not know, so this build keeps its
        // own file and only reads the older one to carry the learning over. The older file is never written.
        public ForecastHistory(string file,DateTimeOffset now,string legacyFile)
        {
            path=file;legacyPath=legacyFile;
            if(path==null)return;
            if(File.Exists(path))Load(path,now);
            else if(legacyPath!=null&&File.Exists(legacyPath))Load(legacyPath,now);
        }
        public static string DefaultPath()
        {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AIUsageManager","forecast-history-2.dat");}
        public static string LegacyPath()
        {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AIUsageManager","forecast-history.dat");}
        static readonly Regex KeyPattern=new Regex(@"^(Claude|Codex|Grok)/(5時間|週|週（[A-Za-z0-9 .+\-()]{1,24}）|週（モデル別）)$");
        static bool KnownKey(string key)
        {return key!=null&&KeyPattern.IsMatch(key);}
        internal static string KnownPlan(string plan)
        {
            // Anthropic renamed Max 20x to Ultra and Max 5x to Max; a rename alone must not restart learning.
            if(plan=="Max 20x")plan="Ultra";else if(plan=="Max 5x")plan="Max";
            return new[]{"プラン未取得","Max","Max 5x","Max 20x","Pro","Pro Lite","Pro Plus","Plus","Free","Team","Business","Enterprise","Edu","SuperGrok","SuperGrok Plus","SuperGrok Heavy","SuperGrok Lite","Premium","Premium+","Super","Ultra"}.Contains(plan)?plan:"プラン未取得";
        }
        public ForecastSeries Record(string key,string plan,ForecastSample sample)
        {
            if(!KnownKey(key))return null;
            // Per-model names come from the service, so the key set is open-ended. Stay within what Load accepts
            // by evicting the per-model window seen least recently; never fail the whole file.
            if(!Series.ContainsKey(key)&&Series.Count>=MaxSeries) {
                string oldest=Series.Where(p=>Array.IndexOf(FixedKeys,p.Key)<0)
                    .OrderBy(p=>p.Value.Samples.Count==0?DateTimeOffset.MinValue:p.Value.Samples[p.Value.Samples.Count-1].At)
                    .Select(p=>p.Key).FirstOrDefault();
                if(oldest==null)return null;
                Series.Remove(oldest);
            }
            ForecastSeries series;
            plan=KnownPlan(plan);
            if(!Series.TryGetValue(key,out series)||(plan!="プラン未取得"&&series.Plan!=plan)) {
                series=new ForecastSeries {Plan=plan};Series[key]=series;
            }
            var last=series.Samples.LastOrDefault();
            if(last==null||(sample.At>last.At&&((sample.At-last.At).TotalMinutes>=4||!ForecastSample.SamePeriod(last,sample))))series.Samples.Add(sample);
            return series;
        }
        public void Prune(DateTimeOffset now)
        {
            foreach(var series in Series.Values) {
                var points=series.Samples;points.RemoveAll(p=>p.At<now.AddDays(-90)||p.At>now.AddMinutes(1));
                // Preserve reset/decrease boundaries, and keep recent 5-minute observations intact.
                for(int i=points.Count-2;i>0;i--) {
                    var p=points[i];
                    if(p.At<now.AddDays(-2)&&p.At.UtcDateTime.Date==points[i+1].At.UtcDateTime.Date&&
                        p.At.UtcDateTime.Hour==points[i+1].At.UtcDateTime.Hour&&ForecastSample.SamePeriod(points[i-1],p)&&ForecastSample.SamePeriod(p,points[i+1]))points.RemoveAt(i);
                }
                if(points.Count>MaxSamples)points.RemoveRange(0,points.Count-MaxSamples);
            }
            // A window that has not been seen for 90 days (e.g. a renamed model) leaves the history.
            foreach(string key in Series.Where(p=>p.Value.Samples.Count==0).Select(p=>p.Key).ToList())Series.Remove(key);
        }
        static JavaScriptSerializer Serializer()
        {return new JavaScriptSerializer {MaxJsonLength=MaxBytes,RecursionLimit=16};}
        void Load(string source,DateTimeOffset now)
        {
            try {
                if(!File.Exists(source))return;
                byte[] encrypted;
                using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    if(input.Length>MaxBytes||input.Length<1)throw new InvalidDataException();
                    using(var buffer=new MemoryStream()){input.CopyTo(buffer);encrypted=buffer.ToArray();}
                }
                byte[] plain=ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser);
                try {
                    var root=Serializer().DeserializeObject(Encoding.UTF8.GetString(plain)) as Dictionary<string,object>;
                    var version=Json.Number(Json.Get(root,"version"));
                    if(version!=1&&version!=2)throw new InvalidDataException();
                    var entries=Json.Get(root,"series") as object[];
                    if(entries==null||entries.Length>(version==1?4:12))throw new InvalidDataException();
                    foreach(var entry in entries) {
                        var data=entry as Dictionary<string,object>;string key=Json.Str(data,"key"),plan=Json.Str(data,"plan");
                        var rows=Json.Get(data,"samples") as object[];
                        if(!KnownKey(key)||rows==null||rows.Length>MaxSamples||Series.ContainsKey(key))throw new InvalidDataException();
                        var series=new ForecastSeries{Plan=KnownPlan(plan)};Series.Add(key,series);
                        foreach(var item in rows) {
                            var row=item as object[];if(row==null||row.Length!=3)throw new InvalidDataException();
                            var at=Json.Date(row[0]);var percent=Json.Percent(row[1]);var reset=Json.Date(row[2]);
                            if(!at.HasValue||!percent.HasValue||!reset.HasValue||reset<=at)throw new InvalidDataException();
                            if(series.Samples.Count>0&&at<=series.Samples[series.Samples.Count-1].At)throw new InvalidDataException();
                            series.Samples.Add(new ForecastSample{At=at.Value,Percent=percent.Value,Reset=reset.Value});
                        }
                    }
                    Prune(now);
                } finally {Array.Clear(plain,0,plain.Length);}
            } catch(Exception e) {
                if(!StorageError(e))throw;Series.Clear();StorageStatus="履歴を読み込めず、新しく学習しています";
            }
        }
        static bool StorageError(Exception e)
        {return e is IOException||e is InvalidDataException||e is UnauthorizedAccessException||e is SecurityException||e is CryptographicException||e is ArgumentException||e is InvalidOperationException;}
        public bool Save(DateTimeOffset now)
        {
            if(path==null)return true;
            try {
                Prune(now);
                var entries=Series.Select(pair=>new {key=pair.Key,plan=pair.Value.Plan,samples=pair.Value.Samples.Select(p=>new[]{(p.At-Epoch).TotalSeconds,p.Percent,(p.Reset-Epoch).TotalSeconds}).ToArray()}).ToArray();
                byte[] plain=Encoding.UTF8.GetBytes(Serializer().Serialize(new {version=2,series=entries}));byte[] encrypted;
                try {encrypted=ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser);}finally {Array.Clear(plain,0,plain.Length);}
                if(encrypted.Length>MaxBytes)throw new InvalidDataException();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path+".tmp",encrypted);
                if(File.Exists(path))File.Replace(path+".tmp",path,null);else File.Move(path+".tmp",path);
                StorageStatus="";return true;
            } catch(Exception e) {
                if(!StorageError(e))throw;StorageStatus="履歴を保存できません（今回の起動中のみ学習）";return false;
            }
        }
        public bool Clear()
        {
            Series.Clear();
            try {
                if(path!=null){File.Delete(path);File.Delete(path+".tmp");}
                // Clearing is the user's request to forget the learning, so the older file goes too.
                if(legacyPath!=null){File.Delete(legacyPath);File.Delete(legacyPath+".tmp");}
                StorageStatus="";return true;
            }
            catch(Exception e){if(!StorageError(e))throw;StorageStatus="保存履歴を削除できませんでした";return false;}
        }
    }
}
