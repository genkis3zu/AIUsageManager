using System;
using System.Drawing;
using System.Diagnostics;
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
        readonly ToolStripMenuItem widgetMenu = new ToolStripMenuItem("Windows ウィジェット");
        readonly ThemeSettings settings;
        readonly ILoginLauncher loginLauncher;
        readonly ToolStripMenuItem loginMenu = new ToolStripMenuItem("ログイン");
        readonly ToolStripMenuItem notificationMenu = new ToolStripMenuItem("通知");
        readonly UsageAlerts alerts;
        readonly ToolStripMenuItem learningMenu = new ToolStripMenuItem("予測の学習");
        readonly Action<string> notify;
        readonly SnapshotServer snapshots;
        readonly RegisteredWaitHandle refreshWait;
        ServiceUsage[] latest=WidgetCards.Unavailable();
        readonly RegisteredWaitHandle[] loginWaits = new RegisteredWaitHandle[2];
        ThemeMode themeMode;
        NotificationMode notificationMode;
        bool refreshing, exiting, loggingIn, clearingHistory;
        DateTimeOffset? nextRefresh;
        public TrayContext(bool show, EventWaitHandle signal) : this(show, signal, null) { }
        public TrayContext(bool show, EventWaitHandle signal, IUsageSource source) : this(show, signal, source, ThemeSettings.Default()) { }
        internal TrayContext(bool show, EventWaitHandle signal, IUsageSource source, ThemeSettings themeSettings)
            : this(show,signal,source,themeSettings,new LoginLauncher(),null) { }
        internal TrayContext(bool show, EventWaitHandle signal, IUsageSource source, ThemeSettings themeSettings, ILoginLauncher launcher, EventWaitHandle[] loginSignals)
            : this(show,signal,source,themeSettings,launcher,loginSignals,null) { }
        internal TrayContext(bool show, EventWaitHandle signal, IUsageSource source, ThemeSettings themeSettings, ILoginLauncher launcher, EventWaitHandle[] loginSignals, Action<string> notification)
            : this(show,signal,source,themeSettings,launcher,loginSignals,notification,null,null) { }
        internal TrayContext(bool show, EventWaitHandle signal, IUsageSource source, ThemeSettings themeSettings, ILoginLauncher launcher, EventWaitHandle[] loginSignals, Action<string> notification, SnapshotServer snapshotServer, EventWaitHandle refreshSignal)
        {
            snapshots=snapshotServer;
            alerts=new UsageAlerts(source==null?UsageForecaster.Default():new UsageForecaster());
            loginLauncher=launcher;
            settings=themeSettings;themeMode=settings.Load();notificationMode=settings.LoadNotifications();
            service = source ?? new UsageService(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), http);
            showEvent=signal;
            // Create the native handle before scheduling messages onto the UI thread.
            IntPtr handle=popup.Handle;
            tray=new NotifyIcon { Icon=Artwork.TrayIcon(),Text="AI Usage Manager",Visible=true,ContextMenuStrip=menu };
            notify=notification??(text=>tray.ShowBalloonTip(8000,"AI 使用量の警告",text,ToolTipIcon.Warning));
            tray.BalloonTipClicked+=(s,e)=>ShowPopup();
            menu.Items.Add("使用量を表示",null,(s,e)=>ShowPopup());
            menu.Items.Add("更新",null,async (s,e)=>await RefreshUsage());
            foreach(string name in new[]{"Codex","Grok"}) { string serviceName=name;loginMenu.DropDownItems.Add(name+" にログイン",null,async(s,e)=>await Login(serviceName)); }
            menu.Items.Add(loginMenu);
            AddThemeChoice("Windowsに合わせる",ThemeMode.System);
            AddThemeChoice("ライト",ThemeMode.Light);
            AddThemeChoice("ダーク",ThemeMode.Dark);
            menu.Items.Add(themeMenu);
            AddNotificationChoice("上限・急増・到達予測",NotificationMode.All);
            AddNotificationChoice("90%・95%・100%のみ",NotificationMode.Thresholds);
            AddNotificationChoice("通知しない（画面には表示）",NotificationMode.Off);
            menu.Items.Add(notificationMenu);
            learningMenu.DropDownItems.Add("学習の状態",null,(s,e)=>MessageBox.Show(alerts.LearningStatus,"予測の学習",MessageBoxButtons.OK,MessageBoxIcon.Information));
            learningMenu.DropDownItems.Add("学習履歴を削除",null,(s,e)=> {
                ClearLearningHistory(()=>MessageBox.Show("このアプリに保存した使用量の学習履歴を削除し、新しく学習します。各サービスのログやログイン情報は変更しません。","学習履歴を削除",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes);
            });
            menu.Items.Add(learningMenu);
            widgetMenu.DropDownItems.Add("ウィジェットを開く",null,(s,e)=>OpenWidgetLink("ms-widgets:"));
            widgetMenu.DropDownItems.Add("追加・削除の手順",null,(s,e)=>MessageBox.Show(
                "登録済み: Win+W → ＋ → AI Usage Manager を追加します。\n右上の … から中／大サイズを選べます。\n\n未登録: ウィジェット用ZIPを展開し、install-widgets.ps1 をWindows PowerShellで実行します。\n削除: 同梱の uninstall-widgets.ps1 を実行します。\n\n必要: Windows 11 x64、Windows App Runtime 2.5.1 x64以降、開発者モード。\n詳しくはウィジェット用ZIPのREADME.mdを参照してください。",
                "Windows ウィジェット",MessageBoxButtons.OK,MessageBoxIcon.Information));
            menu.Items.Add(widgetMenu);
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
            popup.LoginRequested+=async name=>await Login(name);
            if(loginSignals!=null)for(int i=0;i<loginWaits.Length;i++) {
                string name=i==0?"Codex":"Grok";
                loginWaits[i]=ThreadPool.RegisterWaitForSingleObject(loginSignals[i],(state,timedOut)=> {
                    try { if(!exiting)popup.BeginInvoke(new Action(async()=>await Login(name))); }catch(InvalidOperationException){}
                },null,Timeout.Infinite,false);
            }
            refreshTimer.Tick+=async(s,e)=>await RefreshUsage();
            displayTimer.Tick+=(s,e)=>popup.TickDisplay();displayTimer.Start();
            showWait=ThreadPool.RegisterWaitForSingleObject(showEvent,(state,timedOut)=> {
                try { if(!exiting)popup.BeginInvoke(new Action(ShowPopup)); }catch(InvalidOperationException){}
            },null,Timeout.Infinite,false);
            if(refreshSignal!=null)refreshWait=ThreadPool.RegisterWaitForSingleObject(refreshSignal,(state,timedOut)=> {
                try {if(!exiting)popup.BeginInvoke(new Action(async()=>await RefreshUsage()));}catch(InvalidOperationException){}
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
        void OpenWidgetLink(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute=true }); }
            catch { tray.ShowBalloonTip(4000,"Windows ウィジェット","開けませんでした。Win+Wでウィジェットを開いてください。追加手順はGitHubのREADMEから確認できます。",ToolTipIcon.Info); }
        }
        void AddNotificationChoice(string label,NotificationMode mode)
        {
            var item=new ToolStripMenuItem(label) { Tag=mode };
            item.Click+=(s,e)=> {
                notificationMode=mode;ApplyTheme();
                if(!settings.SaveNotifications(mode))tray.ShowBalloonTip(4000,"通知の保存","設定を保存できませんでした。今回の起動中のみ適用します。",ToolTipIcon.Warning);
            };
            notificationMenu.DropDownItems.Add(item);
        }
        void ApplyTheme()
        {
            if(exiting)return;
            var palette=ThemePalette.Resolve(themeMode);
            popup.ApplyTheme(palette);
            var renderer=new ThemeMenuRenderer(palette);
            menu.Renderer=renderer;menu.BackColor=palette.Surface;menu.ForeColor=palette.Ink;
            themeMenu.DropDown.Renderer=renderer;themeMenu.DropDown.BackColor=palette.Surface;themeMenu.DropDown.ForeColor=palette.Ink;
            widgetMenu.DropDown.Renderer=renderer;widgetMenu.DropDown.BackColor=palette.Surface;widgetMenu.DropDown.ForeColor=palette.Ink;
            loginMenu.DropDown.Renderer=renderer;loginMenu.DropDown.BackColor=palette.Surface;loginMenu.DropDown.ForeColor=palette.Ink;
            notificationMenu.DropDown.Renderer=renderer;notificationMenu.DropDown.BackColor=palette.Surface;notificationMenu.DropDown.ForeColor=palette.Ink;
            learningMenu.DropDown.Renderer=renderer;learningMenu.DropDown.BackColor=palette.Surface;learningMenu.DropDown.ForeColor=palette.Ink;
            foreach(ToolStripMenuItem item in themeMenu.DropDownItems)item.Checked=(ThemeMode)item.Tag==themeMode;
            foreach(ToolStripMenuItem item in notificationMenu.DropDownItems)item.Checked=(NotificationMode)item.Tag==notificationMode;
            menu.Invalidate(true);themeMenu.DropDown.Invalidate(true);
        }
        void OnUserPreferenceChanged(object sender,UserPreferenceChangedEventArgs e)
        {
            // SystemEvents can arrive on a worker thread. All controls stay on the UI thread.
            try { if(!exiting)popup.BeginInvoke(new Action(ApplyTheme)); }catch(InvalidOperationException) { }
        }
        void ShowPopup() { if(!exiting){ApplyTheme();popup.OpenAt(Cursor.Position);} }
        internal bool ClearLearningHistory(Func<bool> confirm)
        {
            if(refreshing||exiting||clearingHistory)return false;
            // A modal confirmation pumps UI messages, including timer and remote refresh events.
            clearingHistory=true;
            try {
                if(!confirm()||exiting)return false;
                bool cleared=alerts.ClearHistory();
                foreach(var value in latest)foreach(var window in value.Windows){window.Pace=null;window.Alert=null;}
                popup.SetUsage(latest);PublishSnapshot();
                if(!cleared)MessageBox.Show("保存履歴を削除できませんでした。学習の状態を確認してください。","予測の学習",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return cleared;
            } finally {clearingHistory=false;}
        }
        async Task Login(string name)
        {
            if(loggingIn||exiting)return;
            loggingIn=true;loginMenu.Enabled=false;popup.SetLoginBusy(true);popup.Hide();
            try {
                string error=await loginLauncher.LoginAsync(name);
                if(exiting)return;
                if(error!=null)MessageBox.Show(error,name+" ログイン",MessageBoxButtons.OK,MessageBoxIcon.Information);
                // Let an earlier request finish so it cannot overwrite the post-login result.
                while(refreshing&&!exiting)await Task.Delay(100);
                if(!exiting){await RefreshUsage();ShowPopup();}
            } finally {loggingIn=false;if(!exiting){loginMenu.Enabled=true;popup.SetLoginBusy(false);}}
        }
        async Task RefreshUsage()
        {
            if(refreshing||exiting||clearingHistory)return;
            refreshing=true;refreshTimer.Stop();popup.SetRefresh(true,nextRefresh);menu.Items[1].Enabled=false;learningMenu.Enabled=false;
            PublishSnapshot();
            try
            {
                var result=await service.FetchAllAsync(cancellation.Token);
                if(exiting)return;
                var mode=notificationMode;
                var notices=await Task.Run(()=> {
                    var observed=alerts.Observe(result,DateTimeOffset.Now,mode);
                    alerts.SaveHistory(DateTimeOffset.Now);return observed;
                });
                if(exiting)return;
                latest=result;
                popup.SetUsage(result);
                string summary=string.Join(" | ",result.Select(r=>r.Name+" "+string.Join("/",r.Windows.Select(w=>UsageText.Percent(w.Percent)))));
                tray.Text=summary.Length>63?summary.Substring(0,63):summary;
                // OS notification delivery must never turn a successful fetch into an error.
                if(notices.Length>0)try { notify(UsageAlerts.NotificationText(notices)); }catch(Exception) { }
            }
            catch(Exception)
            {
                // Never show exception text: it can contain request/credential or local-file data.
                if(!exiting){latest=WidgetCards.Unavailable();popup.SetUsage(latest);}
            }
            finally
            {
                refreshing=false;
                if(!exiting)PublishSnapshot();
                if(!exiting) {nextRefresh=DateTimeOffset.Now.AddMinutes(5);popup.SetRefresh(false,nextRefresh);menu.Items[1].Enabled=true;learningMenu.Enabled=true;refreshTimer.Start();}
            }
        }
        void PublishSnapshot() { if(snapshots!=null)snapshots.Publish(UsageSnapshot.Create(latest,DateTimeOffset.Now,refreshing)); }
        protected override void ExitThreadCore()
        {
            if(exiting)return;exiting=true;
            SystemEvents.UserPreferenceChanged-=OnUserPreferenceChanged;
            refreshTimer.Stop();displayTimer.Stop();cancellation.Cancel();showWait.Unregister(null);
            if(refreshWait!=null)refreshWait.Unregister(null);
            foreach(var wait in loginWaits)if(wait!=null)wait.Unregister(null);
            tray.Visible=false;var icon=tray.Icon;tray.Dispose();icon.Dispose();menu.Dispose();popup.Dispose();
            refreshTimer.Dispose();displayTimer.Dispose();http.Dispose();cancellation.Dispose();base.ExitThreadCore();
        }
    }
}
