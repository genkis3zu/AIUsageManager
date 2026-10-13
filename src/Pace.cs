using System;
using System.Globalization;

namespace AIUsageManager
{
    // A conditional extrapolation of this process's recent online observations, not a daily average.
    public sealed class UsagePace
    {
        public double ObservationMinutes, ChangePoints, PointsPerHour;
        public DateTimeOffset? At90, At95, At100;
        public string Summary, Detail, TargetText;
        static string Number(double value) { return value.ToString("0.##",CultureInfo.InvariantCulture); }
        static string Duration(TimeSpan span)
        {
            if(span.TotalDays>=1)return span.TotalDays.ToString("0.#",CultureInfo.InvariantCulture)+"日";
            if(span.TotalHours>=1)return span.TotalHours.ToString("0.#",CultureInfo.InvariantCulture)+"時間";
            return Math.Max(1,(int)Math.Ceiling(span.TotalMinutes))+"分";
        }
        static DateTimeOffset? Target(double target,double percent,double perHour,DateTimeOffset at)
        {
            if(percent>=target||perHour<=0)return null;
            double hours=(target-percent)/perHour;
            if(hours>24*365 || hours>(DateTimeOffset.MaxValue-at).TotalHours)return null;
            return at.AddHours(hours);
        }
        static string Milestone(int target,double percent,DateTimeOffset? predicted,DateTimeOffset at,DateTimeOffset? reset)
        {
            if(percent>=target)return target+"%：到達済み";
            if(!predicted.HasValue)return target+"%：予測保留";
            return target+"%：約"+Duration(predicted.Value-at)+"（"+predicted.Value.ToLocalTime().ToString("MM/dd HH:mm")+"）"+
                (reset.HasValue&&predicted>=reset?"・リセットが先":"");
        }
        public static UsagePace Calculate(UsageWindow window,int count,double minutes,double change,DateTimeOffset at)
        {
            if(count<3||minutes<10||!window.Percent.HasValue||change<0)return null;
            double percent=window.Percent.Value,perHour=change*60/minutes;
            var pace=new UsagePace {ObservationMinutes=minutes,ChangePoints=change,PointsPerHour=perHour};
            // Below 0.1 point, rounding noise is too large to extrapolate a useful date.
            if(change>=0.1) {
                pace.At90=Target(90,percent,perHour,at);pace.At95=Target(95,percent,perHour,at);pace.At100=Target(100,percent,perHour,at);
            }
            int next=percent<90?90:percent<95?95:100;
            var nextAt=next==90?pace.At90:next==95?pace.At95:pace.At100;
            pace.TargetText=percent>=100?"上限到達":nextAt.HasValue?next+"%まで約"+Duration(nextAt.Value-at):change==0?"直近"+Number(minutes)+"分は増加なし":"到達時期は予測保留";
            bool weekly=window.Label=="週";
            string unit=weekly?"日":"時間",rate=Number(perHour*(weekly?24:1));
            string basis="現在"+Number(percent)+"%。直近"+Number(minutes)+"分で +"+Number(change)+"ポイント（1"+unit+"換算 +"+rate+"ポイント/"+unit+"）";
            pace.Summary=pace.TargetText+(nextAt.HasValue?" · +"+rate+"pt/"+(weekly?"日":"時")+"（推定）":"");
            if(nextAt.HasValue&&window.ResetsAt.HasValue&&nextAt>=window.ResetsAt)pace.Summary+="・リセットが先";
            pace.Detail=basis+"\n"+Milestone(90,percent,pace.At90,at,window.ResetsAt)+" / "+Milestone(95,percent,pace.At95,at,window.ResetsAt)+"\n"+
                Milestone(100,percent,pace.At100,at,window.ResetsAt)+"。取得時点のペースが続く場合の推定（日平均ではありません）";
            if(!window.ResetsAt.HasValue)pace.Detail+="。リセット時刻は未取得";
            return pace;
        }
    }
}
