using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AIUsageManager
{
    internal sealed class UsageRow : Control
    {
        ThemePalette palette=ThemePalette.Light;
        ServiceUsage usage;
        readonly Button login = new Button { Text="ログイン", FlatStyle=FlatStyle.Flat, Cursor=Cursors.Hand, Visible=false };
        public event Action<string> LoginRequested;
        public ServiceUsage Usage
        {
            get { return usage; }
            set
            {
                usage=value;
                login.Visible=value.NeedsLogin&&(value.Name=="Codex"||value.Name=="Grok");
                login.AccessibleName=value.Name+" にログイン";
                AccessibleName=value.Name+" "+value.Plan+" "+string.Join("、",value.Windows.Select(w=>w.Label+" "+UsageText.Percent(w.Percent)+" "+UsageText.Reset(w,DateTimeOffset.Now)));
                AccessibleDescription=Stamp(value)+" "+value.Status+" "+value.PlanNote;
                Invalidate();
            }
        }
        public UsageRow() {
            DoubleBuffered=true;BackColor=Color.White;AccessibleRole=AccessibleRole.Grouping;TabStop=false;
            login.FlatAppearance.BorderSize=0;Controls.Add(login);
            login.Click+=(s,e)=>{if(usage!=null&&LoginRequested!=null)LoginRequested(usage.Name);};
        }
        public void SetLoginBusy(bool busy) { login.Enabled=!busy;login.Text=busy?"ログイン中":"ログイン"; }
        protected override void OnResize(EventArgs e) {
            base.OnResize(e);float scale=Width/388f;
            login.SetBounds((int)(294*scale),(int)(101*scale),(int)(91*scale),(int)(27*scale));
            var old=login.Font;login.Font=new Font("Yu Gothic UI",9*scale);if(old!=Control.DefaultFont)old.Dispose();
        }
        public void ApplyTheme(ThemePalette value) { palette=value;BackColor=value.Surface;ForeColor=value.Ink;login.BackColor=value.Surface;login.ForeColor=value.Blue;login.FlatAppearance.MouseOverBackColor=value.Hover;Invalidate(); }
        static string Stamp(ServiceUsage value)
        { return value.RecordedAt.HasValue ? "取得 "+value.RecordedAt.Value.ToLocalTime().ToString("MM/dd HH:mm:ss") : (value.HasData ? "取得時刻不明" : "取得時刻 —"); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);if(usage==null)return;
            var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float scale=Width/388f;g.ScaleTransform(scale,scale);
            Artwork.Provider(g,usage.Name,0,14,palette);
            Artwork.Text(g,usage.Name,14,true,palette.Ink,new RectangleF(39,11,60,23),false);
            Artwork.Text(g,usage.Plan+(string.IsNullOrEmpty(usage.PlanNote)?"":" *"),11,false,palette.Muted,new RectangleF(104,12,160,23),false);
            bool online=usage.Source=="オンライン";
            using(var b=new SolidBrush(online?palette.Blue:palette.Muted))g.FillEllipse(b,302,21,5,5);
            Artwork.Text(g,string.IsNullOrEmpty(usage.Source)?"未取得":usage.Source,10,false,palette.Muted,new RectangleF(310,14,78,19),false);
            int count=usage.Windows.Count;float column=348f/count;
            for(int i=0;i<count;i++)
            {
                var w=usage.Windows[i];float left=39+i*column;
                bool expired=w.ResetsAt.HasValue && w.ResetsAt.Value<=DateTimeOffset.Now;
                Color fill=w.Percent>=80?palette.Red:palette.Fill;
                Artwork.Text(g,w.Label,11,false,palette.Muted,new RectangleF(left,41,35,20),false);
                Artwork.Text(g,UsageText.Percent(w.Percent),12,true,w.Percent.HasValue?fill:palette.Muted,new RectangleF(left+column-60,40,51,20),true);
                float barLeft=left+(count==2?36:26),barWidth=column-(count==2?94:84);
                Artwork.Pill(g,palette.Track,new RectangleF(barLeft,49,barWidth,5));
                if(w.Percent.HasValue && w.Percent>0)Artwork.Pill(g,fill,new RectangleF(barLeft,49,(float)(barWidth*Math.Min(100,w.Percent.Value)/100),5));
                if(!w.Percent.HasValue)Artwork.Text(g,"—",10,false,palette.Muted,new RectangleF(barLeft,41,barWidth,18),false);
                Artwork.Text(g,UsageText.Reset(w,DateTimeOffset.Now),10,false,expired?palette.Red:palette.Muted,new RectangleF(left,63,column-6,18),false);
            }
            string stamp=Stamp(usage);
            bool old=usage.RecordedAt.HasValue && DateTimeOffset.Now-usage.RecordedAt.Value>TimeSpan.FromMinutes(15);
            if(old)stamp+="  ·  保存された値";
            Artwork.Text(g,stamp,10,false,palette.Muted,new RectangleF(39,87,349,18),false);
            if(!string.IsNullOrEmpty(usage.Status))Artwork.Text(g,usage.Status,10,false,palette.Warning,new RectangleF(39,105,usage.NeedsLogin?253:349,18),false);
            using(var pen=new Pen(palette.Separator))g.DrawLine(pen,39,Height/scale-1,388,Height/scale-1);
        }
    }
    public sealed class PopupForm : Form
    {
        readonly UsageRow[] rows={new UsageRow(),new UsageRow(),new UsageRow()};
        readonly Button refresh=new Button(),close=new Button();
        readonly ToolTip tips=new ToolTip();
        readonly Icon windowIcon=Artwork.TrayIcon();
        ThemePalette palette=ThemePalette.Light;
        float scale=1;
        bool busy;
        public DateTimeOffset LastDeactivated { get; private set; }
        DateTimeOffset? nextRefresh;
        public event EventHandler RefreshRequested;
        public event Action<string> LoginRequested;
        public PopupForm()
        {
            Text="AI Usage Manager";Icon=windowIcon;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;
            AutoScaleMode=AutoScaleMode.None;BackColor=Color.White;DoubleBuffered=true;KeyPreview=true;
            AccessibleName="AI 使用量";
            foreach(var row in rows){Controls.Add(row);row.LoginRequested+=name=>{if(LoginRequested!=null)LoginRequested(name);};}
            ConfigureButton(refresh,"更新", "使用量を更新 (F5)");ConfigureButton(close,"×","閉じる (Esc)");
            tips.OwnerDraw=true;
            tips.Draw+=(s,e)=> {
                using(var b=new SolidBrush(palette.Surface))e.Graphics.FillRectangle(b,e.Bounds);
                using(var p=new Pen(palette.Border))e.Graphics.DrawRectangle(p,e.Bounds.X,e.Bounds.Y,e.Bounds.Width-1,e.Bounds.Height-1);
                var bounds=Rectangle.Inflate(e.Bounds,-4,-2);
                TextRenderer.DrawText(e.Graphics,e.ToolTipText,e.Font,bounds,palette.Ink,TextFormatFlags.NoPrefix|TextFormatFlags.Left|TextFormatFlags.VerticalCenter);
            };
            refresh.Click+=(s,e)=>{if(RefreshRequested!=null)RefreshRequested(this,EventArgs.Empty);};
            close.Click+=(s,e)=>Hide();
            Deactivate+=(s,e)=>{LastDeactivated=DateTimeOffset.UtcNow;Hide();};
            KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Escape){Hide();e.Handled=true;}if(e.KeyCode==Keys.F5){if(!busy&&RefreshRequested!=null)RefreshRequested(this,EventArgs.Empty);e.Handled=true;}};
            SetUsage(new[]{ServiceUsage.Empty("Claude",""),ServiceUsage.Empty("Codex",""),ServiceUsage.Empty("Grok","")});
            SetTheme(ThemeMode.System);
        }
        void ConfigureButton(Button button,string text,string label)
        {
            button.Text=text;button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderSize=0;
            button.Cursor=Cursors.Hand;button.AccessibleName=label;tips.SetToolTip(button,label);Controls.Add(button);
        }
        public void SetTheme(ThemeMode mode) { ApplyTheme(ThemePalette.Resolve(mode)); }
        internal void ApplyTheme(ThemePalette value)
        {
            palette=value;BackColor=value.Surface;ForeColor=value.Ink;
            foreach(var row in rows)row.ApplyTheme(value);
            foreach(var button in new[]{refresh,close}) {
                button.BackColor=value.Surface;button.ForeColor=value.Muted;
                button.FlatAppearance.MouseOverBackColor=value.Hover;button.FlatAppearance.MouseDownBackColor=value.Track;
            }
            tips.BackColor=value.Surface;tips.ForeColor=value.Ink;
            if(IsHandleCreated)ApplyWindowTheme();
            Invalidate(true);
        }
        void ApplyWindowTheme() { int dark=palette.IsDark?1:0;DwmSetWindowAttribute(Handle,20,ref dark,4); }
        public void SetUsage(ServiceUsage[] values)
        {
            for(int i=0;i<rows.Length;i++) { rows[i].Usage=values[i];tips.SetToolTip(rows[i],rows[i].AccessibleName+"\n"+rows[i].AccessibleDescription); }
            LayoutRows();Invalidate();if(Visible)ClampToScreen();
        }
        public void SetRefresh(bool isBusy,DateTimeOffset? next)
        {busy=isBusy;nextRefresh=next;refresh.Enabled=!busy;refresh.Text=busy?"更新中":"更新";Invalidate();}
        public void SetLoginBusy(bool value) { foreach(var row in rows)row.SetLoginBusy(value); }
        public void TickDisplay() { if(Visible){foreach(var row in rows)row.Invalidate();Invalidate();} }
        void LayoutRows()
        {
            int y=68;
            foreach(var row in rows)
            {
                int height=string.IsNullOrEmpty(row.Usage.Status)?116:134;
                row.SetBounds(P(16),P(y),P(388),P(height));y+=height;
            }
            ClientSize=new Size(P(420),P(y+44));
            refresh.SetBounds(P(302),P(17),P(66),P(32));close.SetBounds(P(373),P(17),P(28),P(32));
            var old=refresh.Font;refresh.Font=new Font("Yu Gothic UI",9*scale);if(old!=Control.DefaultFont)old.Dispose();
            old=close.Font;close.Font=new Font("Segoe UI",14*scale);if(old!=Control.DefaultFont)old.Dispose();
        }
        int P(float n) { return (int)Math.Round(n*scale); }
        public void OpenAt(Point anchor)
        {
            var monitor=MonitorFromPoint(anchor,2);uint x,y;
            try { if(GetDpiForMonitor(monitor,0,out x,out y)==0)scale=x/96f; }catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
            LayoutRows();var area=Screen.FromPoint(anchor).WorkingArea;
            Location=new Point(Math.Max(area.Left+P(8),Math.Min(anchor.X-Width/2,area.Right-Width-P(8))),Math.Max(area.Top+P(8),Math.Min(anchor.Y-Height-P(10),area.Bottom-Height-P(8))));
            Show();Activate();refresh.Focus();
        }
        void ClampToScreen()
        {
            var area=Screen.FromControl(this).WorkingArea;
            Location=new Point(Math.Max(area.Left,Math.Min(Left,area.Right-Width)),Math.Max(area.Top,Math.Min(Top,area.Bottom-Height)));
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}
            base.OnFormClosing(e);
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if(m.Msg==0x02E0){scale=(m.WParam.ToInt32()&0xffff)/96f;LayoutRows();ClampToScreen();}
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);int value=2;DwmSetWindowAttribute(Handle,33,ref value,4);ApplyWindowTheme();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);var g=e.Graphics;g.ScaleTransform(scale,scale);g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Artwork.Text(g,"使用量",18,true,palette.Ink,new RectangleF(20,16,150,28),false);
            Artwork.Text(g,"AI Usage Manager",10,false,palette.Muted,new RectangleF(20,44,200,18),false);
            string footer=busy?"使用量を取得しています…":"5分ごとに自動更新";
            if(!busy&&nextRefresh.HasValue)footer+="  ·  次回 "+nextRefresh.Value.ToLocalTime().ToString("HH:mm");
            Artwork.Text(g,footer,10,false,palette.Muted,new RectangleF(20,ClientSize.Height/scale-35,380,26),false);
            using(var pen=new Pen(palette.Border))g.DrawRectangle(pen,0,0,ClientSize.Width/scale-1,ClientSize.Height/scale-1);
        }
        protected override void Dispose(bool disposing){if(disposing)tips.Dispose();base.Dispose(disposing);if(disposing)windowIcon.Dispose();}
        [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
        [DllImport("user32.dll")]static extern IntPtr MonitorFromPoint(Point point,uint flags);
        [DllImport("shcore.dll")]static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint x,out uint y);
    }
}
