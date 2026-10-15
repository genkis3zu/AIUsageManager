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
