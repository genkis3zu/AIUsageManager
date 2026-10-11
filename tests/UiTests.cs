using System;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AIUsageManager;
using Microsoft.Win32;

internal static class UiTests
{
    sealed class FakeSource : IUsageSource
    {
        public int Calls;
        TaskCompletionSource<ServiceUsage[]> pending;
        public Task<ServiceUsage[]> FetchAllAsync(CancellationToken cancellation)
        { Calls++;pending=new TaskCompletionSource<ServiceUsage[]>();return pending.Task; }
        public void Complete()
        { pending.SetResult(new[]{ServiceUsage.Empty("Claude",""),ServiceUsage.Empty("Codex",""),ServiceUsage.Empty("Grok","")}); }
    }
    sealed class FakeLogin : ILoginLauncher
    {
        public int Calls;
        public string Service;
        public readonly TaskCompletionSource<string> Pending=new TaskCompletionSource<string>();
        public Task<string> LoginAsync(string service) { Calls++;Service=service;return Pending.Task; }
    }
    static T Field<T>(object o,string name) { return (T)o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o); }
    static int passed;
    static void Assert(bool value,string name) { if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS "+name); }
    static bool Pump(Func<bool> done)
    {
        var until=DateTime.UtcNow.AddSeconds(3);
        while(!done()&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(5);}return done();
    }
    static void CheckSettings(ThemeSettings settings,string path)
    {
        Assert(settings.Load()==ThemeMode.System,"Missing preference follows Windows");
        File.WriteAllText(path,"invalid setting");
        Assert(settings.Load()==ThemeMode.System,"Malformed preference follows Windows");
        foreach(var mode in new[]{ThemeMode.Light,ThemeMode.System,ThemeMode.Dark})
            Assert(settings.Save(mode)&&new ThemeSettings(path).Load()==mode,"Preference persists "+mode);
        Assert(!new ThemeSettings(Path.Combine(path,"settings.ini")).Save(ThemeMode.Dark),"Unwritable preference returns failure without throwing");
        Assert(ThemePalette.Select(ThemeMode.System,true).IsDark&&!ThemePalette.Select(ThemeMode.System,false).IsDark,"Automatic palette follows both Windows appearances");
        Assert(ThemePalette.Select(ThemeMode.Dark,false).IsDark&&!ThemePalette.Select(ThemeMode.Light,true).IsDark,"Explicit palette overrides Windows appearance");
    }
    static void CaptureMenu(ContextMenuStrip menu,ToolStripMenuItem themes)
    {
        string output=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"screenshots");Directory.CreateDirectory(output);
        menu.Show(new Point(30,30));themes.ShowDropDown();Application.DoEvents();
        using(var image=new Bitmap(menu.Width,menu.Height)) {
            menu.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(output,"menu-dark.png"),ImageFormat.Png);
        }
        using(var image=new Bitmap(themes.DropDown.Width,themes.DropDown.Height)) {
            themes.DropDown.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(output,"theme-menu-dark.png"),ImageFormat.Png);
        }
        themes.HideDropDown();menu.Close();
    }
    [STAThread]
    static int Main()
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        TrayContext context=null;
        try
        {
            string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
            string path=Path.Combine(fixture,"settings.ini");var settings=new ThemeSettings(path);CheckSettings(settings,path);
            using(var signal=new EventWaitHandle(false,EventResetMode.AutoReset))
            {
                var source=new FakeSource();var login=new FakeLogin();context=new TrayContext(false,signal,source,settings,login,null);
                var popup=Field<PopupForm>(context,"popup");var refresh=Field<Button>(popup,"refresh");
                var row=popup.Controls.OfType<UsageRow>().Last();
                var authMissing=ServiceUsage.Empty("Grok","ログインが必要です");authMissing.NeedsLogin=true;
                row.Usage=authMissing;
                Assert(row.Controls.OfType<Button>().Any(b=>b.Text.Contains("ログイン")),"Authentication failure exposes a login button");
                var timer=Field<System.Windows.Forms.Timer>(context,"refreshTimer");
                var themes=Field<ToolStripMenuItem>(context,"themeMenu");var menu=Field<ContextMenuStrip>(context,"menu");
                var widgets=Field<ToolStripMenuItem>(context,"widgetMenu");
                var dark=(ToolStripMenuItem)themes.DropDownItems[2];var light=(ToolStripMenuItem)themes.DropDownItems[1];var automatic=(ToolStripMenuItem)themes.DropDownItems[0];
                var darkPalette=ThemePalette.Resolve(ThemeMode.Dark);var lightPalette=ThemePalette.Resolve(ThemeMode.Light);
                Assert(dark.Checked&&popup.BackColor==darkPalette.Surface,"Startup restores selected dark appearance");
                Assert(timer.Interval==300000,"Default refresh is five minutes");
                Assert(Pump(()=>source.Calls==1),"Startup refresh runs on the message loop");
                refresh.PerformClick();Application.DoEvents();Assert(source.Calls==1&&!refresh.Enabled,"Refresh overlap blocked while loading");
                source.Complete();Assert(Pump(()=>refresh.Enabled),"Background completion enables refresh");
                Assert(popup.Controls.Cast<Control>().All(c=>c.BackColor==darkPalette.Surface),"Data refresh retains dark rows and buttons");
                CaptureMenu(menu,themes);
                light.PerformClick();
                Assert(light.Checked&&!dark.Checked&&!automatic.Checked&&popup.BackColor==lightPalette.Surface&&settings.Load()==ThemeMode.Light,"Menu immediately applies and saves light appearance");
                Assert(menu.BackColor==lightPalette.Surface&&themes.DropDown.BackColor==lightPalette.Surface,"Both menu levels follow selected appearance");
                Assert(widgets.DropDownItems.Count==2&&widgets.DropDown.BackColor==lightPalette.Surface,"Widget actions are available and follow the selected appearance");
                automatic.PerformClick();
                Assert(automatic.Checked&&!light.Checked&&!dark.Checked&&settings.Load()==ThemeMode.System,"Automatic appearance is independently selectable and saved");
                var systemPalette=ThemePalette.Resolve(ThemeMode.System);
                popup.ApplyTheme(systemPalette.IsDark?ThemePalette.Light:ThemePalette.Dark);
                var preference=typeof(TrayContext).GetMethod("OnUserPreferenceChanged",BindingFlags.Instance|BindingFlags.NonPublic);
                Task.Run(()=>preference.Invoke(context,new object[]{null,new UserPreferenceChangedEventArgs(UserPreferenceCategory.General)})).Wait();
                Assert(Pump(()=>popup.BackColor==systemPalette.Surface),"System preference notification safely updates UI from worker thread");
                dark.PerformClick();
                Assert(dark.Checked&&popup.BackColor==darkPalette.Surface&&settings.Load()==ThemeMode.Dark,"Dark appearance can be restored without restarting");
                signal.Set();Assert(Pump(()=>popup.Visible),"Second-instance signal opens popup");
                Assert(Screen.FromControl(popup).WorkingArea.Contains(popup.Bounds),"Popup fits current monitor work area");
                refresh.PerformClick();Assert(Pump(()=>source.Calls==2),"Manual refresh requests new values");
                source.Complete();Assert(Pump(()=>refresh.Enabled),"Manual refresh finishes");
                popup.Close();Application.DoEvents();Assert(!popup.Visible&&!popup.IsDisposed,"Window close hides and retains tray popup");
                var shutdown=new FormClosingEventArgs(CloseReason.WindowsShutDown,false);
                typeof(PopupForm).GetMethod("OnFormClosing",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(popup,new object[]{shutdown});
                Assert(!shutdown.Cancel,"Popup never vetoes Windows shutdown");
                timer.Interval=35;
                Assert(Pump(()=>source.Calls==3),"Real timer tick triggers background refresh");
                Application.DoEvents();Assert(source.Calls==3&&!timer.Enabled,"Timer pauses while request pending");
                source.Complete();Assert(Pump(()=>refresh.Enabled),"Timer refresh completes");
                timer.Stop();popup.OpenAt(new Point(300,500));
                row.Usage=authMissing;
                row.Controls.OfType<Button>().Single().PerformClick();
                Assert(Pump(()=>login.Calls==1)&&login.Service=="Grok"&&!popup.Visible,"Login button starts official authentication for its service");
                var loginMenu=Field<ToolStripMenuItem>(context,"loginMenu");
                loginMenu.DropDownItems[0].PerformClick();Application.DoEvents();Assert(login.Calls==1,"Login launches cannot overlap");
                login.Pending.SetResult(null);Assert(Pump(()=>source.Calls==4),"OAuth completion automatically refreshes usage");
                source.Complete();Assert(Pump(()=>popup.Visible&&loginMenu.Enabled),"Completed login restores popup and controls");
                context.ExitThread();Assert(popup.IsDisposed,"Exit releases popup and tray resources");context=null;
            }
            Console.WriteLine("RESULT "+passed+" UI checks passed");return 0;
        }
        catch(Exception e){Console.WriteLine("FAIL UI check: "+e.Message);return 1;}
        finally{if(context!=null)context.ExitThread();}
    }
}
