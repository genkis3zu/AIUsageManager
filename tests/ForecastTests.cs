using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AIUsageManager;

internal static partial class Tests
{
    static List<ForecastSample> Routine(DateTimeOffset start,int hours,Func<DateTimeOffset,double> rate)
    {
        var points=new List<ForecastSample>();double percent=0;
        for(int i=0;i<=hours;i++) {
            if(i%168==0)percent=0;else percent+=rate(start.AddHours(i-1));
            points.Add(new ForecastSample{At=start.AddHours(i),Percent=percent,Reset=start.AddDays((i/168+1)*7)});
        }
        return points;
    }
    static ForecastHistory History(IEnumerable<ForecastSample> points,string path=null)
    {
        var list=points.ToArray();var history=new ForecastHistory(path,list.Last().At);
        foreach(var p in list)history.Record("Codex/週","Pro Lite",p);
        return history;
    }
    // Writes a history file in the format older builds use (version 1).
    static void WriteLegacyHistory(string path,string key,string plan,IEnumerable<ForecastSample> samples)
    {
        var epoch=new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero);
        var rows=samples.Select(p=>new[]{(p.At-epoch).TotalSeconds,p.Percent,(p.Reset-epoch).TotalSeconds}).ToArray();
        string json=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new {version=1,series=new[]{new {key=key,plan=plan,samples=rows}}});
        File.WriteAllBytes(path,System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(json),null,System.Security.Cryptography.DataProtectionScope.CurrentUser));
    }
    static ServiceUsage ClaudeWindow(UsageWindow window,DateTimeOffset at)
    {
        var value=new ServiceUsage{Name="Claude",Plan="Ultra",Source="オンライン",RecordedAt=at};value.Windows.Add(window);return value;
    }
    static UsagePace Forecast(List<ForecastSample> points)
    {
        var last=points.Last();var model=new UsageForecaster(History(points.Take(points.Count-1)));
        var value=Observed(last.Percent,last.At,last.Reset);return model.Observe(value,value.Windows[0],last.At);
    }
    static void RunForecastTests()
    {
        Check("Calibrated forecast alerts require conservative exhaustion before reset",()=> {
            var points=Routine(T.AddDays(-28),28*24,t=>0.4);var a=new UsageAlerts(new UsageForecaster(History(points.Take(points.Count-1))));
            var value=Observed(60,T,T.AddDays(7));var notices=Observe(a,value,T);
            Assert(value.Windows[0].Pace.CanNotify && notices.Length==1 && value.Windows[0].Alert.Forecast && value.Windows[0].Alert.ForecastAt<T.AddDays(7));
        });
        Check("Adaptive daily forecast beats short extrapolation on work and rest cycles",()=> {
            var start=T.AddDays(-14);var points=Routine(start,13*24+17,t=>t.Hour>=9&&t.Hour<17?1:0);
            var pace=Forecast(points);double actualDaily=8,oldDaily=24;
            Assert(!pace.IsLearning && pace.ValidationCount>=3 && pace.ModelWeights[1]+pace.ModelWeights[2]>pace.ModelWeights[0]);
            Assert(Math.Abs(pace.PointsPerHour*24-actualDaily)<Math.Abs(oldDaily-actualDaily)/2);
            Assert(pace.Detail.Contains("平均誤差") && pace.Summary.Contains("約") && pace.LowerRate<pace.UpperRate);
            Console.WriteLine("EVIDENCE work/rest daily error: old="+Math.Abs(oldDaily-actualDaily)+", adaptive="+Math.Abs(pace.PointsPerHour*24-actualDaily).ToString("0.##",System.Globalization.CultureInfo.InvariantCulture)+" points");
        });
        Check("Time and weekday candidate learns weekend differences",()=> {
            var start=new DateTimeOffset(2026,8,31,0,0,0,TimeSpan.Zero);
            Func<DateTimeOffset,double> activity=t=>t.ToLocalTime().DayOfWeek==DayOfWeek.Saturday||t.ToLocalTime().DayOfWeek==DayOfWeek.Sunday?0.02:0.3;
            var points=Routine(start,28*24,activity);
            var friday=points.First(p=>p.At>start.AddDays(20)&&p.At.ToLocalTime().DayOfWeek==DayOfWeek.Friday&&p.At.ToLocalTime().Hour==0).At;
            var saturday=friday.AddDays(1);
            var work=UsageForecaster.Models(points,friday,true);var rest=UsageForecaster.Models(points,saturday,true);
            Assert(work.Rates[2].HasValue&&rest.Rates[2].HasValue&&work.Rates[2]>rest.Rates[2]*3);
        });
        Check("Learned rate follows a sustained change instead of fixing the old average",()=> {
            var start=T.AddDays(-28);var changed=Routine(start,28*24-1,t=>t<start.AddDays(21)?0.1:0.4);
            var pace=Forecast(changed);Assert(pace.PointsPerHour>0.3&&pace.PointsPerHour<0.5&&pace.ValidationCount>=3);
        });
        Check("Historical model fit cannot see later observations",()=> {
            var points=Routine(T.AddDays(-14),14*24,t=>0.2);var at=points[200].At;
            var prefix=UsageForecaster.Models(points.Take(201).ToArray(),at,true);
            foreach(var point in points.Skip(201))point.Percent=99;
            var withFuture=UsageForecaster.Models(points,at,true);
            Assert(prefix.Rates.SequenceEqual(withFuture.Rates)&&prefix.Hours==withFuture.Hours);
        });
        Check("Five-hour windows learn time patterns across earlier reset periods",()=> {
            var points=new List<ForecastSample>();var start=T.AddDays(-14);double percent=0;
            for(int i=0;i<=14*48;i++) {
                if(i%10==0)percent=0;
                else {int hour=start.AddMinutes((i-1)*30).ToLocalTime().Hour;percent+=hour>=9&&hour<17?5:0.1;}
                points.Add(new ForecastSample{At=start.AddMinutes(i*30),Percent=percent,Reset=start.AddHours((i/10+1)*5)});
            }
            var retained=new ForecastHistory(null,T);
            foreach(var point in points)retained.Record("Claude/5時間","Max 20x",point);
            retained.Prune(T);points=retained.Series["Claude/5時間"].Samples;
            var work=points.Last(p=>p.At.ToLocalTime().Hour==10).At;
            var night=points.Last(p=>p.At.ToLocalTime().Hour==2).At;
            var busy=UsageForecaster.Models(points,work,false);var idle=UsageForecaster.Models(points,night,false);
            Assert(busy.Rates[2].HasValue&&idle.Rates[2].HasValue&&busy.Rates[2]>idle.Rates[2]*3);
        });
        Check("Reset and decrease boundaries never produce consumption deltas",()=> {
            var points=new[]{new ForecastSample{At=T,Percent=99,Reset=T.AddHours(1)},new ForecastSample{At=T.AddHours(1),Percent=0,Reset=T.AddDays(7)},new ForecastSample{At=T.AddHours(2),Percent=20,Reset=T.AddDays(7)},new ForecastSample{At=T.AddHours(3),Percent=5,Reset=T.AddDays(7)}};
            var fit=UsageForecaster.Models(points,T.AddHours(3),true);
            Assert(fit.Hours==1 && fit.Rates[1]==20);
        });
        Check("A new plan discards learned quota scaling",()=> {
            var points=Routine(T.AddDays(-4),96,t=>0.2);var model=new UsageForecaster(History(points));
            var value=Observed(25,T.AddHours(1),T.AddDays(3));value.Plan="Ultra";
            Assert(model.Observe(value,value.Windows[0],value.RecordedAt.Value).IsLearning);
        });
        Check("Missing failed stale and duplicate observations do not train",()=> {
            var history=History(new[]{new ForecastSample{At=T,Percent=10,Reset=T.AddDays(7)}});var model=new UsageForecaster(history);
            foreach(string mode in new[]{"failed","cached","stale","duplicate","reversed"}) {
                var value=Observed(20,T.AddMinutes(5),T.AddDays(7));
                if(mode=="failed")value.Status="接続できません";
                if(mode=="cached")value.Source="CLI 記録";
                if(mode=="stale")value.RecordedAt=T.AddHours(-1);
                if(mode=="duplicate")value.RecordedAt=T;
                if(mode=="reversed")value.RecordedAt=T.AddMinutes(-1);
                Assert(model.Observe(value,value.Windows[0],T.AddMinutes(5))==null);
            }
            Assert(history.Series["Codex/週"].Samples.Count==1);
        });
        Check("Unobserved gaps are not learned as idle hourly slots",()=> {
            var a=new ForecastSample{At=T,Percent=0,Reset=T.AddDays(7)};var b=new ForecastSample{At=T.AddHours(48),Percent=20,Reset=T.AddDays(7)};
            var fit=UsageForecaster.Models(new[]{a,b},b.At,true);Assert(fit.Hours==0&&fit.Rates.All(r=>!r.HasValue));
        });
        Check("Repeated manual samples cannot crowd out long history",()=> {
            var history=History(new[]{new ForecastSample{At=T,Percent=10,Reset=T.AddDays(7)}});
            for(int i=1;i<240;i++)history.Record("Codex/週","Pro Lite",new ForecastSample{At=T.AddSeconds(i),Percent=10,Reset=T.AddDays(7)});
            Assert(history.Series["Codex/週"].Samples.Count==1);
        });
        Check("Encrypted history survives restart and clear removes its files",()=> {
            var path=Path.Combine(Fixture(),"forecast-history.dat");var points=Routine(T.AddDays(-5),120,t=>0.2);var history=History(points,path);
            Assert(history.Save(T)&&File.Exists(path)&&!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("Pro Lite"));
            var loaded=new ForecastHistory(path,T);Assert(loaded.Series["Codex/週"].Samples.Last().Percent==points.Last().Percent);
            var model=new UsageForecaster(loaded);var value=Observed(25,T.AddHours(1),T.AddDays(2));
            Assert(!model.Observe(value,value.Windows[0],T.AddHours(1)).IsLearning);
            Assert(model.Clear()&&!File.Exists(path)&&!File.Exists(path+".tmp")&&loaded.Series.Count==0);
        });
        Check("週（Fable） uses weekly forecast rules and 5時間 uses five-hour rules",()=> {
            var model=new UsageForecaster();UsagePace scoped=null,session=null;
            for(int i=0;i<=24;i++) {
                var at=T.AddMinutes(5*i);
                var fable=ClaudeWindow(UsageWindow.Scoped("Fable",10+i,T.AddDays(3)),at);scoped=model.Observe(fable,fable.Windows[0],at);
                if(i<=7){var hour=ClaudeWindow(UsageWindow.FiveHour(5+i,T.AddHours(4)),at);session=model.Observe(hour,hour.Windows[0],at);}
            }
            Assert(scoped!=null&&scoped.IsLearning&&scoped.Detail.Contains("週の目安"));
            Assert(session!=null&&!session.IsLearning);
        });
        Check("Alerts for 週（Fable） are titled with the full label",()=> {
            var value=ClaudeWindow(UsageWindow.Scoped("Fable",96,T.AddDays(3)),T);
            new UsageAlerts().Observe(new[]{value},T,NotificationMode.All);
            Assert(value.Windows[0].Alert!=null&&value.Windows[0].Alert.Title.StartsWith("週（Fable）："));
        });
        Check("History seeds from the legacy file and keeps learning after the Ultra rename",()=> {
            var root=Fixture();string legacy=Path.Combine(root,"forecast-history.dat"),current=Path.Combine(root,"forecast-history-2.dat");
            var points=Routine(T.AddDays(-5),120,t=>0.2);
            WriteLegacyHistory(legacy,"Claude/週","Max 20x",points);
            var history=new ForecastHistory(current,T,legacy);
            Assert(history.Series["Claude/週"].Plan=="Ultra"&&history.Series["Claude/週"].Samples.Count==points.Count);
            history.Record("Claude/週","Ultra",new ForecastSample{At=T.AddHours(1),Percent=points.Last().Percent+1,Reset=points.Last().Reset});
            Assert(history.Series["Claude/週"].Samples.Count==points.Count+1);
            Assert(ForecastHistory.KnownPlan("Max 5x")=="Max"&&ForecastHistory.KnownPlan("Max 20x")=="Ultra");
        });
        Check("Saving never writes the legacy history file",()=> {
            var root=Fixture();string legacy=Path.Combine(root,"forecast-history.dat"),current=Path.Combine(root,"forecast-history-2.dat");
            WriteLegacyHistory(legacy,"Claude/週","Max 20x",Routine(T.AddDays(-2),48,t=>0.2));
            byte[] before=File.ReadAllBytes(legacy);var stamp=File.GetLastWriteTimeUtc(legacy);
            var history=new ForecastHistory(current,T,legacy);
            history.Record("Claude/週（Fable）","Ultra",new ForecastSample{At=T.AddMinutes(5),Percent=40,Reset=T.AddDays(3)});
            Assert(history.Save(T.AddMinutes(5))&&File.Exists(current));
            Assert(File.ReadAllBytes(legacy).SequenceEqual(before)&&File.GetLastWriteTimeUtc(legacy)==stamp);
            string saved=Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(current),null,System.Security.Cryptography.DataProtectionScope.CurrentUser));
            Assert(saved.Contains("\"version\":2"));
        });
        Check("New window keys survive save and load, unsafe names are rejected",()=> {
            var path=Path.Combine(Fixture(),"forecast-history-2.dat");var history=new ForecastHistory(path,T);
            var sample=new ForecastSample{At=T,Percent=10,Reset=T.AddDays(3)};
            Assert(history.Record("Claude/週（Fable）","Ultra",sample)!=null&&history.Record("Codex/5時間","Plus",sample)!=null&&history.Record("Claude/週（モデル別）","Max",sample)!=null);
            Assert(history.Record("Claude/週（<script>）","Ultra",sample)==null&&history.Record("Other/週","Ultra",sample)==null);
            Assert(history.Save(T));
            var loaded=new ForecastHistory(path,T);
            Assert(loaded.Series.ContainsKey("Claude/週（Fable）")&&loaded.Series.ContainsKey("Codex/5時間")&&loaded.Series.ContainsKey("Claude/週（モデル別）"));
        });
        Check("History keeps at most twelve windows and never drops the fixed ones",()=> {
            var path=Path.Combine(Fixture(),"forecast-history-2.dat");var history=new ForecastHistory(path,T);
            var weekly=Routine(T.AddDays(-3),72,t=>0.2);foreach(var p in weekly)history.Record("Claude/週","Ultra",p);
            foreach(string key in new[]{"Claude/5時間","Codex/5時間","Codex/週","Grok/週"})history.Record(key,"Ultra",new ForecastSample{At=T.AddMinutes(-30),Percent=5,Reset=T.AddDays(3)});
            for(int i=0;i<10;i++)Assert(history.Record("Claude/週（M"+i+"）","Ultra",new ForecastSample{At=T.AddMinutes(-20+i),Percent=i,Reset=T.AddDays(3)})!=null);
            Assert(history.Series.Count==12&&history.Series["Claude/週"].Samples.Count==weekly.Count);
            Assert(!history.Series.ContainsKey("Claude/週（M0）")&&!history.Series.ContainsKey("Claude/週（M2）")&&history.Series.ContainsKey("Claude/週（M3）")&&history.Series.ContainsKey("Claude/週（M9）"));
            Assert(new[]{"Claude/5時間","Claude/週","Codex/5時間","Codex/週","Grok/週"}.All(history.Series.ContainsKey));
            Assert(history.Save(T));var loaded=new ForecastHistory(path,T);
            Assert(loaded.Series.Count==12&&loaded.Series["Claude/週"].Samples.Count==weekly.Count&&loaded.StorageStatus=="");
        });
        Check("Pruning removes windows whose samples are all gone",()=> {
            var history=new ForecastHistory(null,T);
            history.Record("Claude/週（Old）","Ultra",new ForecastSample{At=T.AddDays(-100),Percent=10,Reset=T.AddDays(-97)});
            history.Record("Claude/週","Ultra",new ForecastSample{At=T,Percent=10,Reset=T.AddDays(3)});
            history.Prune(T);Assert(!history.Series.ContainsKey("Claude/週（Old）")&&history.Series.ContainsKey("Claude/週"));
        });
        Check("Clear deletes both history files",()=> {
            var root=Fixture();string legacy=Path.Combine(root,"forecast-history.dat"),current=Path.Combine(root,"forecast-history-2.dat");
            WriteLegacyHistory(legacy,"Claude/週","Max 20x",Routine(T.AddDays(-2),48,t=>0.2));
            var history=new ForecastHistory(current,T,legacy);Assert(history.Save(T));
            Assert(history.Clear()&&!File.Exists(current)&&!File.Exists(legacy));
        });
        Check("Corrupt and oversized saved data fail safely without echoing contents",()=> {
            var path=Path.Combine(Fixture(),"forecast-history.dat");File.WriteAllText(path,"private-fixture");
            var history=new ForecastHistory(path,T);Assert(history.Series.Count==0&&history.StorageStatus.Contains("読み込めず")&&!history.StorageStatus.Contains("private"));
            using(var file=File.Create(path))file.SetLength(4194305);
            Assert(new ForecastHistory(path,T).Series.Count==0);
        });
        Check("Retention removes ninety-day-old observations and bounds storage",()=> {
            var points=Routine(T.AddDays(-100),100*24,t=>0.2);var history=History(points);history.Prune(T);
            var saved=history.Series["Codex/週"].Samples;
            Assert(saved.Count<=6000&&saved.All(p=>p.At>=T.AddDays(-90))&&saved.Last().At==T);
        });
        Check("Write failure keeps forecasts available with a visible storage state",()=> {
            var root=Fixture();var blocked=Path.Combine(root,"file");File.WriteAllText(blocked,"fixture");
            var history=History(Routine(T.AddDays(-3),72,t=>0.2),Path.Combine(blocked,"history.dat"));
            Assert(!history.Save(T)&&history.Series.Count==1&&history.StorageStatus.Contains("保存できません"));
        });
    }
}
