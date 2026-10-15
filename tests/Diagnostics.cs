using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AIUsageManager;

internal static class Diagnostics
{
    [STAThread]
    static int Main(string[] args)
    {
        System.Net.ServicePointManager.SecurityProtocol=System.Net.SecurityProtocolType.Tls12;
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        ServiceUsage[] values;
        if(args.Contains("--live"))
        {
            using(var client=UsageService.CreateHttpClient())values=new UsageService(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),client).FetchAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        else
        {
            var now=DateTimeOffset.Now;
            var claude=ServiceUsage.Empty("Claude","");claude.Plan="Max 20x";claude.Source="オンライン";claude.RecordedAt=now;
            claude.Windows[0].Percent=1;claude.Windows[0].ResetsAt=now.AddHours(4.5);claude.Windows[1].Percent=98;claude.Windows[1].ResetsAt=now.AddDays(2);
            var codex=ServiceUsage.Empty("Codex","");codex.Plan="Pro Lite";codex.Source="CLI 記録";codex.RecordedAt=now.AddHours(-3);codex.Windows[0].Percent=50;codex.Windows[0].ResetsAt=now.AddDays(3);
            var grok=ServiceUsage.Empty("Grok","");grok.Plan="SuperGrok Heavy";grok.Source="オンライン";grok.RecordedAt=now;grok.Windows[0].Percent=42;grok.Windows[0].ResetsAt=now.AddDays(2.5);
            values=new[]{claude,codex,grok};
            if(args.Contains("--alerts")) {
                var alerts=new UsageAlerts();
                codex.Source="オンライン";
                for(int i=0;i<3;i++) {
                    foreach(var value in values)value.RecordedAt=now.AddMinutes(-10+5*i);
                    claude.Windows[0].Percent=6;claude.Windows[1].Percent=100;
                    codex.Windows[0].Percent=20+15*i;grok.Windows[0].Percent=89+3*i;
                    alerts.Observe(values,now.AddMinutes(-10+5*i),NotificationMode.Off);
                }
            }
            if(args.Contains("--forecast")) {
                var alerts=new UsageAlerts();codex.Source="オンライン";
                values[2]=ServiceUsage.Empty("Grok","再ログインが必要です");values[2].NeedsLogin=true;
                var start=now.AddHours(-324);
                for(int i=0;i<=324;i++) {
                    claude.RecordedAt=codex.RecordedAt=start.AddHours(i);
                    claude.Windows[0].Percent=(i%5)*6;claude.Windows[0].ResetsAt=start.AddHours((i/5+1)*5);
                    claude.Windows[1].Percent=(i%168)*0.3;claude.Windows[1].ResetsAt=start.AddDays((i/168+1)*7);
                    codex.Windows[0].Percent=(i%168)*0.5;codex.Windows[0].ResetsAt=claude.Windows[1].ResetsAt;
                    alerts.Observe(values,start.AddHours(i),NotificationMode.Off);
                }
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"forecast-sample.json"),UsageSnapshot.Create(values,now,false));
            }
        }
        foreach(var value in values)
            Console.WriteLine(value.Name+" | "+value.Plan+" | "+string.Join(", ",value.Windows.Select(w=>w.Label+"="+UsageText.Percent(w.Percent)))+" | "+value.Source+" | "+(value.RecordedAt.HasValue?value.RecordedAt.Value.ToLocalTime().ToString("s"):"unknown time")+" | "+value.Status);
        string output=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"screenshots");Directory.CreateDirectory(output);
        using(var form=new PopupForm())
        {
            string theme=args.Contains("--dark")?"dark":"light";
            form.SetTheme(args.Contains("--dark")?ThemeMode.Dark:ThemeMode.Light);
            form.Show();Application.DoEvents();
            form.SetUsage(values);form.SetRefresh(false,DateTimeOffset.Now.AddMinutes(5));
            foreach(float scale in new[]{1f,1.5f,2f})
            {
                typeof(PopupForm).GetField("scale",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,scale);
                form.SetUsage(values);
                using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(output,(args.Contains("--live")?"live":args.Contains("--forecast")?"forecast":args.Contains("--alerts")?"alerts":"sample")+"-"+theme+"-"+(int)(scale*100)+".png"),ImageFormat.Png);}
            }
            var missing=ServiceUsage.Empty("Claude","CLI で再ログインが必要です");
            var zero=ServiceUsage.Empty("Codex","");zero.Plan="Pro Lite";zero.Windows[0].Percent=0;zero.RecordedAt=DateTimeOffset.Now;
            var expired=ServiceUsage.Empty("Grok","再ログインが必要です");expired.NeedsLogin=true;
            typeof(PopupForm).GetField("scale",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,1f);form.SetUsage(new[]{missing,zero,expired});
            using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(output,"states-"+theme+"-100.png"),ImageFormat.Png);}
        }
        return 0;
    }
}
