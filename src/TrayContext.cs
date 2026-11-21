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
        // AIの状況: public status pages read with each refresh, plus weekly reset notices.
        readonly Func<CancellationToken,Task<ProviderStatus[]>> statusPages;
        readonly ResetTracker resets=new ResetTracker();
        StatusSummary latestStatus;
        // 対応待ち: Claude Code and Codex sessions waiting for the user, read every 5 seconds without changing any settings.
        readonly IWaitingSource waitingSource;
        readonly WaitingTracker waitingTracker=new WaitingTracker();
        readonly WaitingReminders waitingReminders=new WaitingReminders();
        readonly System.Windows.Forms.Timer waitingTimer=new System.Windows.Forms.Timer { Interval = 5000 };
        readonly ToolStripMenuItem waitingMenu=new ToolStripMenuItem("対応待ちの通知");
        readonly ToolStripMenuItem startupMenu=new ToolStripMenuItem("Windowsのサインイン時に起動");
        readonly IStartupStore startup;
        readonly Action<string> waitingNotify;
        WaitingList latestWaiting;
        string waitingSignature="";
        WaitingNoticeMode waitingMode;
        bool readingWaiting;
        // Order matches the login signals Program passes in: Claude, Codex, Grok.
        static readonly string[] LoginServices = {"Claude","Codex","Grok"};
        readonly RegisteredWaitHandle[] loginWaits = new RegisteredWaitHandle[3];
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
        internal TrayContext(bool show, EventWaitHandle signal, IUsageSource source, ThemeSettings themeSettings, ILoginLauncher launcher, EventWaitHandle[] loginSignals, Action<string> notification, SnapshotServer snapshotServer, EventWaitHandle refreshSignal, Func<CancellationToken,Task<ProviderStatus[]>> statusSource=null, IWaitingSource waitingSource=null, Action<string> waitingNotification=null, IStartupStore startupStore=null)
        {
            startup=startupStore??(source==null?RegistryStartupStore.Default():null);
            this.waitingSource=waitingSource??(source==null?new LocalWaitingSource(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)):null);
            statusPages=statusSource??(source==null?(Func<CancellationToken,Task<ProviderStatus[]>>)(token=>StatusPages.FetchAsync(http,token)):null);
            snapshots=snapshotServer;
            alerts=new UsageAlerts(source==null?UsageForecaster.Default():new UsageForecaster());
            loginLauncher=launcher;
            settings=themeSettings;themeMode=settings.Load();notificationMode=settings.LoadNotifications();waitingMode=settings.LoadWaiting();
            service = source ?? new UsageService(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), http);
            showEvent=signal;
            // Create the native handle before scheduling messages onto the UI thread.
            IntPtr handle=popup.Handle;
            tray=new NotifyIcon { Icon=Artwork.TrayIcon(),Text="AI Usage Manager",Visible=true,ContextMenuStrip=menu };
            notify=notification??(text=>tray.ShowBalloonTip(8000,"AI 使用量の警告",text,ToolTipIcon.Warning));
            tray.BalloonTipClicked+=(s,e)=>ShowPopup();
            waitingNotify=waitingNotification??(text=>tray.ShowBalloonTip(8000,"対応待ち",text,ToolTipIcon.Info));
            menu.Items.Add("使用量を表示",null,(s,e)=>ShowPopup());
            menu.Items.Add("更新",null,async (s,e)=>await RefreshUsage());
            foreach(string name in LoginServices) { string serviceName=name;loginMenu.DropDownItems.Add(name+" にログイン",null,async(s,e)=>await Login(serviceName)); }
            menu.Items.Add(loginMenu);
            AddThemeChoice("Windowsに合わせる",ThemeMode.System);
            AddThemeChoice("ライト",ThemeMode.Light);
            AddThemeChoice("ダーク",ThemeMode.Dark);
            menu.Items.Add(themeMenu);
            AddNotificationChoice("上限・急増・到達予測",NotificationMode.All);
            AddNotificationChoice("90%・95%・100%のみ",NotificationMode.Thresholds);
            AddNotificationChoice("通知しない（画面には表示）",NotificationMode.Off);
            menu.Items.Add(notificationMenu);
            AddWaitingChoice("3分後、その後10分ごと",WaitingNoticeMode.Standard);
            AddWaitingChoice("1分後、その後5分ごと",WaitingNoticeMode.Frequent);
            AddWaitingChoice("通知しない",WaitingNoticeMode.Off);
            menu.Items.Add(waitingMenu);
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
            if(startup!=null) {
                startupMenu.Checked=StartAtSignIn.IsOn(startup,Application.ExecutablePath);
                startupMenu.Click+=(s,e)=> {
                    if(!StartAtSignIn.Set(startup,Application.ExecutablePath,!startupMenu.Checked))tray.ShowBalloonTip(4000,"サインイン時の起動","設定を変更できませんでした。",ToolTipIcon.Warning);
                    startupMenu.Checked=StartAtSignIn.IsOn(startup,Application.ExecutablePath);
                };
                menu.Items.Add(startupMenu);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("終了",null,(s,e)=>ExitThread());
            ApplyTheme();
            menu.Opening+=(s,e)=> {
                ApplyTheme();
                // The entry can also be changed outside the app (for example by another copy of it).
                if(startup!=null)startupMenu.Checked=StartAtSignIn.IsOn(startup,Application.ExecutablePath);
            };
            SystemEvents.UserPreferenceChanged+=OnUserPreferenceChanged;
            tray.MouseClick+=(s,e)=> {
                if(e.Button!=MouseButtons.Left)return;
                if(popup.Visible)popup.Hide();
                else if((DateTimeOffset.UtcNow-popup.LastDeactivated).TotalMilliseconds>350)ShowPopup();
            };
            popup.RefreshRequested+=async(s,e)=>await RefreshUsage();
            popup.LoginRequested+=async name=>await Login(name);
            popup.WaitingOpenRequested+=app=>AppLinks.Open(app);
            if(loginSignals!=null)for(int i=0;i<Math.Min(loginWaits.Length,loginSignals.Length);i++) {
                string name=LoginServices[i];
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
            if(this.waitingSource!=null) {
                waitingTimer.Tick+=async(s,e)=>await ReadWaiting();waitingTimer.Start();
                popup.BeginInvoke(new Action(async()=>await ReadWaiting()));
            }
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
        void AddWaitingChoice(string label,WaitingNoticeMode mode)
        {
            var item=new ToolStripMenuItem(label) { Tag=mode };
            item.Click+=(s,e)=> {
                waitingMode=mode;ApplyTheme();
                if(!settings.SaveWaiting(mode))tray.ShowBalloonTip(4000,"通知の保存","設定を保存できませんでした。今回の起動中のみ適用します。",ToolTipIcon.Warning);
            };
            waitingMenu.DropDownItems.Add(item);
        }
        // Reads the waiting list off the UI thread, shows it, shares it when it changes, and announces long waits.
        async Task ReadWaiting()
        {
            if(readingWaiting||exiting||waitingSource==null)return;
            readingWaiting=true;
            try
            {
                var input=await Task.Run(()=>waitingSource.Read(DateTimeOffset.Now));
                if(exiting||input==null)return;
                var now=DateTimeOffset.Now;
                var list=waitingTracker.Observe(input.Claude,input.ClaudeReadable,input.Codex,input.CodexReadable,now);
                latestWaiting=list;
                string signature=string.Join("|",list.Items.Select(i=>i.Key+"/"+i.Reason+"/"+i.Since.ToUnixTimeSeconds()+"/"+i.Place).Concat(list.Unreadable.Select(u=>"!"+u.Service)));
                bool changed=signature!=waitingSignature;waitingSignature=signature;
                if(changed||popup.Visible)popup.SetWaiting(list,now);
                if(changed)PublishSnapshot();
                foreach(string text in waitingReminders.Due(list.Items,now,waitingMode))waitingNotify(text);
            }
            catch(Exception) { }
            finally { readingWaiting=false; }
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
            waitingMenu.DropDown.Renderer=renderer;waitingMenu.DropDown.BackColor=palette.Surface;waitingMenu.DropDown.ForeColor=palette.Ink;
            foreach(ToolStripMenuItem item in waitingMenu.DropDownItems)item.Checked=(WaitingNoticeMode)item.Tag==waitingMode;
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
                var statusTask=statusPages==null?null:statusPages(cancellation.Token);
                var result=await service.FetchAllAsync(cancellation.Token);
                ProviderStatus[] providers=null;
                if(statusTask!=null)try {providers=await statusTask;}catch(Exception){providers=null;}
                if(exiting)return;
                var mode=notificationMode;
                var notices=await Task.Run(()=> {
                    var observed=alerts.Observe(result,DateTimeOffset.Now,mode);
                    alerts.SaveHistory(DateTimeOffset.Now);return observed;
                });
                if(exiting)return;
                latest=result;
                if(statusPages!=null) {latestStatus=StatusSummary.Create(providers,resets.Observe(result,DateTimeOffset.Now),DateTimeOffset.Now);popup.SetStatus(latestStatus);}
                popup.SetUsage(result);
                tray.Text=UsageText.Summary(result);
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
        void PublishSnapshot() { if(snapshots!=null)snapshots.Publish(UsageSnapshot.Create(latest,DateTimeOffset.Now,refreshing,latestStatus,latestWaiting)); }
        protected override void ExitThreadCore()
        {
            if(exiting)return;exiting=true;
            SystemEvents.UserPreferenceChanged-=OnUserPreferenceChanged;
            refreshTimer.Stop();displayTimer.Stop();waitingTimer.Stop();cancellation.Cancel();showWait.Unregister(null);
            if(refreshWait!=null)refreshWait.Unregister(null);
            foreach(var wait in loginWaits)if(wait!=null)wait.Unregister(null);
            tray.Visible=false;var icon=tray.Icon;tray.Dispose();icon.Dispose();menu.Dispose();popup.Dispose();
            refreshTimer.Dispose();displayTimer.Dispose();waitingTimer.Dispose();http.Dispose();cancellation.Dispose();base.ExitThreadCore();
        }
    }
}
