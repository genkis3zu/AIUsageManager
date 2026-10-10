using System;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AIUsageManager
{
    internal sealed class TrayContext : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly PopupForm popup = new PopupForm();
        readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 300000 };
        readonly System.Windows.Forms.Timer displayTimer = new System.Windows.Forms.Timer { Interval = 15000 };
        readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        readonly HttpClient http = UsageService.CreateHttpClient();
        readonly IUsageSource service;
        readonly EventWaitHandle showEvent;
        readonly RegisteredWaitHandle showWait;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem themeMenu = new ToolStripMenuItem("テーマ");
        readonly ThemeSettings settings;
        ThemeMode themeMode;
        bool refreshing, exiting;
        DateTimeOffset? nextRefresh;
        public TrayContext(bool show, EventWaitHandle signal) : this(show, signal, null) { }
        public TrayContext(bool show, EventWaitHandle signal, IUsageSource source) : this(show, signal, source, ThemeSettings.Default()) { }
        internal TrayContext(bool show, EventWaitHandle signal, IUsageSource source, ThemeSettings themeSettings)
        {
            settings=themeSettings;themeMode=settings.Load();
            service = source ?? new UsageService(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), http);
            showEvent=signal;
            // Create the native handle before scheduling messages onto the UI thread.
            IntPtr handle=popup.Handle;
            tray=new NotifyIcon { Icon=Artwork.TrayIcon(),Text="AI Usage Manager",Visible=true,ContextMenuStrip=menu };
            menu.Items.Add("使用量を表示",null,(s,e)=>ShowPopup());
            menu.Items.Add("更新",null,async (s,e)=>await RefreshUsage());
            AddThemeChoice("Windowsに合わせる",ThemeMode.System);
            AddThemeChoice("ライト",ThemeMode.Light);
            AddThemeChoice("ダーク",ThemeMode.Dark);
            menu.Items.Add(themeMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("終了",null,(s,e)=>ExitThread());
            ApplyTheme();
            menu.Opening+=(s,e)=>ApplyTheme();
            SystemEvents.UserPreferenceChanged+=OnUserPreferenceChanged;
            tray.MouseClick+=(s,e)=> {
                if(e.Button!=MouseButtons.Left)return;
                if(popup.Visible)popup.Hide();
                else if((DateTimeOffset.UtcNow-popup.LastDeactivated).TotalMilliseconds>350)ShowPopup();
            };
            popup.RefreshRequested+=async(s,e)=>await RefreshUsage();
            refreshTimer.Tick+=async(s,e)=>await RefreshUsage();
            displayTimer.Tick+=(s,e)=>popup.TickDisplay();displayTimer.Start();
            showWait=ThreadPool.RegisterWaitForSingleObject(showEvent,(state,timedOut)=> {
                try { if(!exiting)popup.BeginInvoke(new Action(ShowPopup)); }catch(InvalidOperationException){}
            },null,Timeout.Infinite,false);
            popup.BeginInvoke(new Action(async()=> { if(show)ShowPopup();await RefreshUsage(); }));
        }
        void AddThemeChoice(string label,ThemeMode mode)
        {
            var item=new ToolStripMenuItem(label) { Tag=mode };
            item.Click+=(s,e)=> {
                themeMode=mode;ApplyTheme();
                if(!settings.Save(mode))tray.ShowBalloonTip(4000,"テーマの保存","設定を保存できませんでした。今回の起動中のみ適用します。",ToolTipIcon.Warning);
            };
            themeMenu.DropDownItems.Add(item);
        }
        void ApplyTheme()
        {
            if(exiting)return;
            var palette=ThemePalette.Resolve(themeMode);
            popup.ApplyTheme(palette);
            var renderer=new ThemeMenuRenderer(palette);
            menu.Renderer=renderer;menu.BackColor=palette.Surface;menu.ForeColor=palette.Ink;
            themeMenu.DropDown.Renderer=renderer;themeMenu.DropDown.BackColor=palette.Surface;themeMenu.DropDown.ForeColor=palette.Ink;
            foreach(ToolStripMenuItem item in themeMenu.DropDownItems)item.Checked=(ThemeMode)item.Tag==themeMode;
            menu.Invalidate(true);themeMenu.DropDown.Invalidate(true);
        }
        void OnUserPreferenceChanged(object sender,UserPreferenceChangedEventArgs e)
        {
            // SystemEvents can arrive on a worker thread. All controls stay on the UI thread.
            try { if(!exiting)popup.BeginInvoke(new Action(ApplyTheme)); }catch(InvalidOperationException) { }
        }
        void ShowPopup() { if(!exiting){ApplyTheme();popup.OpenAt(Cursor.Position);} }
        async Task RefreshUsage()
        {
            if(refreshing||exiting)return;
            refreshing=true;refreshTimer.Stop();popup.SetRefresh(true,nextRefresh);menu.Items[1].Enabled=false;
            try
            {
                var result=await service.FetchAllAsync(cancellation.Token);
                if(exiting)return;
                popup.SetUsage(result);
                string summary=string.Join(" | ",result.Select(r=>r.Name+" "+string.Join("/",r.Windows.Select(w=>UsageText.Percent(w.Percent)))));
                tray.Text=summary.Length>63?summary.Substring(0,63):summary;
            }
            catch(Exception)
            {
                // Never show exception text: it can contain request/credential or local-file data.
                if(!exiting)popup.SetUsage(new[]{ServiceUsage.Empty("Claude","取得できません・次回更新で再試行"),ServiceUsage.Empty("Codex","取得できません・次回更新で再試行"),ServiceUsage.Empty("Grok","取得できません・次回更新で再試行")});
            }
            finally
            {
                refreshing=false;
                if(!exiting) {nextRefresh=DateTimeOffset.Now.AddMinutes(5);popup.SetRefresh(false,nextRefresh);menu.Items[1].Enabled=true;refreshTimer.Start();}
            }
        }
        protected override void ExitThreadCore()
        {
            if(exiting)return;exiting=true;
            SystemEvents.UserPreferenceChanged-=OnUserPreferenceChanged;
            refreshTimer.Stop();displayTimer.Stop();cancellation.Cancel();showWait.Unregister(null);
            tray.Visible=false;var icon=tray.Icon;tray.Dispose();icon.Dispose();menu.Dispose();popup.Dispose();
            refreshTimer.Dispose();displayTimer.Dispose();http.Dispose();cancellation.Dispose();base.ExitThreadCore();
        }
    }
}
