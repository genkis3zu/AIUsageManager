using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace AIUsageManager
{
    public static class WidgetCards
    {
        static Dictionary<string,object> O(params object[] pairs)
        {
            var result=new Dictionary<string,object>();
            for(int i=0;i<pairs.Length;i+=2)result.Add((string)pairs[i],pairs[i+1]);
            return result;
        }
        static object Text(string text,string color,bool subtle)
        {return O("type","TextBlock","text",text,"size","small","spacing","none","wrap",false,"color",color,"isSubtle",subtle);}
        static string Bar(double? percent,int width)
        {
            using(var bitmap=new Bitmap(width,6))using(var g=Graphics.FromImage(bitmap))using(var output=new MemoryStream())
            {
                g.Clear(Color.FromArgb(110,128,138,156));
                if(percent.HasValue&&percent>0)
                    using(var brush=new SolidBrush(percent>=80?Color.FromArgb(240,87,106):Color.FromArgb(129,178,255)))
                        g.FillRectangle(brush,0,0,(float)(width*Math.Min(100,percent.Value)/100),6);
                bitmap.Save(output,ImageFormat.Png);return "data:image/png;base64,"+Convert.ToBase64String(output.ToArray());
            }
        }
        static string Icon(string name)
        {
            using(var bitmap=new Bitmap(28,28))using(var g=Graphics.FromImage(bitmap))using(var output=new MemoryStream()) {
                g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Artwork.Provider(g,name,0,0,ThemePalette.Light);bitmap.Save(output,ImageFormat.Png);
                return "data:image/png;base64,"+Convert.ToBase64String(output.ToArray());
            }
        }
        static object Row(ServiceUsage value,DateTimeOffset now,bool large)
        {
            var header=O("type","RichTextBlock","spacing","none","inlines",new object[]{
                O("type","TextRun","text",value.Name+" · ","weight","bolder"),
                O("type","TextRun","text",value.Plan+(string.IsNullOrEmpty(value.PlanNote)?"":" *"),"size","small","isSubtle",true)});
            var columns=new List<object>();
            foreach(var window in value.Windows)
            {
                string reset=UsageText.Reset(window,now).Replace("リセットまで ","");
                string compactReset=reset.Replace("期限経過・再取得待ち","期限経過").Replace("リセット時刻不明","時刻不明").Replace("時間","h").Replace("日","d").Replace("分","m").Replace(" ","");
                string label=large?window.Label:window.Label.Replace("時間","h");
                var windowItems=new List<object>{
                    Text(label+"  "+UsageText.Percent(window.Percent)+(large?"":" · "+compactReset),window.Percent>=80?"attention":"default",false),
                    O("type","Image","url",Bar(window.Percent,value.Windows.Count==2?180:360),"size","stretch","spacing","none","altText",window.Label+" "+UsageText.Percent(window.Percent)+" リセット "+reset)};
                if(large)windowItems.Add(Text(window.ResetsAt.HasValue&&window.ResetsAt>now?"リセット "+reset:reset,"default",true));
                columns.Add(O("type","Column","width","stretch","spacing","small","items",windowItems));
            }
            string stamp=value.RecordedAt.HasValue?value.RecordedAt.Value.ToLocalTime().ToString("MM/dd HH:mm"):(value.HasData?"時刻不明":"未取得");
            string source=string.IsNullOrEmpty(value.Source)?"未取得":value.Source;
            if(large&&value.RecordedAt.HasValue&&now-value.RecordedAt.Value>TimeSpan.FromMinutes(15))source+=" · 保存された値";
            var title=O("type","ColumnSet","spacing","none","columns",new object[]{
                O("type","Column","width","20px","items",new object[]{O("type","Image","url",Icon(value.Name),"width","20px","height","20px","altText",value.Name)}),
                O("type","Column","width","stretch","spacing","small","items",new object[]{header})});
            var items=new List<object>{title,O("type","ColumnSet","spacing","none","columns",columns),Text("取得 "+stamp+(large?" · "+source:""),"default",true)};
            if(large&&!string.IsNullOrEmpty(value.Status))items.Add(O("type","TextBlock","text",value.Status,"size","small","spacing","none","wrap",true,"maxLines",2,"color","warning"));
            return O("type","Container","spacing",large?"medium":"small","separator",value.Name!="Claude","items",items);
        }
        static object Card(ServiceUsage[] values,DateTimeOffset now,bool large)
        {
            return O("$schema","http://adaptivecards.io/schemas/adaptive-card.json","type","AdaptiveCard","version","1.5",
                "body",values.Select(value=>Row(value,now,large)).ToArray(),
                "actions",new object[]{O("type","Action.Execute","title","更新","verb","refresh"),O("type","Action.Execute","title","アプリを開く","verb","open")});
        }
        public static string Create(ServiceUsage[] values,DateTimeOffset now)
        {
            if(values==null||values.Length!=3)throw new ArgumentException("Three service rows required");
            return new JavaScriptSerializer().Serialize(O("medium",Card(values,now,false),"large",Card(values,now,true)));
        }
        public static ServiceUsage[] Unavailable()
        {return new[]{ServiceUsage.Empty("Claude","取得できません・更新で再試行"),ServiceUsage.Empty("Codex","取得できません・更新で再試行"),ServiceUsage.Empty("Grok","取得できません・更新で再試行")};}
        public static ServiceUsage[] Sample(DateTimeOffset now)
        {
            var values=Unavailable();
            for(int i=0;i<values.Length;i++){values[i].RecordedAt=now;values[i].Source="サンプル";values[i].Status="";values[i].Plan=i==0?"Max":i==1?"Plus":"SuperGrok";foreach(var window in values[i].Windows){window.Percent=i==0?82:i==1?50:25;window.ResetsAt=now.AddDays(2);}}
            values[0].Windows[0].Percent=5;values[0].Windows[0].ResetsAt=now.AddHours(4);return values;
        }
    }
}
