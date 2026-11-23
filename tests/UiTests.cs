using System;
using System.Linq;
using System.Collections.Generic;
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
    sealed class FakeWaiting : IWaitingSource
    {
        public volatile string Id="s1";
        public DateTimeOffset Since=DateTimeOffset.Now.AddMinutes(-4);
        public WaitingSnapshotInput Read(DateTimeOffset now)
        {
            var session=new ClaudeSession{Pid=1,SessionId=Id,Status="waiting",WaitingFor="input needed",StatusUpdatedAt=Since,Name="Fix login",Entrypoint="claude-desktop"};
            return new WaitingSnapshotInput{Claude=new[]{session},ClaudeReadable=true,Codex=new CodexThread[0],CodexReadable=true};
        }
    }
    sealed class FakeSource : IUsageSource
    {
        public int Calls;
        TaskCompletionSource<ServiceUsage[]> pending;
        public Task<ServiceUsage[]> FetchAllAsync(CancellationToken cancellation)
        { Calls++;pending=new TaskCompletionSource<ServiceUsage[]>();return pending.Task; }
        public void Complete()
        { pending.SetResult(new[]{ServiceUsage.Empty("Claude",""),ServiceUsage.Empty("Codex",""),ServiceUsage.Empty("Grok","")}); }
        public void Complete(ServiceUsage[] values) { pending.SetResult(values); }
    }
    sealed class FakeStartup : IStartupStore
    {
        public string Value; public bool Fail;
        public string Read() { return Value; }
        public bool Write(string command) { if(Fail)return false;Value=command;return true; }
        public bool Delete() { if(Fail)return false;Value=null;return true; }
        public bool IsDisabled() { return false; }
        public bool ClearDisabled() { return !Fail; }
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
        Assert(settings.LoadNotifications()==NotificationMode.All,"Usage warnings default to all notification types");
        Assert(settings.SaveNotifications(NotificationMode.Off)&&settings.Load()==ThemeMode.System,"Muting notifications preserves theme");
        File.WriteAllText(path,"invalid setting");
        Assert(settings.Load()==ThemeMode.System,"Malformed preference follows Windows");
        foreach(var mode in new[]{ThemeMode.Light,ThemeMode.System,ThemeMode.Dark})
            Assert(settings.Save(mode)&&new ThemeSettings(path).Load()==mode,"Preference persists "+mode);
        Assert(settings.SaveNotifications(NotificationMode.Thresholds)&&settings.Save(ThemeMode.Dark)&&new ThemeSettings(path).LoadNotifications()==NotificationMode.Thresholds,"Theme changes retain persisted notification mode");
        settings.SaveNotifications(NotificationMode.All);
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
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    static void CheckHotkeyAndDrag()
    {
        var key=NowDoingHotkey.Parse("Ctrl+Alt+Shift+F24");int presses=0;
        using(var first=new HotkeyWindow(key,()=>presses++))
        {
            Assert(first.Registered,"The shortcut key is registered with Windows");
            using(var second=new HotkeyWindow(key,()=>{}))Assert(!second.Registered,"A shortcut key that is already taken is reported as not registered");
            SendMessage(first.Handle,0x0312,(IntPtr)1,IntPtr.Zero);
            Assert(presses==1,"Pressing the shortcut key opens the いまやってること window");
            SendMessage(first.Handle,0x0312,(IntPtr)2,IntPtr.Zero);
            Assert(presses==1,"Another shortcut key message does nothing");
        }
        using(var again=new HotkeyWindow(key,()=>{}))Assert(again.Registered,"Closing gives the shortcut key back to Windows");
        var hook=new DoubleTapHook(DoubleTapKey.RightShift,()=>{});
        Assert(hook.Installed,"The keyboard hook for a double press is installed");
        // Every key on the PC waits for the hook, so it must not wait for the tray's window thread (diary checks, menus).
        Assert(hook.HookThreadId!=0&&hook.HookThreadId!=GetCurrentThreadId(),"The keyboard hook runs on a thread of its own");
        hook.Dispose();
        Assert(!hook.Installed,"Closing removes the keyboard hook");
        using(var again=new DoubleTapHook(DoubleTapKey.RightShift,()=>{}))Assert(again.Installed,"The keyboard hook can be installed again");
        string hookFixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(hookFixture);
        using(var show=new EventWaitHandle(false,EventResetMode.AutoReset))
        {
            var tray=new TrayContext(false,show,new FakeSource(),new ThemeSettings(Path.Combine(hookFixture,"settings.ini")),new FakeLogin(),null,text=>{},null,null,nowDoingHotkey:"DoubleRightShift");
            var tap=Field<DoubleTapHook>(tray,"nowDoingTap");
            Assert(tap!=null&&tap.Installed&&Field<HotkeyWindow>(tray,"nowDoingKey")==null,"now_doing_hotkey=DoubleRightShift installs the keyboard hook instead of registering a shortcut key");
            tray.ExitThread();
            Assert(!tap.Installed,"The tray app removes the keyboard hook when it exits");
        }
        using(var show=new EventWaitHandle(false,EventResetMode.AutoReset))
        {
            var tray=new TrayContext(false,show,new FakeSource(),new ThemeSettings(Path.Combine(hookFixture,"settings.ini")),new FakeLogin(),null,text=>{},null,null,nowDoingHotkey:"Ctrl+Alt+Shift+F23");
            Assert(Field<DoubleTapHook>(tray,"nowDoingTap")==null&&Field<HotkeyWindow>(tray,"nowDoingKey").Registered,"A shortcut key setting installs no keyboard hook");
            tray.ExitThread();
        }
        using(var shown=new NowDoingForm(DateTime.Now,TodayView(),Actions(new List<string>(),null),ThemePalette.Light))
        {
            shown.Show();Application.DoEvents();
            string method=ForegroundWindow.Bring(shown.Handle,false);
            Assert(method=="SetForegroundWindow"||method=="AttachThreadInput"||method=="none","Bringing the window to the front without a key says how it went");
        }
        using(var popup=new PopupForm())
        {
            popup.SetWaiting(new WaitingList{Items=new[]{new WaitingItem{Key="Claude/q",Service="Claude",Place="P",App="claude",Reason=WaitReason.Question,Since=DateTimeOffset.Now}}},DateTimeOffset.Now);
            popup.OpenAt(new Point(400,700));popup.Refresh();Application.DoEvents();
            float s=Field<float>(popup,"scale");
            Func<float,float,int> hit=(x,y)=>{Point p=popup.PointToScreen(new Point((int)(x*s),(int)(y*s)));return SendMessage(popup.Handle,0x84,IntPtr.Zero,new IntPtr(((p.Y&0xffff)<<16)|(p.X&0xffff))).ToInt32();};
            Assert(hit(60,30)==2&&!popup.MaximizeBox,"The 使用量 window can be dragged by its heading, and a double click there does not maximize it");
            Assert(hit(200,100)==1,"A waiting item in the 使用量 window stays clickable");
            popup.Hide();
        }
    }
    static void SaveNowDoing(Form form,string name)
    {
        string output=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"screenshots");Directory.CreateDirectory(output);
        using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(output,name),ImageFormat.Png);}
    }
    static void CheckNowDoingWindow()
    {
        var written=new List<string>();var results=new Queue<NowDoingFailure>(new[]{NowDoingFailure.Busy,NowDoingFailure.None});
        var opened=new DateTime(2026,11,24,11,5,0);
        var view=new NowDoingView{Path=@"C:\Notes\2026年_日誌\2026-11-24 - 日誌.md",Heading="## 📝 日中ログ"};
        string[] samples={"メールの返信","見積の表を直している","設計メモを書いている","昼休み","買い物に出る","資料を読む","ストレッチ","会議の準備をしている。長い文は最後が省略されて1行に収まることを確かめるための文"};
        for(int i=0;i<8;i++)view.Records.Add(new NowDoingRecord{Time=(9+i/2).ToString("00")+":"+(i%2*30).ToString("00"),Text=samples[i]});
        using(var form=new NowDoingForm(opened,view,text=>{written.Add(text);return results.Dequeue();},f=>NowDoingNote.Message(f,null),ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            Assert(form.TopMost&&form.FormBorderStyle==FormBorderStyle.None&&form.Status=="Enter で日誌へ ／ Shift+Enter で改行 ／ Esc でやめる"&&!form.StatusIsFailure,"The いまやってること window is on top and says the keys");
            Assert(form.Page=="2026-11-24 - 日誌  ›  📝 日中ログ"&&form.Folder==@"C:\Notes\2026年_日誌","The いまやってること window shows the page, the section and the folder it writes to");
            Assert(form.ShownRecords.Count==NowDoingForm.MaxShown&&form.ShownRecords[0].Text==samples[7]&&form.ShownRecords[5].Text==samples[2],"The いまやってること window lists today's records, newest first, at most six");
            Assert(Field<TextBox>(form,"input").MaxLength==32767&&Field<TextBox>(form,"input").Multiline,"The いまやってること box takes several lines and long texts (the limits are checked on Enter and Ctrl+Enter)");
            SaveNowDoing(form,"now-doing-light.png");
            form.Input="  見積の表\tを直している ";form.Submit();
            Assert(form.Visible&&form.Input=="  見積の表\tを直している "&&form.Status=="日誌が使用中です。もう一度 Enter を押してください"&&form.StatusIsFailure,"A failed write keeps the window and the text, and shows the reason in red");
            form.Submit();
            Assert(!form.Visible&&written.Count==2&&written[0]=="見積の表 を直している"&&written[1]==written[0],"Enter again writes the cleaned text and closes the window");
        }
        var missing=new NowDoingView{Path=view.Path,Heading=view.Heading,Failure=NowDoingFailure.NoNote};
        using(var form=new NowDoingForm(opened,missing,text=>NowDoingFailure.None,f=>NowDoingNote.Message(f,null),ThemePalette.Dark))
        {
            form.Show();Application.DoEvents();
            Assert(form.Status=="今日の日誌がまだありません"&&form.StatusIsFailure&&form.Page=="2026-11-24 - 日誌  ›  📝 日中ログ","Without today's diary the window says so as soon as it opens, and still shows which page it means");
            SaveNowDoing(form,"now-doing-missing-dark.png");
        }
        var empty=new NowDoingView{Path=view.Path,Heading=view.Heading};
        using(var form=new NowDoingForm(opened,empty,text=>NowDoingFailure.None,f=>"",ThemePalette.Dark))
        {
            form.Show();Application.DoEvents();
            Assert(form.ShownRecords.Count==0&&!form.StatusIsFailure,"A diary without records opens without a failure");
            SaveNowDoing(form,"now-doing-empty-dark.png");
        }
        using(var form=new NowDoingForm(opened,new NowDoingView{Failure=NowDoingFailure.NoSetting},text=>NowDoingFailure.None,f=>NowDoingNote.Message(f,null),ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            Assert(form.Page=="書き込み先が決まっていません"&&form.Folder==""&&form.Status=="settings.ini に now_doing_note がありません","Without the setting the window says there is no page to write to");
        }
        using(var form=new NowDoingForm(opened,view,text=>{written.Add(text);return NowDoingFailure.None;},f=>"",ThemePalette.Light))
        {
            form.Show();Application.DoEvents();form.Input=" \t ";form.Submit();
            Assert(!form.Visible&&written.Count==2,"An empty line closes the window without writing");
        }
        using(var form=new NowDoingForm(opened,view,text=>{written.Add(text);return NowDoingFailure.None;},f=>"",ThemePalette.Light))
        {
            form.Show();Application.DoEvents();form.Input="x";
            var message=new Message();
            typeof(NowDoingForm).GetMethod("ProcessCmdKey",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{message,Keys.Escape});
            Assert(!form.Visible&&written.Count==2,"Esc closes the window without writing");
        }
        var key=typeof(NowDoingForm).GetMethod("ProcessCmdKey",BindingFlags.Instance|BindingFlags.NonPublic);
        var sent=new List<string>();
        using(var form=new NowDoingForm(opened,view,text=>{written.Add(text);return NowDoingFailure.None;},f=>"",ThemePalette.Light,text=>{sent.Add(text);return true;}))
        {
            form.Show();Application.DoEvents();
            Assert(form.Status=="Enter で日誌へ ／ Ctrl+Enter で Claudian へも ／ Shift+Enter で改行","With Claudian on, the window says Ctrl+Enter also sends to Claudian");
            SaveNowDoing(form,"now-doing-claudian-light.png");
            form.Input="見積";key.Invoke(form,new object[]{new Message(),Keys.Control|Keys.Enter});
            Assert(!form.Visible&&written.Count==3&&written[2]=="見積"&&sent.Count==1&&sent[0]=="見積","Ctrl+Enter writes the line to the diary, then hands it to Claudian and closes");
        }
        using(var form=new NowDoingForm(opened,view,text=>{written.Add(text);return NowDoingFailure.None;},f=>"",ThemePalette.Light,text=>{sent.Add(text);return true;}))
        {
            form.Show();Application.DoEvents();form.Input="日誌だけ";key.Invoke(form,new object[]{new Message(),Keys.Enter});
            Assert(!form.Visible&&written.Count==4&&sent.Count==1,"Enter alone does not send to Claudian");
        }
        using(var form=new NowDoingForm(opened,view,text=>{written.Add(text);return NowDoingFailure.None;},f=>"",ThemePalette.Light,text=>{sent.Add(text);return false;}))
        {
            form.Show();Application.DoEvents();form.Input="送れない";key.Invoke(form,new object[]{new Message(),Keys.Control|Keys.Enter});
            Assert(form.Visible&&form.Input==""&&form.StatusIsFailure&&form.Status==NowDoingForm.ClaudianFailed&&written.Count==5&&sent.Count==2,"When Claudian cannot be reached the line stays written once, the box is emptied and the window says so");
        }
        using(var form=new NowDoingForm(opened,view,text=>{written.Add(text);return NowDoingFailure.Busy;},f=>"busy",ThemePalette.Light,text=>{sent.Add(text);return true;}))
        {
            form.Show();Application.DoEvents();form.Input="書けない";key.Invoke(form,new object[]{new Message(),Keys.Control|Keys.Enter});
            Assert(form.Visible&&form.Input=="書けない"&&sent.Count==2,"Nothing goes to Claudian when the diary cannot be written");
        }
        using(var form=new NowDoingForm(opened,view,text=>{written.Add(text);return NowDoingFailure.None;},f=>"",ThemePalette.Light))
        {
            form.Show();Application.DoEvents();form.Input="y";key.Invoke(form,new object[]{new Message(),Keys.Control|Keys.Enter});
            Assert(!form.Visible&&written[written.Count-1]=="y"&&sent.Count==2,"Without the setting Ctrl+Enter only writes to the diary");
        }
    }
    // The window's actions for the tests: writing records the line, sending (when given) records the text.
    static NowDoingActions Actions(List<string> written,List<string> sent,Func<string,string,Task<NowDoingScriptResult>> after=null)
    {
        return new NowDoingActions{Write=t=>{written.Add(t);return NowDoingFailure.None;},Describe=f=>NowDoingNote.Message(f,null),
            Send=sent==null?null:(Func<string,bool>)(t=>{sent.Add(t);return true;}),After=after,ShowOutputMs=50};
    }
    static Task<NowDoingScriptResult> Result(bool ok,string output,string reason=null)
    {
        var done=new TaskCompletionSource<NowDoingScriptResult>();done.SetResult(new NowDoingScriptResult{Ok=ok,Output=output??"",Reason=reason});return done.Task;
    }
    static NowDoingView TodayView()
    {
        var view=new NowDoingView{Path=@"C:\Notes\2026年_日誌\2026-11-24 - 日誌.md",Heading="## 📝 日中ログ"};
        view.Records.Add(new NowDoingRecord{Time="09:00",Text="メールの返信"});view.Records.Add(new NowDoingRecord{Time="10:30",Text="見積の表を直している"});
        return view;
    }
    static void CheckNowDoingInput()
    {
        var opened=new DateTime(2026,11,24,11,5,0);var view=TodayView();
        var written=new List<string>();var sent=new List<string>();var calls=new List<string>();
        using(var form=new NowDoingForm(opened,view,Actions(written,null),ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            var box=Field<TextBox>(form,"input");int one=form.ClientSize.Height;
            form.Input="a";box.SelectionStart=1;form.Press(Keys.Shift|Keys.Enter);
            Assert(form.Input=="a\r\n"&&form.InputLines==2&&form.Visible&&written.Count==0,"Shift+Enter starts a new line in the いまやってること box");
            form.Input="一\r\n二\r\n三";Application.DoEvents();
            Assert(form.InputLines==3&&form.ClientSize.Height>one,"The いまやってること box grows with its lines");
            SaveNowDoing(form,"now-doing-multiline-light.png");
            form.Input=string.Join("\r\n",Enumerable.Range(1,10));Application.DoEvents();
            Assert(form.InputLines==6&&box.ScrollBars==ScrollBars.Vertical,"The box grows to six lines at most and scrolls after that");
            // The test's own text stands in for the clipboard, which other apps on this PC use too.
            var paste=(NowDoingTextBox)box;
            form.Input="";paste.PastedText=()=>"一\n二\r\n三";SendMessage(box.Handle,0x302,IntPtr.Zero,IntPtr.Zero);
            Assert(form.Input=="一\r\n二\r\n三","Pasted text keeps its line breaks, also when they are LF only");
            form.Input="x";paste.PastedText=()=>null;SendMessage(box.Handle,0x302,IntPtr.Zero,IntPtr.Zero);
            Assert(form.Input=="x","Pasting something that is not text changes nothing");
            form.Press(Keys.Control|Keys.A);
            Assert(box.SelectionLength==box.TextLength,"Ctrl+A selects the whole box");
            form.Input="a\r\nb";form.Press(Keys.Enter);
            Assert(!form.Visible&&written.Count==1&&written[0]=="a b","Enter writes the lines of the box as one line");
        }
        using(var form=new NowDoingForm(opened,view,Actions(written,sent),ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            form.Input=new string('あ',201);form.Press(Keys.Enter);
            Assert(form.Visible&&form.Status==NowDoingForm.TooLongToRecord&&form.StatusIsFailure&&form.Input.Length==201&&written.Count==1,"More than 200 characters is not written with Enter, and the text stays");
            form.Input=new string('a',4001);form.Press(Keys.Control|Keys.Enter);
            Assert(form.Visible&&form.Status==NowDoingForm.TooLongToSend&&form.StatusIsFailure&&written.Count==1&&sent.Count==0,"More than 4000 characters is neither written nor sent");
            form.Input="一行目\r\n二行目\r\n三行目";form.Press(Keys.Control|Keys.Enter);
            Assert(!form.Visible&&written[1]=="一行目（Claudian へ・3行）"&&sent.Count==1&&sent[0]=="一行目\n二行目\n三行目","Ctrl+Enter writes the first line and sends the whole text");
        }
        using(var form=new NowDoingForm(opened,view,Actions(written,null),ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            var box=(NowDoingTextBox)Field<TextBox>(form,"input");box.Focus();
            // After a failed after script the window empties the box itself; then a long text is pasted and typed after.
            form.Input="x";form.Input="";
            string twenty=string.Join("\n",Enumerable.Range(1,20));
            box.PastedText=()=>twenty;SendMessage(box.Handle,0x302,IntPtr.Zero,IntPtr.Zero);Application.DoEvents();
            SendMessage(box.Handle,0x102,(IntPtr)'Z',IntPtr.Zero);
            Assert(form.InputLines==6&&form.Input==twenty.Replace("\n","\r\n")+"Z","Text in the box stays when the box starts to scroll after it was emptied by the window");
        }
        Func<string,string,Task<NowDoingScriptResult>> shows=(mode,text)=>{calls.Add(mode+"|"+text);return Result(true,"🚶 散歩 ✅\n2行目");};
        using(var form=new NowDoingForm(opened,view,Actions(written,null,shows),ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            form.Input="🚶 散歩";form.Press(Keys.Enter);
            Assert(calls.Count==1&&calls[0]=="record|🚶 散歩"&&written[2]=="🚶 散歩","The after script gets record and the written text");
            Assert(form.Visible&&form.Status=="🚶 散歩 ✅"&&!form.StatusIsFailure,"The first line of the after script shows in the window");
            Assert(Pump(()=>!form.Visible),"The window closes after showing the after script's line");
        }
        var pending=new TaskCompletionSource<NowDoingScriptResult>();
        using(var form=new NowDoingForm(opened,view,Actions(written,sent,(mode,text)=>{calls.Add(mode+"|"+text);return pending.Task;}),ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            form.Input="質問です\r\n続き";form.Press(Keys.Control|Keys.Enter);
            Assert(form.Busy&&form.Status==NowDoingForm.Working&&sent.Count==1&&calls[1]=="send|質問です\n続き"&&written[3]=="質問です（Claudian へ・2行）","Ctrl+Enter runs the after script with send and the whole text, and waits for it");
            form.Press(Keys.Escape);form.Press(Keys.Enter);Application.DoEvents();
            Assert(form.Visible&&written.Count==4,"While the after script runs the window takes no keys");
            pending.SetResult(new NowDoingScriptResult{Ok=true,Output=""});
            Assert(Pump(()=>!form.Visible)&&sent.Count==2&&sent[1]=="質問です\n続き","Ctrl+Enter sends to Claudian after the after script ends, then closes");
        }
        Func<string,string,Task<NowDoingScriptResult>> fails=(mode,text)=>{calls.Add(mode+"|"+text);return Result(false,"","見出しが見つかりません");};
        using(var form=new NowDoingForm(opened,view,Actions(written,sent,fails),ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            form.Input="🚶 散歩";form.Press(Keys.Enter);
            Assert(form.Visible&&form.Status=="日誌には書きました。そのあとの処理に失敗しました: 見出しが見つかりません"&&form.StatusIsFailure&&form.Input==""&&written.Count==5&&!form.Busy,"A failed after script keeps the window, empties the box and says the diary was written");
            SaveNowDoing(form,"now-doing-after-failed-light.png");
            form.Input="送る";form.Press(Keys.Control|Keys.Enter);
            Assert(form.Visible&&sent.Count==3&&sent[2]=="送る"&&form.Status==NowDoingForm.AfterFailed("見出しが見つかりません"),"Ctrl+Enter still sends when the after script fails");
        }
    }
    static NowDoingButton[] Buttons(params string[] values)
    {
        var list=new NowDoingButton[NowDoingButton.Count];
        for(int i=0;i<values.Length;i++)if(values[i]!=null)list[i]=NowDoingButton.Parse(i+1,values[i]);
        return list;
    }
    static void CheckNowDoingButtons()
    {
        var opened=new DateTime(2026,11,24,11,5,0);var view=TodayView();
        var written=new List<string>();var calls=new List<string>();var clips=new List<string>();var apps=new List<string>();
        Func<string,string,Task<NowDoingScriptResult>> after=(mode,text)=>{calls.Add(mode+"|"+text);return Result(true,"");};
        Func<Task<NowDoingScriptResult>,NowDoingButton[],NowDoingActions> make=(copied,buttons)=>{
            var a=Actions(written,null,after);a.Buttons=buttons;a.Copy=command=>{calls.Add("copy|"+command);return copied;};
            a.SetClipboard=text=>{clips.Add(text);return true;};a.OpenApp=app=>{apps.Add(app);return true;};return a;
        };
        var nine=Buttons("🚶 散歩|record:🚶 散歩 ✅","☕ 休憩|record:☕ 休憩 ✅","📚 読書|record:📚 読書 ✅","🧹 片付け|record:🧹 片付け ✅",
            "⏱️ 作業|text:⏱️ ","🗒️ 議事|text:🗒️ ","📈 依頼文|copy:python r.py|open:claude://","🧘 ストレッチ|record:🧘 ストレッチ","📞 電話の記録をする|text:📞 ");
        using(var form=new NowDoingForm(opened,view,make(Result(true,"x"),nine),ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            float s=Field<float>(form,"scale");var controls=form.ButtonControls;
            var reading=controls.OrderBy(b=>b.Top).ThenBy(b=>b.Left).ToList();
            Assert(controls.Count==9&&controls.Select(b=>b.Text).SequenceEqual(nine.Select(b=>b.ShortLabel))&&reading.SequenceEqual(controls)&&controls[8].Text=="📞 電話の記録を","Buttons sit under the box in their order, with names cut at 8 characters");
            Assert(controls.Select(b=>b.Top).Distinct().Count()>=2&&controls.All(b=>b.Left>=(int)(20*s)-1&&b.Right<=form.ClientSize.Width-(int)(20*s)+1),"Buttons that do not fit wrap to the next row inside the window");
            Assert(controls.Min(b=>b.Top)>Field<TextBox>(form,"input").Bottom&&controls.Max(b=>b.Bottom)<=(int)(Field<float>(form,"statusTop")*s)+1,"Buttons sit between the box and the status line");
            Assert(Field<ToolTip>(form,"tips").GetToolTip(controls[4])=="⏱️ 作業（Alt+5）"&&controls[4].AccessibleName=="⏱️ 作業（Alt+5）","A button says its name and its Alt key");
            SaveNowDoing(form,"now-doing-buttons-light.png");
            controls[5].PerformClick();
            Assert(form.Input=="🗒️ "&&Field<TextBox>(form,"input").SelectionStart==form.Input.Length&&form.Visible,"Clicking a text button puts its text in the box");
        }
        var three=Buttons("🚶 散歩|record:🚶 散歩 ✅",null,null,null,"⏱️ 作業|text:⏱️ ",null,"📈 依頼文|copy:python r.py|open:claude://");
        using(var form=new NowDoingForm(opened,view,make(Result(true,"x"),three),ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            form.Press(Keys.Alt|Keys.D5);
            Assert(form.Input=="⏱️ "&&Field<TextBox>(form,"input").SelectionStart==form.Input.Length,"Alt+number presses that button and leaves the caret at the end");
            form.Input="";form.Press(Keys.Alt|Keys.D8);form.Press(Keys.Alt|Keys.D2);
            Assert(form.Input==""&&form.Visible&&written.Count==0,"Alt with a number that has no button does nothing");
            form.Press(Keys.Alt|Keys.D1);
            Assert(written.Count==1&&written[0]=="🚶 散歩 ✅"&&calls.Last()=="record|🚶 散歩 ✅"&&!form.Visible,"A record button writes its text and runs the after script");
        }
        using(var form=new NowDoingForm(opened,view,make(Result(true,"依頼文\n2行目"),three),ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            form.PressButton(7);
            Assert(calls.Last()=="copy|python r.py"&&clips.Count==1&&clips[0]=="依頼文\r\n2行目"&&apps.Count==1&&apps[0]=="claude"&&form.Status==NowDoingForm.Copied,"A copy button puts the output in the clipboard and opens the app");
            Assert(Pump(()=>!form.Visible),"The window closes after a copy");
        }
        using(var form=new NowDoingForm(opened,view,make(Result(false,"","今日の依頼文がありません"),three),ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            form.Press(Keys.Alt|Keys.D7);
            Assert(clips.Count==1&&apps.Count==1&&form.Visible&&!form.Busy&&form.StatusIsFailure&&form.Status=="コピーできませんでした: 今日の依頼文がありません","A failed copy changes nothing and says why");
        }
        using(var form=new NowDoingForm(opened,view,make(Result(true,""),three),ThemePalette.Light))
        {
            form.Show();Application.DoEvents();form.PressButton(7);
            Assert(clips.Count==1&&apps.Count==1&&form.Status=="コピーできませんでした: 出力が空です","A copy with no output changes nothing");
        }
        var busyClipboard=make(Result(true,"x"),three);busyClipboard.SetClipboard=text=>false;
        using(var form=new NowDoingForm(opened,view,busyClipboard,ThemePalette.Light))
        {
            form.Show();Application.DoEvents();form.PressButton(7);
            Assert(apps.Count==1&&form.Visible&&form.StatusIsFailure&&form.Status==NowDoingForm.ClipboardBusy,"When the clipboard cannot be set the app is not opened");
        }
        var wrong=make(Result(true,"x"),three);wrong.Notice=NowDoingButton.Problem(new[]{2,5});
        using(var form=new NowDoingForm(opened,view,wrong,ThemePalette.Dark))
        {
            form.Show();Application.DoEvents();
            Assert(form.Footer.Count==1&&form.Footer[0]=="settings.ini の now_doing_button2・now_doing_button5 の書き方が違います","Wrong buttons are named at the bottom of the window");
            SaveNowDoing(form,"now-doing-button-problem-dark.png");
        }
    }
    static void CheckNowDoingTodo()
    {
        var opened=new DateTime(2026,11,24,11,5,0);var view=TodayView();var written=new List<string>();
        int plainTop;
        using(var plain=new NowDoingForm(opened,view,Actions(written,null),ThemePalette.Light)){plain.Show();Application.DoEvents();plainTop=Field<TextBox>(plain,"input").Top;}
        var listed=Actions(written,null);listed.List=token=>Result(true,"🚶 散歩がまだ\nDW#1 見積\n\n  ⚠️ 注意  \n📈 今日の1%\n六行目\n七行目");
        using(var form=new NowDoingForm(opened,view,listed,ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            Assert(form.Todo.SequenceEqual(new[]{"🚶 散歩がまだ","DW#1 見積","⚠️ 注意","📈 今日の1%","六行目"}),"The list script's lines show as まだのこと, at most five, without blank lines");
            Assert(Field<TextBox>(form,"input").Top>plainTop&&form.Footer.Count==0,"まだのこと sits above the box");
            SaveNowDoing(form,"now-doing-todo-light.png");
        }
        var empty=Actions(written,null);empty.List=token=>Result(true,"");
        using(var form=new NowDoingForm(opened,view,empty,ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            Assert(form.Todo.Count==0&&form.Footer.Count==0&&Field<TextBox>(form,"input").Top==plainTop,"A list script that prints nothing shows no まだのこと");
        }
        var failing=Actions(written,null);failing.List=token=>Result(false,"","時間切れ（3秒）");
        using(var form=new NowDoingForm(opened,view,failing,ThemePalette.Dark))
        {
            form.Show();Application.DoEvents();
            Assert(form.Todo.Count==0&&form.Footer.Count==1&&form.Footer[0]==NowDoingForm.TodoFailed,"A failed list script shows no list and a small note at the bottom");
            SaveNowDoing(form,"now-doing-todo-failed-dark.png");
        }
        CancellationToken seen=CancellationToken.None;
        var hanging=Actions(written,null);hanging.List=token=>{seen=token;return new TaskCompletionSource<NowDoingScriptResult>().Task;};
        using(var form=new NowDoingForm(opened,view,hanging,ThemePalette.Light))
        {
            form.Show();Application.DoEvents();
            Assert(seen.CanBeCanceled&&!seen.IsCancellationRequested,"The list script runs while the window is open");
            form.Close();Application.DoEvents();
            Assert(seen.IsCancellationRequested,"Closing the window stops a list script that is still running");
        }
        Assert(NowDoingForm.RecordRows(8,600,400,26)==6&&NowDoingForm.RecordRows(8,500,400,26)==3&&NowDoingForm.RecordRows(8,420,400,26)==3
            &&NowDoingForm.RecordRows(8,530,400,26)==5&&NowDoingForm.RecordRows(2,100,400,26)==2&&NowDoingForm.RecordRows(0,600,400,26)==1,"Fewer of today's records are shown on a short screen, never fewer than three");
    }
    [STAThread]
    static int Main()
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        // DoEvents is not a persistent Application.Run loop. Keep a UI context installed
        // so worker continuations return to this test's UI thread, including while hidden.
        WindowsFormsSynchronizationContext.AutoInstall=false;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        TrayContext context=null;
        try
        {
            string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
            string path=Path.Combine(fixture,"settings.ini");var settings=new ThemeSettings(path);CheckSettings(settings,path);
            CheckNowDoingWindow();
            CheckNowDoingInput();
            CheckNowDoingButtons();
            CheckNowDoingTodo();
            CheckHotkeyAndDrag();
            using(var signal=new EventWaitHandle(false,EventResetMode.AutoReset))
            using(var refreshSignal=new EventWaitHandle(false,EventResetMode.AutoReset))
            using(var snapshots=new SnapshotServer("AIUsageManager.UiTests."+Guid.NewGuid().ToString("N")))
            {
                var source=new FakeSource();var login=new FakeLogin();var notices=new List<string>();context=new TrayContext(false,signal,source,settings,login,null,text=>notices.Add(text),snapshots,refreshSignal);
                var popup=Field<PopupForm>(context,"popup");var refresh=Field<Button>(popup,"refresh");
                var row=Field<UsageRow[]>(popup,"rows").Last();
                var authMissing=ServiceUsage.Empty("Grok","ログインが必要です");authMissing.NeedsLogin=true;
                row.Usage=authMissing;
                Assert(row.Controls.OfType<Button>().Any(b=>b.Text.Contains("ログイン")),"Authentication failure exposes a login button");
                var claudeNeeds=ServiceUsage.Empty("Claude","Claude Code で再ログインが必要です");claudeNeeds.NeedsLogin=true;
                Assert(UsageRow.ShowsLogin(claudeNeeds)&&UsageRow.ShowsLogin(authMissing)&&!UsageRow.ShowsLogin(ServiceUsage.Empty("Claude","")),"Login button appears for Claude, Codex and Grok when login is needed");
                Assert(Program.LoginService(new[]{"--login-claude"})=="Claude"&&Program.LoginService(new[]{"--login-codex"})=="Codex"&&Program.LoginService(new[]{"--login-grok"})=="Grok"&&Program.LoginService(new[]{"--login-other"})==null&&Program.LoginService(new[]{"--login-claude","x"})==null,"Start arguments request login only for the three known services");
                Assert(Program.ShowsRunningApp(new string[0])&&Program.ShowsRunningApp(new[]{"--show"})&&!Program.ShowsRunningApp(new[]{"--background"})&&!Program.ShowsRunningApp(new[]{"--refresh"})&&!Program.ShowsRunningApp(new[]{"--login-codex"}),"A second start opens the running app's window, but not a start in the background");
                var three=new ServiceUsage{Name="Claude",Plan="Max",Source="オンライン",RecordedAt=DateTimeOffset.Now};
                three.Windows.Add(UsageWindow.FiveHour(10,DateTimeOffset.Now.AddHours(3)));three.Windows.Add(UsageWindow.Week(20,DateTimeOffset.Now.AddDays(3)));three.Windows.Add(UsageWindow.Scoped("Fable",96,DateTimeOffset.Now.AddDays(3)));
                var paintRow=new UsageRow{Width=388,Height=110};paintRow.Usage=three;
                using(var image=new Bitmap(388,110))paintRow.DrawToBitmap(image,new Rectangle(0,0,388,110));
                Assert(paintRow.AccessibleName.Contains("5時間 10%")&&paintRow.AccessibleName.Contains("週 20%")&&paintRow.AccessibleName.Contains("週（Fable） 96%"),"Popup row with three Claude windows paints and names all three");
                Assert(UsageRow.ResetText(three.Windows[1],3,DateTimeOffset.Now).StartsWith("リセットまで")&&!UsageRow.ResetText(three.Windows[1],4,DateTimeOffset.Now).Contains("リセットまで"),"Popup uses short reset text when four windows share a row");
                Assert(UsageRow.Caption(three.Windows[2],3)=="Fable"&&UsageRow.Caption(three.Windows[0],3)=="5h"&&UsageRow.Caption(three.Windows[1],2)=="週"&&UsageRow.Caption(UsageWindow.Scoped("Fable",1,null),2)=="週（Fable）","Popup uses short captions only when three or more windows share a row");
                var single=new ServiceUsage{Name="Claude",Plan="Ultra",Source="オンライン",RecordedAt=DateTimeOffset.Now};single.Windows.Add(UsageWindow.Week(40,DateTimeOffset.Now.AddDays(3)));
                paintRow.Usage=single;using(var image=new Bitmap(388,110))paintRow.DrawToBitmap(image,new Rectangle(0,0,388,110));
                Assert(paintRow.AccessibleName.Contains("週 40%")&&!paintRow.AccessibleName.Contains("5時間"),"Popup row with one window paints without a 5-hour column");
                paintRow.Dispose();
                var timer=Field<System.Windows.Forms.Timer>(context,"refreshTimer");
                var themes=Field<ToolStripMenuItem>(context,"themeMenu");var menu=Field<ContextMenuStrip>(context,"menu");
                var widgets=Field<ToolStripMenuItem>(context,"widgetMenu");
                var dark=(ToolStripMenuItem)themes.DropDownItems[2];var light=(ToolStripMenuItem)themes.DropDownItems[1];var automatic=(ToolStripMenuItem)themes.DropDownItems[0];
                var darkPalette=ThemePalette.Resolve(ThemeMode.Dark);var lightPalette=ThemePalette.Resolve(ThemeMode.Light);
                Assert(dark.Checked&&popup.BackColor==darkPalette.Surface,"Startup restores selected dark appearance");
                Assert(timer.Interval==300000,"Default refresh is five minutes");
                Assert(Pump(()=>source.Calls==1),"Startup refresh runs on the message loop");
                var learning=Field<ToolStripMenuItem>(context,"learningMenu");
                Assert(!learning.Enabled&&!context.ClearLearningHistory(()=>true),"History deletion cannot overlap an active fetch");
                refresh.PerformClick();Application.DoEvents();Assert(source.Calls==1&&!refresh.Enabled,"Refresh overlap blocked while loading");
                source.Complete();Assert(Pump(()=>refresh.Enabled),"Background completion enables refresh");
                Assert(learning.Enabled&&learning.DropDownItems.Count==2,"Learning status and deletion are available after refresh");
                Assert(context.ClearLearningHistory(()=> {
                    var task=(Task)typeof(TrayContext).GetMethod("RefreshUsage",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(context,null);
                    Application.DoEvents();
                    return task.IsCompleted&&source.Calls==1;
                }),"Refresh during modal deletion confirmation is blocked and deletion succeeds");
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
                bool timerFired=Pump(()=>source.Calls==3);
                Assert(timerFired,"Real timer tick triggers background refresh (calls="+source.Calls+", timer="+timer.Enabled+", refreshing="+Field<bool>(context,"refreshing")+", clearing="+Field<bool>(context,"clearingHistory")+")");
                Application.DoEvents();Assert(source.Calls==3&&!timer.Enabled,"Timer pauses while request pending");
                // Only this check uses the accelerated timer. A 35 ms restart during later
                // asynchronous completions would request unrelated fake responses on slower CI.
                timer.Interval=300000;
                source.Complete();Assert(Pump(()=>refresh.Enabled),"Timer refresh completes");
                timer.Stop();popup.OpenAt(new Point(300,500));
                row.Usage=authMissing;
                row.Controls.OfType<Button>().Single().PerformClick();
                Assert(Pump(()=>login.Calls==1)&&login.Service=="Grok"&&!popup.Visible,"Login button starts official authentication for its service");
                var loginMenu=Field<ToolStripMenuItem>(context,"loginMenu");
                Assert(string.Join("|",loginMenu.DropDownItems.Cast<ToolStripItem>().Select(i=>i.Text))=="Claude にログイン|Codex にログイン|Grok にログイン","Login menu lists Claude, Codex and Grok");
                loginMenu.DropDownItems[0].PerformClick();Application.DoEvents();Assert(login.Calls==1,"Login launches cannot overlap");
                login.Pending.SetResult(null);Assert(Pump(()=>source.Calls==4),"OAuth completion automatically refreshes usage");
                source.Complete();Assert(Pump(()=>popup.Visible&&loginMenu.Enabled),"Completed login restores popup and controls");
                timer.Stop();refresh.PerformClick();Assert(Pump(()=>source.Calls==5),"Warning update is requested");
                var high=ServiceUsage.Empty("Codex","");high.Source="オンライン";high.RecordedAt=DateTimeOffset.Now;high.Windows[0].Percent=95;high.Windows[0].ResetsAt=DateTimeOffset.Now.AddDays(1);
                var warningValues=new[]{ServiceUsage.Empty("Claude",""),high,ServiceUsage.Empty("Grok","")};
                source.Complete(warningValues);Assert(Pump(()=>refresh.Enabled)&&notices.Count==1&&notices[0].Contains("Codex"),"Fresh high usage delivers a Windows notification through the app boundary");
                var warningRow=Field<UsageRow[]>(popup,"rows")[1];
                Assert(warningRow.AccessibleDescription.Contains("残り5%")&&warningRow.Height>=180,"Warning text is accessible and has dedicated visible space");
                string snapshot=Field<string>(snapshots,"latest");
                Assert(snapshot.Contains("\"percent\":95")&&snapshot.Contains("残り5%")&&source.Calls==5,"Tray publishes the same usage and alert without a second provider fetch");
                timer.Stop();refresh.PerformClick();Assert(Pump(()=>source.Calls==6),"Repeated warning refresh is requested");
                high.RecordedAt=DateTimeOffset.Now;source.Complete(warningValues);Assert(Pump(()=>refresh.Enabled)&&notices.Count==1,"Manual refresh does not repeat the same warning notification");
                var notifications=Field<ToolStripMenuItem>(context,"notificationMenu");notifications.DropDownItems[2].PerformClick();
                Assert(settings.LoadNotifications()==NotificationMode.Off,"Notification menu persists muted mode");
                timer.Stop();refresh.PerformClick();Assert(Pump(()=>source.Calls==7),"Muted update is requested");
                high.RecordedAt=DateTimeOffset.Now;high.Windows[0].Percent=100;source.Complete(warningValues);
                Assert(Pump(()=>refresh.Enabled)&&notices.Count==1&&warningRow.AccessibleDescription.Contains("上限"),"Mute suppresses delivery while preserving visible warnings");
                timer.Stop();
                var crowded=WidgetCards.Sample(DateTimeOffset.Now);foreach(var value in crowded){value.Source="オンライン";foreach(var window in value.Windows)window.Percent=100;}
                // Anchor to one monitor before resizing; cursor placement near another screen changes DPI.
                var crowdedArea=Screen.PrimaryScreen.WorkingArea;popup.Location=crowdedArea.Location;
                float crowdedScale=Math.Max(2f,crowdedArea.Height/400f);
                typeof(PopupForm).GetField("scale",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(popup,crowdedScale);popup.SetUsage(crowded);
                var content=Field<Panel>(popup,"content");
                Assert(popup.Height<=crowdedArea.Height&&content.AutoScrollMinSize.Height>content.ClientSize.Height,"Many warnings at high DPI stay within the monitor with scrolling (popup="+popup.Height+", content="+content.ClientSize.Height+", total="+content.AutoScrollMinSize.Height+", scale="+crowdedScale+")");
                var keyHandler=typeof(PopupForm).GetMethod("ProcessCmdKey",BindingFlags.Instance|BindingFlags.NonPublic);
                Func<Keys,bool> key=keys=>(bool)keyHandler.Invoke(popup,new object[]{new Message(),keys});
                Assert(key(Keys.End)&&Field<UsageRow[]>(popup,"rows").Last().Bottom<=content.ClientSize.Height,"End key makes the last warning reachable without a mouse");
                int scrolled=content.AutoScrollPosition.Y;popup.TickDisplay();
                Assert(content.AutoScrollPosition.Y==scrolled,"Display refresh preserves the warning scroll position");
                Assert(key(Keys.Home)&&content.AutoScrollPosition.Y==0,"Home key returns to the first warning");
                Assert(key(Keys.PageDown)&&content.AutoScrollPosition.Y<0&&key(Keys.PageUp)&&content.AutoScrollPosition.Y==0,"Page keys scroll the warning list from the focused header");
                refreshSignal.Set();Assert(Pump(()=>source.Calls==8),"Obsidian refresh signal requests a tray refresh");
                source.Complete();Assert(Pump(()=>refresh.Enabled),"Signaled refresh completes normally");
                context.ExitThread();Assert(popup.IsDisposed,"Exit releases popup and tray resources");context=null;
            }
            // Login requests from --login-<service> (Stream Deck, widget): before the app starts and while it runs.
            using(var showEvent=new EventWaitHandle(false,EventResetMode.AutoReset))
            using(var claudeEvent=new EventWaitHandle(false,EventResetMode.AutoReset))
            using(var codexEvent=new EventWaitHandle(false,EventResetMode.AutoReset))
            using(var grokEvent=new EventWaitHandle(false,EventResetMode.AutoReset))
            {
                string loginFixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(loginFixture);
                var loginSource=new FakeSource();var requested=new FakeLogin();
                claudeEvent.Set();
                var statusSnapshots=new SnapshotServer("AIUsageManager.UiTests.Status."+Guid.NewGuid().ToString("N"));
                Func<CancellationToken,Task<ProviderStatus[]>> statusPages=token=>Task.FromResult(new[]{new ProviderStatus{Company="Claude",Level=StatusLevel.Major,Detail="Claude Code"},new ProviderStatus{Company="OpenAI",Level=StatusLevel.Operational}});
                context=new TrayContext(false,showEvent,loginSource,new ThemeSettings(Path.Combine(loginFixture,"settings.ini")),requested,new[]{claudeEvent,codexEvent,grokEvent},text=>{},statusSnapshots,null,statusPages);
                Assert(Pump(()=>loginSource.Calls==1),"Tray started for a login still runs its first refresh");
                loginSource.Complete();
                Assert(Pump(()=>requested.Calls==1)&&requested.Service=="Claude","A login requested before the tray app starts runs for Claude once it is ready");
                var statusPopup=Field<PopupForm>(context,"popup");
                Assert(Pump(()=>statusPopup.AccessibleDescription=="Claude 障害（Claude Code）")&&Field<string>(statusSnapshots,"latest").Contains("\"title\":\"Claude 障害\""),"Refresh shows the outage line in the tray and shares it with Stream Deck");
                requested.Pending.SetResult(null);Assert(Pump(()=>loginSource.Calls==2),"Finished login refreshes usage");
                loginSource.Complete();Assert(Pump(()=>!Field<bool>(context,"loggingIn")),"Login state is released after the refresh");
                codexEvent.Set();
                Assert(Pump(()=>requested.Calls==2)&&requested.Service=="Codex","A login requested while the tray app runs goes to the requested service");
                context.ExitThread();context=null;statusSnapshots.Dispose();
            }
            // Waiting list: a session waiting for an answer shows in the tray, goes to Stream Deck and is announced.
            using(var waitingShow=new EventWaitHandle(false,EventResetMode.AutoReset))
            {
                string waitingFixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(waitingFixture);
                var waitingSnapshots=new SnapshotServer("AIUsageManager.UiTests.Waiting."+Guid.NewGuid().ToString("N"));
                var waitingSource=new FakeWaiting();var waitingNotices=new List<string>();
                context=new TrayContext(false,waitingShow,new FakeSource(),new ThemeSettings(Path.Combine(waitingFixture,"settings.ini")),new FakeLogin(),null,text=>{},waitingSnapshots,null,null,waitingSource,text=>waitingNotices.Add(text));
                Field<System.Windows.Forms.Timer>(context,"waitingTimer").Interval=50;
                var waitingPopup=Field<PopupForm>(context,"popup");
                Assert(!Field<ContextMenuStrip>(context,"menu").Items.Contains(Field<ToolStripMenuItem>(context,"startupMenu")),"A test run without its own sign-in store never reads or changes the real entry");
                var unreadable=new WaitingList{Unreadable=new[]{new WaitingUnreadable{Service="Codex",Reason="記録を読めません"}}};
                var finishedOnly=new WaitingList{Items=new[]{new WaitingItem{Key="Claude/x",Service="Claude",Place="P",App="claude",Reason=WaitReason.Finished,Since=DateTimeOffset.Now}}};
                Assert(PopupForm.WaitingHeading(unreadable)=="対応待ちを確認できません"&&PopupForm.WaitingHeading(finishedOnly)=="対応待ちなし","The tray never says 対応待ちなし when a record could not be read");
                Assert(Pump(()=>waitingPopup.WaitingText.Contains("Claude")&&waitingPopup.WaitingText.Contains("質問")&&waitingPopup.WaitingText.Contains("4分")),"Waiting sessions are listed in the tray with their reason and time");
                Assert(Pump(()=>{string latest=Field<string>(waitingSnapshots,"latest");return latest!=null&&latest.Contains("\"waiting\":{")&&latest.Contains("\"reason\":\"question\"");}),"The waiting list is shared with Stream Deck");
                Assert(Pump(()=>waitingNotices.Count==1)&&waitingNotices[0].Contains("が質問の答えを4分待っています"),"A wait longer than 3 minutes is announced once");
                Field<ToolStripMenuItem>(context,"waitingMenu").DropDownItems.Cast<ToolStripMenuItem>().First(i=>i.Text=="通知しない").PerformClick();
                waitingSource.Since=DateTimeOffset.Now.AddMinutes(-20);waitingSource.Id="s2";
                Assert(Pump(()=>waitingPopup.WaitingText.Contains("20分"))&&waitingNotices.Count==1,"Choosing 通知しない stops waiting notifications");
                Assert(new ThemeSettings(Path.Combine(waitingFixture,"settings.ini")).LoadWaiting()==WaitingNoticeMode.Off,"The waiting notice choice is saved");
                context.ExitThread();context=null;waitingSnapshots.Dispose();
            }
            // Starting at sign-in: the tray menu shows and changes the entry, which starts this app in the background.
            using(var startupShow=new EventWaitHandle(false,EventResetMode.AutoReset))
            {
                string startupFixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(startupFixture);
                var startup=new FakeStartup();var startupWarnings=new List<string>();
                context=new TrayContext(false,startupShow,new FakeSource(),new ThemeSettings(Path.Combine(startupFixture,"settings.ini")),new FakeLogin(),null,text=>{},null,null,null,null,null,startup,startupWarning:text=>startupWarnings.Add(text));
                var startupItem=Field<ToolStripMenuItem>(context,"startupMenu");
                Assert(startupItem.Text=="Windowsのサインイン時に起動"&&!startupItem.Checked&&Field<ContextMenuStrip>(context,"menu").Items.Contains(startupItem),"The tray menu offers starting at sign-in, off at first");
                startupItem.PerformClick();
                Assert(startupItem.Checked&&startup.Value==StartAtSignIn.Command(Application.ExecutablePath),"Turning it on adds an entry that starts this app in the background");
                startupItem.PerformClick();
                Assert(!startupItem.Checked&&startup.Value==null,"Turning it off removes the entry");
                startup.Value=StartAtSignIn.Command(Application.ExecutablePath);
                typeof(ToolStripDropDown).GetMethod("OnOpening",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(Field<ContextMenuStrip>(context,"menu"),new object[]{new System.ComponentModel.CancelEventArgs()});
                Assert(startupItem.Checked&&startupWarnings.Count==0,"The menu shows an entry added outside the app when it opens");
                startup.Fail=true;startupItem.PerformClick();
                Assert(startupItem.Checked&&startup.Value==StartAtSignIn.Command(Application.ExecutablePath),"A change that cannot be saved leaves the shown state as it is");
                Assert(startupWarnings.Count==1&&startupWarnings[0]==StartAtSignIn.CouldNotChange,"A change that cannot be saved is reported once");
                context.ExitThread();context=null;
            }
            Console.WriteLine("RESULT "+passed+" UI checks passed");return 0;
        }
        catch(Exception e){Console.WriteLine("FAIL UI check: "+e.Message);return 1;}
        finally{if(context!=null)context.ExitThread();}
    }
}
