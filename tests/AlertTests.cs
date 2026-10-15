using System;
using System.Linq;
using AIUsageManager;

internal static partial class Tests
{
    static ServiceUsage Observed(double? percent, DateTimeOffset at, DateTimeOffset? reset)
    {
        var value=ServiceUsage.Empty("Codex","");value.Plan="Pro Lite";value.Source="オンライン";value.RecordedAt=at;
        value.Windows[0].Percent=percent;value.Windows[0].ResetsAt=reset;return value;
    }
    static UsageNotice[] Observe(UsageAlerts alerts,ServiceUsage value,DateTimeOffset now,NotificationMode mode=NotificationMode.All)
    { return alerts.Observe(new[]{value},now,mode); }
    static void RunAlertTests()
    {
        Check("Short weekly burst is learning rather than a confident multi-day forecast",()=> {
            var a=new UsageAlerts();var reset=T.AddDays(7);
            Observe(a,Observed(20,T,reset),T);Observe(a,Observed(23,T.AddMinutes(5),reset),T.AddMinutes(5));
            var value=Observed(26,T.AddMinutes(10),reset);var notices=Observe(a,value,T.AddMinutes(10));
            Assert(notices.Length==0 && value.Windows[0].Pace!=null && value.Windows[0].Pace.Summary.Contains("学習中") && value.Windows[0].Pace.At90==null && value.Windows[0].Alert==null);
        });
        Check("Adaptive pace explains current percentage and milestone uncertainty",()=> {
            var points=Routine(T.AddDays(-5),120,t=>0.4);var a=new UsageAlerts(new UsageForecaster(History(points)));
            var value=Observed(48.4,T.AddHours(1),T.AddDays(2));Observe(a,value,T.AddHours(1));
            var pace=value.Windows[0].Pace;
            Assert(pace!=null && pace.Detail.Contains("現在48.4%") && pace.Summary.Contains("90%") && pace.Detail.Contains("平均誤差") && pace.Detail.Contains("ポイント") && pace.Detail.Contains("95%"));
        });
        Check("Slow weekly pace is available without a limit warning",()=> {
            var a=new UsageAlerts();var reset=T.AddDays(2);
            Observe(a,Observed(35.75,T,reset),T);Observe(a,Observed(35.8,T.AddMinutes(15),reset),T.AddMinutes(15));
            var value=Observed(36,T.AddMinutes(30),reset);Observe(a,value,T.AddMinutes(30));
            var shown=UsageAlerts.ForDisplay(value,T.AddMinutes(30));
            Assert(shown.Length==1 && shown[0].Detail.Contains("現在36%") && shown[0].Informational && shown[0].Title.Contains("学習中") && value.Windows[0].Pace.At90==null);
        });
        Check("Pace milestones handle reached levels, zero growth and missing reset",()=> {
            var reached=UsagePace.Adaptive(new UsageWindow("週",96,T.AddDays(7)),T,2,1,3,72,4,3,1,24,true,new[]{0.2,0.6,0.2},"");
            Assert(reached.At90==null && reached.At95==null && reached.At100==T.AddHours(2) && reached.Summary.Contains("100%まで") && reached.Detail.Contains("95%：到達済み"));
            var steady=Forecast(Routine(T.AddDays(-5),120,t=>0));
            Assert(steady.PointsPerHour==0 && steady.At90==null && steady.Summary.Contains("時期未確定")&&!steady.CanNotify);
            var value=Observed(20,T,null);var unknown=new UsageForecaster().Observe(value,value.Windows[0],T);
            Assert(unknown.IsLearning && unknown.At90==null && unknown.Detail.Contains("リセット時刻"));
        });
        Check("Pace is preserved on identical reads but cleared on failure or a new period",()=> {
            var a=new UsageAlerts();var reset=T.AddDays(7);
            Observe(a,Observed(20,T,reset),T);Observe(a,Observed(21,T.AddMinutes(5),reset),T.AddMinutes(5));
            var value=Observed(22,T.AddMinutes(10),reset);Observe(a,value,T.AddMinutes(10));
            Assert(value.Windows[0].Pace!=null);
            var repeated=Observed(22,T.AddMinutes(10),reset);Observe(a,repeated,T.AddMinutes(11));Assert(repeated.Windows[0].Pace!=null);
            var failed=Observed(22,T.AddMinutes(10),reset);failed.Status="接続できません";Observe(a,failed,T.AddMinutes(11));
            var resumed=Observed(22,T.AddMinutes(10),reset);Observe(a,resumed,T.AddMinutes(12));Assert(resumed.Windows[0].Pace==null);
            var next=Observed(22,T.AddMinutes(15),reset.AddDays(7));Observe(a,next,T.AddMinutes(15));Assert(next.Windows[0].Pace.IsLearning && next.Windows[0].Pace.At90==null);
        });
        Check("Alert thresholds notify once per level and period",()=> {
            var a=new UsageAlerts();var reset=T.AddDays(1);
            Assert(Observe(a,Observed(89,T,reset),T,NotificationMode.Thresholds).Length==0);
            Assert(Observe(a,Observed(90,T.AddMinutes(5),reset),T.AddMinutes(5),NotificationMode.Thresholds).Single().Threshold==90);
            Assert(Observe(a,Observed(94,T.AddMinutes(10),reset),T.AddMinutes(10),NotificationMode.Thresholds).Length==0);
            Assert(Observe(a,Observed(95,T.AddMinutes(15),reset),T.AddMinutes(15),NotificationMode.Thresholds).Single().Threshold==95);
            Assert(Observe(a,Observed(100,T.AddMinutes(20),reset),T.AddMinutes(20),NotificationMode.Thresholds).Single().Threshold==100);
            Assert(Observe(a,Observed(100,T.AddMinutes(25),reset),T.AddMinutes(25),NotificationMode.Thresholds).Length==0);
        });
        Check("Initial critical reading emits highest threshold only",()=> {
            var value=Observed(99,T,T.AddDays(1));var notices=Observe(new UsageAlerts(),value,T);
            Assert(notices.Length==1 && notices[0].Threshold==95 && value.Windows[0].Alert.Title.Contains("残り") && value.Windows[0].Alert.Compact.Contains("目前"));
        });
        Check("Small corrections and reset timestamp jitter do not repeat alerts",()=> {
            var a=new UsageAlerts();Observe(a,Observed(95,T,T.AddDays(1)),T);
            Observe(a,Observed(94,T.AddMinutes(5),T.AddDays(1).AddSeconds(20)),T.AddMinutes(5));
            Assert(Observe(a,Observed(95,T.AddMinutes(10),T.AddDays(1)),T.AddMinutes(10)).Length==0);
        });
        Check("New usage period and manual reset rearm threshold alerts",()=> {
            var a=new UsageAlerts();Observe(a,Observed(95,T,T.AddDays(1)),T);
            var next=T.AddDays(1);Assert(Observe(a,Observed(95,next,next.AddDays(1)),next).Length==1);
            Observe(a,Observed(0,next.AddMinutes(5),next.AddDays(1)),next.AddMinutes(5));
            Assert(Observe(a,Observed(95,next.AddMinutes(10),next.AddDays(1)),next.AddMinutes(10)).Single().Threshold==95);
        });
        Check("Old, failed, future, expired and unknown values never raise current alerts",()=> {
            foreach(var value in new[]{Observed(null,T,T.AddDays(1)),Observed(99,T.AddMinutes(-16),T.AddDays(1)),Observed(99,T.AddMinutes(2),T.AddDays(1)),Observed(99,T,T.AddSeconds(-1))}) {
                Assert(Observe(new UsageAlerts(),value,T).Length==0 && value.Windows[0].Alert==null);
            }
            var cached=Observed(99,T,T.AddDays(1));cached.Source="CLI 記録";cached.Status="認証切れ";
            Assert(Observe(new UsageAlerts(),cached,T).Length==0 && UsageAlerts.Current(cached,cached.Windows[0],T)==null);
        });
        Check("Each Claude window has independent thresholds",()=> {
            var value=ServiceUsage.Empty("Claude","");value.Source="オンライン";value.RecordedAt=T;
            value.Windows[0].Percent=95;value.Windows[0].ResetsAt=T.AddHours(3);value.Windows[1].Percent=100;value.Windows[1].ResetsAt=T.AddDays(1);
            var notices=new UsageAlerts().Observe(new[]{value},T,NotificationMode.All);
            Assert(notices.Length==2 && notices[0].Window=="5時間" && notices[1].Window=="週" && value.Windows[1].Alert.Title.Contains("上限"));
        });
        Check("Rapid increase uses percentage points over at least five minutes",()=> {
            var a=new UsageAlerts();Observe(a,Observed(20,T,T.AddDays(1)),T);
            var fast=Observed(30,T.AddSeconds(10),T.AddDays(1));Assert(Observe(a,fast,T.AddSeconds(10)).Length==0 && fast.Windows[0].Alert==null);
            var value=Observed(32,T.AddMinutes(5),T.AddDays(1));var notices=Observe(a,value,T.AddMinutes(5));
            Assert(notices.Length==1 && value.Windows[0].Alert.RapidIncrease && value.Windows[0].Alert.Detail.Contains("12"));
        });
        Check("Early observations cannot trigger a long-range forecast notification",()=> {
            var a=new UsageAlerts();Observe(a,Observed(20,T,T.AddHours(3)),T);
            var second=Observed(23,T.AddMinutes(5),T.AddHours(3));Observe(a,second,T.AddMinutes(5));Assert(second.Windows[0].Alert==null);
            var value=Observed(26,T.AddMinutes(10),T.AddHours(3));var notices=Observe(a,value,T.AddMinutes(10));
            Assert(notices.Length==0 && value.Windows[0].Alert==null && value.Windows[0].Pace.IsLearning);
        });
        Check("Steady usage and reset before predicted exhaustion do not forecast",()=> {
            foreach(bool steady in new[]{false,true}) {
                var a=new UsageAlerts();Observe(a,Observed(20,T,T.AddMinutes(30)),T);Observe(a,Observed(steady?20:23,T.AddMinutes(5),T.AddMinutes(30)),T.AddMinutes(5));
                var value=Observed(steady?20:26,T.AddMinutes(10),T.AddMinutes(30));Assert(Observe(a,value,T.AddMinutes(10)).Length==0 && value.Windows[0].Alert==null);
            }
        });
        Check("Missing reset still warns at threshold but never predicts exhaustion",()=> {
            var value=Observed(95,T,null);Assert(Observe(new UsageAlerts(),value,T).Length==1 && !value.Windows[0].Alert.Forecast);
        });
        Check("Repeated manual refresh cannot manufacture observation history",()=> {
            var a=new UsageAlerts();var value=Observed(30,T,T.AddDays(1));
            for(int i=0;i<10;i++)Observe(a,value,T.AddMinutes(i));
            Assert(value.Windows[0].Alert==null);
        });
        Check("Offline gaps and usage decreases discard old forecasts",()=> {
            foreach(bool decrease in new[]{false,true}) {
                var a=new UsageAlerts();Observe(a,Observed(20,T,T.AddDays(1)),T);Observe(a,Observed(23,T.AddMinutes(5),T.AddDays(1)),T.AddMinutes(5));
                var now=decrease?T.AddMinutes(10):T.AddMinutes(30);var value=Observed(decrease?19:26,now,T.AddDays(1));
                Assert(Observe(a,value,now).Length==0 && value.Windows[0].Alert==null);
            }
        });
        Check("Trend notification cooldown does not suppress a new 95 percent threshold",()=> {
            var a=new UsageAlerts();Observe(a,Observed(70,T,T.AddDays(1)),T);
            var rapid=Observed(82,T.AddMinutes(5),T.AddDays(1));Assert(Observe(a,rapid,T.AddMinutes(5)).Length==1);
            var ongoing=Observed(86,T.AddMinutes(10),T.AddDays(1));Assert(Observe(a,ongoing,T.AddMinutes(10)).Length==0);
            Assert(Observe(a,Observed(95,T.AddMinutes(15),T.AddDays(1)),T.AddMinutes(15)).Single().Threshold==95);
        });
        Check("Muted or threshold-only notifications keep in-app trend warnings",()=> {
            foreach(var mode in new[]{NotificationMode.Off,NotificationMode.Thresholds}) {
                var a=new UsageAlerts();Observe(a,Observed(20,T,T.AddDays(1)),T,mode);
                var value=Observed(32,T.AddMinutes(5),T.AddDays(1));Assert(Observe(a,value,T.AddMinutes(5),mode).Length==0 && value.Windows[0].Alert.RapidIncrease);
            }
            var off=new UsageAlerts();Assert(Observe(off,Observed(100,T,T.AddDays(1)),T,NotificationMode.Off).Length==0);
        });
        Check("Out of order observations cannot roll back notification state",()=> {
            var a=new UsageAlerts();Observe(a,Observed(95,T.AddMinutes(10),T.AddDays(1)),T.AddMinutes(10));
            Assert(Observe(a,Observed(0,T,T.AddDays(1)),T.AddMinutes(11)).Length==0);
            Assert(Observe(a,Observed(95,T.AddMinutes(15),T.AddDays(1)),T.AddMinutes(15)).Length==0);
        });
    }
}
