using System;
using System.Globalization;

namespace AIUsageManager
{
    // Display of learned rates and conditional arrival estimates, including their uncertainty.
    public sealed class UsagePace
    {
        public double PointsPerHour;
        public DateTimeOffset? At90, At95, At100;
        public string Summary, Detail, TargetText;
        public bool IsLearning, CanNotify;
        public double LowerRate, UpperRate, ErrorPoints;
        public int ValidationCount;
        public double[] ModelWeights;
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
        internal static UsagePace Learning(UsageWindow window,double hours,int days,string storage)
        {
            return new UsagePace {IsLearning=true,Summary="学習中 · 観測"+Number(hours)+"時間",TargetText="予測は学習中",
                Detail="現在"+Number(window.Percent.GetValueOrDefault())+"%。予測は学習中（観測"+days+"日・"+Number(hours)+"時間）。\n"+
                (window.Label=="週"?"週の目安には24時間分以上の観測が必要です。":"目安には30分以上・3回以上の観測が必要です。")+
                "休止時間も含めて傾向を学びます。"+(string.IsNullOrEmpty(storage)?"":"\n"+storage)};
        }
        static string Range(int target,double percent,double low,double high,DateTimeOffset at)
        {
            if(percent>=target)return target+"%：到達済み";
            var early=Target(target,percent,high,at);var late=Target(target,percent,low,at);
            if(!early.HasValue)return target+"%：時期未確定";
            return target+"%まで約"+Duration(early.Value-at)+(late.HasValue?"〜"+Duration(late.Value-at):"〜未定");
        }
        internal static UsagePace Adaptive(UsageWindow window,DateTimeOffset at,double rate,double low,double high,double hours,int days,int count,double error,double horizon,bool reliable,double[] weights,string storage)
        {
            double percent=window.Percent.Value;int next=percent<90?90:percent<95?95:100;
            var pace=new UsagePace {PointsPerHour=rate,LowerRate=low,UpperRate=high,
                ValidationCount=count,ErrorPoints=error,CanNotify=reliable,ModelWeights=weights};
            pace.At90=Target(90,percent,rate,at);pace.At95=Target(95,percent,rate,at);pace.At100=Target(100,percent,rate,at);
            pace.TargetText=Range(next,percent,low,high,at);
            pace.Summary=(count>=3?"":"学習中 · ")+pace.TargetText+(count>=3?" · 実績で補正":"");
            string unit=window.Label=="週"?"日":"時間";
            var central=next==90?pace.At90:next==95?pace.At95:pace.At100;
            if(central.HasValue&&window.ResetsAt.HasValue&&central>=window.ResetsAt)pace.Summary+="・リセットが先";
            pace.Detail="現在"+Number(percent)+"%。履歴"+days+"日・観測"+Number(hours)+"時間。推定 +"+Number(rate*(window.Label=="週"?24:1))+"ポイント/"+unit+"\n"+
                Range(90,percent,low,high,at)+" / "+Range(95,percent,low,high,at)+"\n"+
                Milestone(100,percent,pace.At100,at,window.ResetsAt)+"。目安幅は過去の誤差と推定方法の差から算出。\n"+
                "検証"+count+"件："+Number(horizon)+"時間先の平均誤差 "+(count>0?Number(error)+"ポイント":"未評価")+
                "。作業量が変わると外れることがあります。"+(string.IsNullOrEmpty(storage)?"":"\n"+storage);
            return pace;
        }
    }
}
