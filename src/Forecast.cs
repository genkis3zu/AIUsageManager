using System;
using System.Collections.Generic;
using System.Linq;

namespace AIUsageManager
{
    // All backtests fit only observations available at the historical prediction time.
    public sealed class UsageForecaster
    {
        readonly ForecastHistory history;
        readonly object gate=new object();
        internal sealed class Fit
        {
            public readonly double?[] Rates=new double?[3];
            public double Hours;
            public int Days;
        }
        sealed class Bucket
        {
            public double Change, Hours;
            public readonly HashSet<DateTime> Days=new HashSet<DateTime>();
            public void Add(double rate,double hours,double weight,DateTime day)
            {Change+=rate*hours*weight;Hours+=hours*weight;Days.Add(day);}
        }
        public UsageForecaster() : this(null,DateTimeOffset.UtcNow) { }
        public UsageForecaster(string path,DateTimeOffset now) {history=new ForecastHistory(path,now);}
        internal UsageForecaster(ForecastHistory data) {history=data;}
        public static UsageForecaster Default() {return new UsageForecaster(ForecastHistory.DefaultPath(),DateTimeOffset.UtcNow);}
        public bool Save(DateTimeOffset now) {lock(gate)return history.Save(now);}
        public bool Clear() {lock(gate)return history.Clear();}
        public string Status {
            get {lock(gate)return (history.StorageStatus.Length>0?history.StorageStatus+"\n":"")+
                "このPCに90日保存・Windowsユーザー別に暗号化\n観測 "+history.Series.Values.Sum(s=>s.Samples.Count)+"件\n会話内容・ログイン情報は保存しません";}
        }
        static int Group(DateTime local) {return (local.DayOfWeek==DayOfWeek.Saturday||local.DayOfWeek==DayOfWeek.Sunday?24:0)+local.Hour;}
        static double Overlap(DateTimeOffset start,DateTimeOffset end,DateTimeOffset floor)
        {return Math.Max(0,(end-(start>floor?start:floor)).TotalHours);}
        internal static Fit Models(IList<ForecastSample> samples,DateTimeOffset at,bool weekly)
        {
            var result=new Fit();var shortTerm=new Bucket();var longTerm=new Bucket();
            var pattern=Enumerable.Range(0,48).Select(i=>new Bucket()).ToArray();
            var weekday=Enumerable.Range(0,168).Select(i=>new Bucket()).ToArray();
            var days=new HashSet<DateTime>();var patternDays=new HashSet<DateTime>();
            double recentHours=weekly?6:0.5,longHours=weekly?168:24;
            for(int i=1;i<samples.Count;i++) {
                var a=samples[i-1];var b=samples[i];if(b.At>at)break;
                if(b.At<at.AddDays(-28)||!ForecastSample.Continuous(a,b))continue;
                double hours=(b.At-a.At).TotalHours;
                if(hours>(weekly?26:2))continue;
                double rate=(b.Percent-a.Percent)/hours;
                double shortHours=Overlap(a.At,b.At,at.AddHours(-recentHours));
                double historyHours=Overlap(a.At,b.At,at.AddHours(-longHours));
                double weight=Math.Pow(0.5,(at-b.At).TotalDays/7);
                var day=b.At.ToLocalTime().Date;
                shortTerm.Add(rate,shortHours,1,day);longTerm.Add(rate,historyHours,weight,day);
                result.Hours+=historyHours;
                if(historyHours>0)days.Add(day);
                // A long gap has a known total, but does not reveal individual hours of activity.
                if(hours>1.5)continue;
                patternDays.Add(day);
                DateTimeOffset cursor=a.At;
                while(cursor<b.At) {
                    var local=cursor.ToLocalTime();var end=cursor.AddMinutes(60-local.Minute).AddSeconds(-local.Second).AddTicks(-(local.Ticks%TimeSpan.TicksPerSecond));
                    if(end<=cursor)end=cursor.AddHours(1);if(end>b.At)end=b.At;
                    double portion=(end-cursor).TotalHours;
                    pattern[Group(local.DateTime)].Add(rate,portion,weight,local.Date);
                    weekday[(int)local.DayOfWeek*24+local.Hour].Add(rate,portion,weight,local.Date);
                    cursor=end;
                }
            }
            result.Days=days.Count;
            if(shortTerm.Hours>=(weekly?1:0.25))result.Rates[0]=shortTerm.Change/shortTerm.Hours;
            if(longTerm.Hours>0)result.Rates[1]=longTerm.Change/longTerm.Hours;
            if(patternDays.Count>=7&&result.Rates[1].HasValue) {
                double hours=weekly?24:0.5,total=0,matched=0;
                for(double offset=0;offset<hours;offset+=0.5) {
                    var local=at.AddHours(offset+0.25).ToLocalTime();
                    var exact=weekday[(int)local.DayOfWeek*24+local.Hour];var similar=pattern[Group(local.DateTime)];
                    var bucket=exact.Days.Count>=2?exact:similar.Days.Count>=3?similar:null;
                    double rate=bucket!=null&&bucket.Hours>0?bucket.Change/bucket.Hours:result.Rates[1].Value;
                    total+=rate*0.5;if(bucket!=null)matched+=0.5;
                }
                if(matched>=hours/2)result.Rates[2]=total/hours;
            }
            return result;
        }
        static void Backtest(IList<ForecastSample> points,DateTimeOffset at,bool weekly,double[] errors,double[] weights,int[] counts)
        {
            double horizon=weekly?24:0.5;DateTimeOffset next=at.AddHours(-horizon);int tested=0;
            for(int i=points.Count-2;i>=0&&tested<28;i--) {
                var origin=points[i];if(origin.At>next)continue;
                if(origin.At<at.AddDays(weekly?-28:-7))break;
                next=origin.At.AddHours(weekly?-24:-1);
                int end=i+1;
                while(end<points.Count&&points[end].At<origin.At.AddHours(horizon))end++;
                if(end>=points.Count||points[end].At>at||points[end].At>origin.At.AddHours(horizon+(weekly?1.5:1.0/6)))continue;
                bool continuous=true;for(int j=i+1;j<=end;j++)if(!ForecastSample.Continuous(points[j-1],points[j])){continuous=false;break;}
                if(!continuous)continue;
                var fit=Models(points,origin.At,weekly);
                if(fit.Hours<(weekly?24:0.5))continue;
                double hours=(points[end].At-origin.At).TotalHours,actual=points[end].Percent-origin.Percent;
                double recency=Math.Pow(0.5,(at-points[end].At).TotalDays/(weekly?7:1));
                for(int model=0;model<3;model++)if(fit.Rates[model].HasValue) {
                    // Normalize small endpoint timing differences to the named forecast horizon.
                    double error=Math.Abs(fit.Rates[model].Value*hours-actual)*horizon/hours;
                    errors[model]+=error*recency;weights[model]+=recency;counts[model]++;
                }
                tested++;
            }
        }
        public UsagePace Observe(ServiceUsage service,UsageWindow window,DateTimeOffset now)
        {
            lock(gate) {
                if(!service.RecordedAt.HasValue||!window.Percent.HasValue)return null;
                var at=service.RecordedAt.Value;
                // The alerts layer checks freshness; repeat these guards for standalone callers.
                if(service.Source!="オンライン"||service.Status!=""||service.NeedsLogin||at<now.AddMinutes(-15)||at>now.AddMinutes(1)||
                    !Json.Percent(window.Percent.Value).HasValue)return null;
                if(!window.ResetsAt.HasValue||window.ResetsAt<=at)return UsagePace.Learning(window,0,0,"リセット時刻を確認できるまで予測を保留します");
                ForecastSeries previous;
                if(history.Series.TryGetValue(service.Name+"/"+window.Label,out previous)&&previous.Samples.Count>0&&at<=previous.Samples[previous.Samples.Count-1].At)return null;
                var series=history.Record(service.Name+"/"+window.Label,service.Plan,new ForecastSample {At=at,Percent=window.Percent.Value,Reset=window.ResetsAt.Value});
                if(series==null)return null;
                history.Prune(at);
                bool weekly=window.Label=="週";var fit=Models(series.Samples,at,weekly);
                if(series.Samples.Count<3||fit.Hours<(weekly?24:0.5))return UsagePace.Learning(window,fit.Hours,fit.Days,history.StorageStatus);
                double[] errors=new double[3],errorWeights=new double[3];int[] counts=new int[3];
                Backtest(series.Samples,at,weekly,errors,errorWeights,counts);
                double horizon=weekly?24:0.5;
                double baseline=errorWeights.Sum()>0?errors.Sum()/errorWeights.Sum():Math.Max(1,fit.Rates[1].GetValueOrDefault()*horizon*0.5);
                double[] priors={0.15,0.65,0.20},blend=new double[3];
                for(int i=0;i<3;i++)if(fit.Rates[i].HasValue) {
                    double score=(errors[i]+2*baseline)/(errorWeights[i]+2);
                    blend[i]=priors[i]/Math.Pow(0.5+score,2);
                }
                double sum=blend.Sum(),rate=0,error=0;
                for(int i=0;i<3;i++){blend[i]/=sum;rate+=blend[i]*fit.Rates[i].GetValueOrDefault();error+=blend[i]*(errorWeights[i]>0?errors[i]/errorWeights[i]:baseline);}
                double variance=0;for(int i=0;i<3;i++)variance+=blend[i]*Math.Pow(fit.Rates[i].GetValueOrDefault()-rate,2);
                int validations=counts.Max();
                double width=Math.Max(rate*(validations>=3?0.25:0.75),Math.Max(2*error/horizon,Math.Sqrt(variance)));
                double low=Math.Max(0,rate-width),high=rate+width;
                bool reliable=validations>=3&&error<=Math.Max(2,rate*horizon*0.4)&&low>0;
                return UsagePace.Adaptive(window,at,rate,low,high,fit.Hours,fit.Days,validations,error,horizon,reliable,blend,history.StorageStatus);
            }
        }
    }
}
