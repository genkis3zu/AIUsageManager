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
        Check("Forecast requires three samples over ten minutes and is explicitly an estimate",()=> {
            var a=new UsageAlerts();Observe(a,Observed(20,T,T.AddHours(3)),T);
            var second=Observed(23,T.AddMinutes(5),T.AddHours(3));Observe(a,second,T.AddMinutes(5));Assert(second.Windows[0].Alert==null);
            var value=Observed(26,T.AddMinutes(10),T.AddHours(3));var notices=Observe(a,value,T.AddMinutes(10));
            Assert(notices.Length==1 && value.Windows[0].Alert.Forecast && value.Windows[0].Alert.Title.Contains("見込み") && value.Windows[0].Alert.Detail.Contains("推定"));
            Assert(Math.Abs((value.Windows[0].Alert.ForecastAt.Value-T).TotalMinutes-133.3333)<0.01);
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
