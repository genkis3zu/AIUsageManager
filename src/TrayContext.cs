using System;
using System.Collections.Generic;
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
        readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = (int)UsageService.RefreshInterval.TotalMilliseconds };
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
        readonly Action<string> warnStartup;
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
        internal TrayContext(bool show, EventWaitHandle signal, IUsageSource source, ThemeSettings themeSettings, ILoginLauncher launcher, EventWaitHandle[] loginSignals, Action<string> notification, SnapshotServer snapshotServer, EventWaitHandle refreshSignal, Func<CancellationToken,Task<ProviderStatus[]>> statusSource=null, IWaitingSource waitingSource=null, Action<string> waitingNotification=null, IStartupStore startupStore=null, string nowDoingHotkey=null, Action<string> startupWarning=null, NowDoingSettings nowDoing=null)
        {
            // now_doing_ai_done=on: finished AI work goes to the 日中ログ of the diary in now_doing_note.
            this.nowDoing=nowDoing;
            if(nowDoing!=null&&nowDoing.AiDone)
            {
                // now_doing_ai_done_detail=on: each entry also says what was asked, what came back and the commits made.
                string profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if(nowDoing.AiDoneDetail)
                {
                    aiDoneDetail=new AiDoneDetailLog(nowDoing.AiDoneExclude,DateTimeOffset.Now,(item,after)=>AiTurnReader.Read(item,after,profile));
                    SystemEvents.SessionEnding+=OnSessionEnding;
                }
                else aiDone=new AiDoneLog(nowDoing.AiDoneExclude);
            }
            startup=StartAtSignIn.StoreFor(startupStore,source!=null);
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
            warnStartup=startupWarning??(text=>tray.ShowBalloonTip(8000,"サインイン時の起動",text,ToolTipIcon.Warning));
            menu.Items.Add("使用量を表示",null,(s,e)=>ShowPopup());
            menu.Items.Add("更新",null,async (s,e)=>await RefreshUsage(true));
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
                    string warning=StartAtSignIn.Change(startup,Application.ExecutablePath,!startupMenu.Checked);
                    if(warning!=null)warnStartup(warning);
                    startupMenu.Checked=StartAtSignIn.IsOn(startup,Application.ExecutablePath);
                };
                menu.Items.Add(startupMenu);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("終了",null,(s,e)=>ExitThread());
            ApplyTheme();
            // The shortcut key for the いまやってること window (now_doing_hotkey in settings.ini): a key registered with
            // Windows, or a double press of Ctrl or Shift found by a keyboard hook while the tray app runs.
            NowDoingHotkey hotkey=NowDoingHotkey.Parse(nowDoingHotkey);
            bool hotkeyReady=false;
            if(hotkey!=null&&hotkey.Double!=DoubleTapKey.None) {nowDoingTap=new DoubleTapHook(hotkey.Double,()=>OpenNowDoing(true));hotkeyReady=nowDoingTap.Installed;}
            else if(hotkey!=null) {nowDoingKey=new HotkeyWindow(hotkey,()=>OpenNowDoing(false));hotkeyReady=nowDoingKey.Registered;}
            string hotkeyProblem=NowDoingHotkey.Problem(nowDoingHotkey,hotkey,hotkeyReady);
            if(hotkeyProblem!=null)tray.ShowBalloonTip(8000,"いまやってること",hotkeyProblem,ToolTipIcon.Warning);
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
            popup.RefreshRequested+=async(s,e)=>await RefreshUsage(true);
            popup.LoginRequested+=async name=>await Login(name);
            popup.WaitingOpenRequested+=app=>AppLinks.Open(app);
            if(loginSignals!=null)for(int i=0;i<Math.Min(loginWaits.Length,loginSignals.Length);i++) {
                string name=LoginServices[i];
                loginWaits[i]=ThreadPool.RegisterWaitForSingleObject(loginSignals[i],(state,timedOut)=> {
                    try { if(!exiting)popup.BeginInvoke(new Action(async()=>await Login(name))); }catch(InvalidOperationException){}
                },null,Timeout.Infinite,false);
            }
            refreshTimer.Tick+=async(s,e)=>await RefreshUsage(false);
            displayTimer.Tick+=(s,e)=>popup.TickDisplay();displayTimer.Start();
            showWait=ThreadPool.RegisterWaitForSingleObject(showEvent,(state,timedOut)=> {
                try { if(!exiting)popup.BeginInvoke(new Action(ShowPopup)); }catch(InvalidOperationException){}
            },null,Timeout.Infinite,false);
            if(refreshSignal!=null)refreshWait=ThreadPool.RegisterWaitForSingleObject(refreshSignal,(state,timedOut)=> {
                try {if(!exiting)popup.BeginInvoke(new Action(async()=>await RefreshUsage(true)));}catch(InvalidOperationException){}
            },null,Timeout.Infinite,false);
            if(this.waitingSource!=null) {
                waitingTimer.Tick+=async(s,e)=>await ReadWaiting();waitingTimer.Start();
                popup.BeginInvoke(new Action(async()=>await ReadWaiting()));
            }
            popup.BeginInvoke(new Action(async()=> { if(show)ShowPopup();await RefreshUsage(false); }));
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
                if(aiDone!=null)aiDone.Observe(list.Items,now);
                if(aiDoneDetail!=null)aiDoneDetail.Observe(list.Items,now);
                if(aiDone!=null||aiDoneDetail!=null)await WriteAiDone(now);
            }
            catch(Exception) { }
            finally { readingWaiting=false; }
        }
        readonly NowDoingSettings nowDoing;
        readonly AiDoneLog aiDone;
        readonly AiDoneDetailLog aiDoneDetail;
        // An entry for the diary: the lines and the minute that decides where they go.
        sealed class DiaryEntry { public DateTime Time; public IList<string> Lines; }
        readonly Queue<DiaryEntry> doneQueue=new Queue<DiaryEntry>();
        DiaryEntry doneEntry;
        DateTimeOffset doneDue,doneNextTry;
        // The detailed entries being made off the UI thread, so that the app ending meanwhile still writes them.
        Task<List<DiaryEntry>> detailMaking;
        // Counts the writes at sign-out or exit. A WriteAiDone that a flush overtook stops after its await: the flush has
        // taken over what it was doing.
        int doneFlushes;
        // Writes the entry for finished AI work when it is due and the diary has been quiet for 10 seconds. A missing or
        // unwritable diary is not reported: the entry is dropped. The after script is never run for these entries.
        async Task WriteAiDone(DateTimeOffset now)
        {
            int flushes=doneFlushes;
            if(aiDone!=null&&doneEntry==null&&doneQueue.Count==0)
            {
                AiDoneEntry line=aiDone.Due(now);
                if(line!=null)doneQueue.Enqueue(new DiaryEntry{Time=line.Time,Lines=NowDoingNote.TwoLines(line.Time,line.Text,AiDoneLog.Mark)});
            }
            if(aiDoneDetail!=null)
            {
                // Reading the records and running git take time and are done off the UI thread.
                var detail=aiDoneDetail;
                detailMaking=Task.Run(()=>DetailEntries(detail.Due(now)));
                List<DiaryEntry> made=await detailMaking;
                if(exiting||flushes!=doneFlushes)return;
                detailMaking=null;
                foreach(var e in made)doneQueue.Enqueue(e);
            }
            if(doneEntry==null){if(doneQueue.Count==0)return;doneEntry=doneQueue.Dequeue();doneDue=now;doneNextTry=now;}
            if(now<doneNextTry)return;
            DiaryEntry entry=doneEntry;
            string path=string.IsNullOrEmpty(nowDoing.Note)?null:NowDoingNote.NotePath(nowDoing.Note,entry.Time);
            // The diary may be on a synced or network folder; it is looked at off this thread.
            DateTime? changed=path==null?null:await Task.Run(()=>DiaryChanged(path));
            if(exiting||flushes!=doneFlushes)return;
            if(changed==null){doneEntry=null;return;}
            AiDoneWait decision=AiDoneLog.Decide(changed.Value,now.UtcDateTime,doneDue.UtcDateTime);
            if(decision==AiDoneWait.Wait){doneNextTry=now.AddSeconds(10);return;}
            doneEntry=null;
            if(decision==AiDoneWait.GiveUp)return;
            string backup=AiDoneBackup();
            await Task.Run(()=>NowDoingNote.WriteEntry(nowDoing,entry.Time,entry.Lines,backup));
        }
        static string AiDoneBackup(){return NowDoingNote.BackupPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ai-done-last.md");}
        // The diary entries for pieces of work, with the commits made in the working folder meanwhile. Placed at the minute
        // the piece of work started.
        static List<DiaryEntry> DetailEntries(List<AiStretch> stretches)
        {
            var entries=new List<DiaryEntry>();
            foreach(var stretch in stretches)
            {
                List<string> commits=stretch.Turns.Count==0?new List<string>():GitCommits.Between(stretch.Cwd,stretch.Start,stretch.End);
                DateTime start=stretch.Start.LocalDateTime;
                entries.Add(new DiaryEntry{Time=new DateTime(start.Year,start.Month,start.Day,start.Hour,start.Minute,0),Lines=AiDoneFormat.Entry(stretch,commits.Take(5).ToList(),commits.Count>5)});
            }
            return entries;
        }
        // When the app ends or Windows signs out: the waiting work is written at once, without waiting for the diary to be
        // quiet, within 5 seconds in all. What it writes is taken out of the queue and the log, so after a cancelled sign-out
        // the exit writes only what came later.
        void FlushAiDone()
        {
            if(aiDoneDetail==null||nowDoing==null)return;
            doneFlushes++;
            var waiting=new List<DiaryEntry>();
            if(doneEntry!=null)waiting.Add(doneEntry);
            waiting.AddRange(doneQueue);
            doneEntry=null;doneQueue.Clear();
            var detail=aiDoneDetail;var making=detailMaking;detailMaking=null;var settings=nowDoing;string backup=AiDoneBackup();
            try
            {
                Task.Run(()=>
                {
                    if(making!=null){try{waiting.AddRange(making.Result);}catch(AggregateException){}}
                    waiting.AddRange(DetailEntries(detail.All()));
                    foreach(var e in waiting)NowDoingNote.WriteEntry(settings,e.Time,e.Lines,backup);
                }).Wait(5000);
            }
            catch(AggregateException){}
        }
        void OnSessionEnding(object sender,SessionEndingEventArgs e){FlushAiDone();}
        // When the diary was last written, or null when it is not there or cannot be looked at.
        static DateTime? DiaryChanged(string path)
        {
            try { return System.IO.File.Exists(path)?System.IO.File.GetLastWriteTimeUtc(path):(DateTime?)null; }
            catch(System.IO.IOException){return null;}
            catch(UnauthorizedAccessException){return null;}
            catch(System.Security.SecurityException){return null;}
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
                if(!exiting){await RefreshUsage(true);ShowPopup();}
            } finally {loggingIn=false;if(!exiting){loginMenu.Enabled=true;popup.SetLoginBusy(false);}}
        }
        // manual: asked for by the user (更新, F5, --refresh, after a login); the shared folder then reuses a result for 180 seconds instead of 270.
        async Task RefreshUsage(bool manual)
        {
            if(refreshing||exiting||clearingHistory)return;
            refreshing=true;refreshTimer.Stop();popup.SetRefresh(true,nextRefresh);menu.Items[1].Enabled=false;learningMenu.Enabled=false;
            PublishSnapshot();
            try
            {
                var statusTask=statusPages==null?null:statusPages(cancellation.Token);
                var result=await service.FetchAllAsync(cancellation.Token,manual);
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
                if(!exiting) {nextRefresh=DateTimeOffset.Now.Add(UsageService.RefreshInterval);popup.SetRefresh(false,nextRefresh);menu.Items[1].Enabled=true;learningMenu.Enabled=true;refreshTimer.Start();}
            }
        }
        void PublishSnapshot() { if(snapshots!=null)snapshots.Publish(UsageSnapshot.Create(latest,DateTimeOffset.Now,refreshing,latestStatus,latestWaiting)); }
        HotkeyWindow nowDoingKey;
        DoubleTapHook nowDoingTap;
        // Opens the いまやってること window as its own process, the same way the Stream Deck key does. Windows lets the
        // app that got the shortcut key bring a window to the front, and this passes that on to the new process. A double
        // press gives no such permission, so that window is told it may send one unused key to come to the front.
        void OpenNowDoing(bool doublePress)
        {
            if(exiting)return;
            try
            {
                using(var process=Process.Start(new ProcessStartInfo(Application.ExecutablePath,doublePress?NowDoingNote.TapArgument:NowDoingNote.Argument){UseShellExecute=false}))
                    if(process!=null)AllowSetForegroundWindow(process.Id);
            }
            catch(System.ComponentModel.Win32Exception) { tray.ShowBalloonTip(4000,"いまやってること","窓を開けませんでした。",ToolTipIcon.Warning); }
            catch(InvalidOperationException) { tray.ShowBalloonTip(4000,"いまやってること","窓を開けませんでした。",ToolTipIcon.Warning); }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
        protected override void ExitThreadCore()
        {
            if(exiting)return;exiting=true;
            if(aiDoneDetail!=null){SystemEvents.SessionEnding-=OnSessionEnding;FlushAiDone();}
            if(nowDoingKey!=null){nowDoingKey.Dispose();nowDoingKey=null;}
            if(nowDoingTap!=null)nowDoingTap.Dispose();
            SystemEvents.UserPreferenceChanged-=OnUserPreferenceChanged;
            refreshTimer.Stop();displayTimer.Stop();waitingTimer.Stop();cancellation.Cancel();showWait.Unregister(null);
            if(refreshWait!=null)refreshWait.Unregister(null);
            foreach(var wait in loginWaits)if(wait!=null)wait.Unregister(null);
            tray.Visible=false;var icon=tray.Icon;tray.Dispose();icon.Dispose();menu.Dispose();popup.Dispose();
            refreshTimer.Dispose();displayTimer.Dispose();waitingTimer.Dispose();http.Dispose();cancellation.Dispose();base.ExitThreadCore();
        }
    }
}
