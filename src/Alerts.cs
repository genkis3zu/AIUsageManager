using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AIUsageManager
{
    public enum NotificationMode { Off, Thresholds, All }
    public sealed class UsageAlert
    {
        public int Threshold;
        public bool RapidIncrease, Forecast, Informational;
        public DateTimeOffset? ForecastAt;
        public string Title = "", Detail = "", Compact = "";
    }
    public sealed class UsageNotice
    {
        public string Service, Window, Text;
        public int Threshold;
    }
    public sealed class UsageAlerts
    {
        sealed class Sample { public DateTimeOffset At; public double Percent; }
        sealed class WindowState
        {
            public string Plan;
            public DateTimeOffset? ResetAt, LastAt, LastNotice;
            public double LastPercent;
            public int HighestThreshold, NotifiedTrends;
            public UsagePace Pace;
            public UsageAlert Alert;
            public readonly List<Sample> Samples = new List<Sample>();
        }
        readonly Dictionary<string, WindowState> states = new Dictionary<string, WindowState>();

        static bool Fresh(ServiceUsage service, UsageWindow window, DateTimeOffset now)
        {
            return service.Source=="オンライン" && string.IsNullOrEmpty(service.Status) && !service.NeedsLogin && service.RecordedAt.HasValue &&
                service.RecordedAt>=now.AddMinutes(-15) && service.RecordedAt<=now.AddMinutes(1) &&
                window.Percent.HasValue && !double.IsNaN(window.Percent.Value) && !double.IsInfinity(window.Percent.Value) && window.Percent>=0 && window.Percent<=100000 &&
                (!window.ResetsAt.HasValue || window.ResetsAt>now);
        }
        static int Level(double percent) { return percent>=100?100:percent>=95?95:percent>=90?90:0; }
        static string Number(double value) { return value.ToString("0.#",CultureInfo.InvariantCulture); }
        static string Duration(TimeSpan remaining)
        {
            if(remaining.TotalDays>=1)return ((int)remaining.TotalDays)+"日"+remaining.Hours+"時間";
            if(remaining.TotalHours>=1)return ((int)remaining.TotalHours)+"時間"+remaining.Minutes+"分";
            return Math.Max(1,(int)Math.Ceiling(remaining.TotalMinutes))+"分";
        }
        static UsageAlert Describe(UsageWindow window,int threshold,bool rapid,DateTimeOffset? forecast,DateTimeOffset at,double rise,double minutes)
        {
            if(threshold==0&&!rapid&&!forecast.HasValue)return null;
            string label=window.Label+"：";
            var alert=new UsageAlert {Threshold=threshold,RapidIncrease=rapid,Forecast=forecast.HasValue,ForecastAt=forecast};
            if(threshold>=100) { alert.Title=label+"上限に達しています";alert.Compact=label+"上限到達"; }
            else if(threshold>=90) { alert.Title=label+"使用量"+Number(window.Percent.Value)+"%・残り"+Number(Math.Max(0,100-window.Percent.Value))+"%";alert.Compact=label+(threshold>=95?"上限目前":"残りわずか"); }
            else if(forecast.HasValue) { alert.Title=label+Number(window.Percent.Value)+"% · "+window.Pace.TargetText+"（推定）";alert.Compact=label+"上限予測"; }
            else { alert.Title=label+"使用量が急増しています";alert.Compact=label+"急増"; }
            var details=new List<string>();
            if(threshold>=100)details.Add(window.ResetsAt.HasValue?"リセット予定："+window.ResetsAt.Value.ToLocalTime().ToString("MM/dd HH:mm"):"リセット時刻は未取得です");
            else {
                if(rapid)details.Add("急増：直近"+Number(minutes)+"分で +"+Number(rise)+"ポイント");
                if(window.Pace!=null)details.Add(window.Pace.Detail);
                if(details.Count==0)details.Add(window.ResetsAt.HasValue?"リセットまで "+Duration(window.ResetsAt.Value-at):"使用量を抑える目安です");
            }
            alert.Detail=string.Join("。",details);return alert;
        }
        public UsageNotice[] Observe(ServiceUsage[] values, DateTimeOffset now, NotificationMode mode)
        {
            var notices=new List<UsageNotice>();
            foreach(var service in values)foreach(var window in service.Windows) {
                window.Alert=null;window.Pace=null;
                string key=service.Name+"\n"+window.Label;
                WindowState state;states.TryGetValue(key,out state);
                if(!Fresh(service,window,now)) { if(state!=null){state.Samples.Clear();state.NotifiedTrends=0;state.Pace=null;state.Alert=null;}continue; }
                DateTimeOffset at=service.RecordedAt.Value;double percent=window.Percent.Value;
                // Re-reading a saved observation must not advance history or reset suppression.
                if(state!=null&&state.LastAt.HasValue&&at<=state.LastAt.Value) {
                    if(at==state.LastAt.Value&&percent==state.LastPercent){window.Pace=state.Pace;window.Alert=state.Alert??Current(service,window,now);}
                    else window.Alert=Current(service,window,now);
                    continue;
                }
                bool changedPeriod=state!=null&&state.ResetAt.HasValue&&window.ResetsAt.HasValue&&Math.Abs((window.ResetsAt.Value-state.ResetAt.Value).TotalMinutes)>2;
                if(state==null||changedPeriod||state.Plan!=service.Plan||(state.LastAt.HasValue&&state.LastPercent-percent>=20)) {
                    state=new WindowState {Plan=service.Plan,ResetAt=window.ResetsAt};states[key]=state;
                }
                if(state.LastAt.HasValue&&(percent<state.LastPercent||at-state.LastAt.Value>TimeSpan.FromMinutes(15))) {state.Samples.Clear();state.NotifiedTrends=0;}
                if(window.ResetsAt.HasValue&&!state.ResetAt.HasValue)state.ResetAt=window.ResetsAt;
                state.Samples.RemoveAll(s=>at-s.At>TimeSpan.FromMinutes(30));
                state.Samples.Add(new Sample {At=at,Percent=percent});
                if(state.Samples.Count>120)state.Samples.RemoveAt(0);
                state.LastAt=at;state.LastPercent=percent;
                var recent=state.Samples.FirstOrDefault(s=>at-s.At>=TimeSpan.FromMinutes(5)&&at-s.At<=TimeSpan.FromMinutes(15));
                double rise=recent==null?0:percent-recent.Percent;
                double minutes=recent==null?0:(at-recent.At).TotalMinutes;
                bool rapid=rise>=10&&percent<100;
                DateTimeOffset? forecast=null;
                var first=state.Samples[0];double span=(at-first.At).TotalMinutes,delta=percent-first.Percent;
                window.Pace=UsagePace.Calculate(window,state.Samples.Count,span,delta,at);
                if(percent<100&&window.ResetsAt.HasValue&&state.Samples.Count>=3&&span>=10&&delta>=2) {
                    var predicted=at.AddMinutes((100-percent)*span/delta);
                    if(predicted>now&&predicted<window.ResetsAt.Value)forecast=predicted;
                }
                int level=Level(percent),trends=(rapid?1:0)|(forecast.HasValue?2:0);
                window.Alert=Describe(window,level,rapid,forecast,at,rise,minutes);
                state.Pace=window.Pace;state.Alert=window.Alert;
                bool newThreshold=level>state.HighestThreshold;
                state.HighestThreshold=Math.Max(level,state.HighestThreshold);
                state.NotifiedTrends&=trends;
                bool newTrend=(trends&~state.NotifiedTrends)!=0&&(!state.LastNotice.HasValue||now-state.LastNotice.Value>=TimeSpan.FromMinutes(30));
                bool send=mode!=NotificationMode.Off&&(newThreshold||(mode==NotificationMode.All&&newTrend));
                if(send) {
                    var alert=window.Alert;
                    notices.Add(new UsageNotice {Service=service.Name,Window=window.Label,Threshold=level,Text=service.Name+" "+alert.Title+"。"+alert.Detail});
                    state.LastNotice=now;state.NotifiedTrends|=trends;
                } else if(mode!=NotificationMode.All)state.NotifiedTrends=trends;
            }
            return notices.ToArray();
        }
        public static UsageAlert Current(ServiceUsage service, UsageWindow window, DateTimeOffset now)
        { return Fresh(service,window,now)?Describe(window,Level(window.Percent.Value),false,null,now,0,0):null; }
        public static UsageAlert[] ForDisplay(ServiceUsage service,DateTimeOffset now)
        { return service.Windows.Select(w=> {
            var alert=ForWindow(service,w,now);if(alert!=null)return alert;
            var pace=PaceForWindow(service,w,now);
            return pace==null?null:new UsageAlert {Informational=true,Title=w.Label+"："+pace.Summary,Detail=pace.Detail,Compact=w.Label+"："+pace.TargetText};
        }).Where(a=>a!=null).ToArray(); }
        public static UsagePace PaceForWindow(ServiceUsage service,UsageWindow window,DateTimeOffset now)
        { return Fresh(service,window,now)?window.Pace:null; }
        public static UsageAlert ForWindow(ServiceUsage service,UsageWindow window,DateTimeOffset now)
        { return Fresh(service,window,now)?window.Alert??Current(service,window,now):null; }
        public static string NotificationText(UsageNotice[] notices)
        {
            string text=string.Join("\n",notices.OrderByDescending(n=>n.Threshold).Select(n=>n.Text));
            return text.Length<=255?text:text.Substring(0,229)+"…\n詳しくは使用量を開いてください";
        }
    }
}
