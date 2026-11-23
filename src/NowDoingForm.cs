using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace AIUsageManager
{
    // The いまやってること window. It shows the diary page the record goes to and today's 📝 records, takes one line of
    // text, Enter writes it to that page and Esc closes without writing. It reads only that diary and what is typed.
    internal sealed class NowDoingForm : Form
    {
        public const int MaxShown = 6;
        const string Hint = "Enter で日誌へ ／ Esc でやめる";
        // Layout in 96-DPI units; everything is multiplied by the screen's scale.
        const float Width96 = 520, Pad = 20, FieldTop = 98, FieldHeight = 42, StatusTop = 146, ListTop = 176, RowHeight = 26;

        readonly DateTime openedAt;
        readonly NowDoingView view;
        readonly Func<string, NowDoingFailure> write;
        readonly Func<NowDoingFailure, string> describe;
        readonly ThemePalette palette;
        readonly TextBox input = new TextBox();
        readonly Button close = new Button();
        readonly ToolTip tips = new ToolTip();
        readonly List<NowDoingRecord> shown = new List<NowDoingRecord>();
        Font title, strong, body, small, smallStrong;
        float scale = 1;
        string status;
        bool statusIsFailure;
        public NowDoingFailure LastFailure { get; private set; }

        public NowDoingForm(DateTime openedAt, NowDoingView view, Func<string, NowDoingFailure> write, Func<NowDoingFailure, string> describe, ThemePalette palette)
        {
            this.openedAt = openedAt; this.view = view; this.write = write; this.describe = describe; this.palette = palette;
            Text = "いまやってること"; AccessibleName = Text; AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None; MaximizeBox = false; MinimizeBox = false; DoubleBuffered = true;
            ShowInTaskbar = true; TopMost = true; StartPosition = FormStartPosition.Manual; KeyPreview = true;
            BackColor = palette.Surface; ForeColor = palette.Ink;
            input.MaxLength = NowDoingNote.MaxLength; input.Multiline = false; input.BorderStyle = BorderStyle.None;
            input.BackColor = palette.Hover; input.ForeColor = palette.Ink; input.AccessibleName = "いまやってること";
            input.AccessibleDescription = view.Path == null ? null : "書き込み先 " + view.Path;
            input.GotFocus += (s, e) => Invalidate(); input.LostFocus += (s, e) => Invalidate();
            close.Text = "×"; close.FlatStyle = FlatStyle.Flat; close.FlatAppearance.BorderSize = 0; close.TabStop = false;
            close.BackColor = palette.Surface; close.ForeColor = palette.Muted; close.Cursor = Cursors.Hand;
            close.FlatAppearance.MouseOverBackColor = palette.Hover; close.FlatAppearance.MouseDownBackColor = palette.Track;
            close.AccessibleName = "閉じる (Esc)"; tips.SetToolTip(close, close.AccessibleName);
            close.Click += (s, e) => Close();
            Controls.Add(input); Controls.Add(close);
            // Newest first; only the last few fit.
            for (int i = view.Records.Count - 1; i >= 0 && shown.Count < MaxShown; i--) shown.Add(view.Records[i]);
            if (view.Failure == NowDoingFailure.None) ShowStatus(null); else ShowStatus(describe(view.Failure));
            try { Icon = new Icon(typeof(NowDoingForm).Assembly.GetManifestResourceStream("AIUsageManager.App.ico")); } catch (ArgumentException) { }
        }

        public string Input { get { return input.Text; } set { input.Text = value; } }
        public string Status { get { return status; } }
        public bool StatusIsFailure { get { return statusIsFailure; } }
        // The page and section the record goes to, and the folder of that page.
        public string Page
        {
            get
            {
                if (view.Path == null) return "書き込み先が決まっていません";
                string section = (view.Heading ?? "").TrimStart('#', ' ');
                return System.IO.Path.GetFileNameWithoutExtension(view.Path) + (section.Length == 0 ? "" : "  ›  " + section);
            }
        }
        public string Folder { get { return view.Path == null ? "" : System.IO.Path.GetDirectoryName(view.Path); } }
        public IList<NowDoingRecord> ShownRecords { get { return shown.AsReadOnly(); } }
        // The record list is shown only when the diary could be read.
        bool HasList { get { return view.Path != null && view.Failure == NowDoingFailure.None; } }

        void ShowStatus(string failure)
        {
            status = failure ?? Hint; statusIsFailure = failure != null;
            Invalidate();
        }

        int Px(float value) { return (int)Math.Round(value * scale); }
        Rectangle Box(float x, float y, float w, float h) { return new Rectangle(Px(x), Px(y), Px(w), Px(h)); }
        static Font Make(float size, bool bold) { return new Font("Yu Gothic UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }
        float ContentHeight { get { return HasList ? ListTop + 34 + Math.Max(1, shown.Count) * RowHeight + 14 : StatusTop + 20 + 16; } }

        void Arrange()
        {
            scale = DeviceDpi / 96f;
            foreach (Font old in new[] { title, strong, body, small, smallStrong }) if (old != null) old.Dispose();
            title = Make(15 * scale, true); strong = Make(12.5f * scale, true); body = Make(12.5f * scale, false);
            small = Make(10.5f * scale, false); smallStrong = Make(10.5f * scale, true);
            Font oldInput = input.Font; input.Font = Make(16 * scale, false); if (oldInput != Control.DefaultFont) oldInput.Dispose();
            Font oldClose = close.Font; close.Font = Make(16 * scale, false); if (oldClose != Control.DefaultFont) oldClose.Dispose();
            ClientSize = new Size(Px(Width96), Px(ContentHeight));
            Rectangle field = Box(Pad, FieldTop, Width96 - 2 * Pad, FieldHeight);
            input.SetBounds(field.Left + Px(12), field.Top + (field.Height - input.Height) / 2, field.Width - Px(24), input.Height);
            close.SetBounds(Px(Width96 - 12 - 30), Px(10), Px(30), Px(30));
            // In the middle of the screen that has the mouse pointer.
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        }

        protected override void OnLoad(EventArgs e) { Arrange(); base.OnLoad(e); }
        // Moving to the mouse's screen can change the scale; the window is laid out again for it.
        protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Arrange(); Invalidate(); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); input.Focus(); }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Rounded corners on Windows 11 and the dark frame in the dark theme, as in the tray popup.
            int round = 2, dark = palette.IsDark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 33, ref round, 4); DwmSetWindowAttribute(Handle, 20, ref dark, 4);
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // Without a title bar, the window is moved by dragging any empty part of it.
            if (m.Msg == 0x84 && m.Result == (IntPtr)1) m.Result = (IntPtr)2;
        }

        void Draw(Graphics g, string text, Font font, Color color, Rectangle r, TextFormatFlags extra)
        {
            TextRenderer.DrawText(g, text, font, r, color, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | extra);
        }
        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (title == null) return;
            Graphics g = e.Graphics;
            float inner = Width96 - 2 * Pad;
            // Heading: the name of the window, and the minute the record gets.
            Draw(g, NowDoingNote.Mark + " いまやってること", title, palette.Ink, Box(Pad, 12, 260, 30), TextFormatFlags.Left);
            string clock = openedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
            Rectangle clockArea = Box(Pad + 260, 12, inner - 260 - 36, 30);
            int label = TextRenderer.MeasureText(g, " の記録", small, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
            Draw(g, " の記録", small, palette.Muted, new Rectangle(clockArea.Right - label, clockArea.Top + Px(3), label, clockArea.Height), TextFormatFlags.Left);
            Draw(g, clock, title, palette.Blue, new Rectangle(clockArea.Left, clockArea.Top, clockArea.Width - label, clockArea.Height), TextFormatFlags.Right);
            // Where the record goes: the page and section, then the folder (shortened in the middle when long).
            Draw(g, Page, strong, view.Path == null ? palette.Muted : palette.Ink, Box(Pad, 48, inner, 22), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            Draw(g, Folder, small, palette.Muted, Box(Pad, 70, inner, 18), TextFormatFlags.Left | TextFormatFlags.PathEllipsis);
            // The input box.
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle field = Box(Pad, FieldTop, inner, FieldHeight);
            using (GraphicsPath path = Rounded(field, Px(8)))
            {
                using (var fill = new SolidBrush(palette.Hover)) g.FillPath(fill, path);
                using (var pen = new Pen(input.Focused ? palette.Blue : palette.Border, Math.Max(1f, 1.5f * scale))) g.DrawPath(pen, path);
            }
            g.SmoothingMode = SmoothingMode.None;
            Draw(g, status, small, statusIsFailure ? palette.Red : palette.Muted, Box(Pad, StatusTop, inner, 20), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (HasList) PaintList(g, inner);
            using (var pen = new Pen(palette.Border)) g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        void PaintList(Graphics g, float inner)
        {
            using (var pen = new Pen(palette.Separator)) g.DrawLine(pen, Px(Pad), Px(ListTop), Px(Pad + inner), Px(ListTop));
            Draw(g, "今日の記録", smallStrong, palette.Muted, Box(Pad, ListTop + 10, 200, 18), TextFormatFlags.Left);
            int count = view.Records.Count;
            string total = count == 0 ? "" : count > shown.Count ? count + " 件（新しい " + shown.Count + " 件）" : count + " 件";
            Draw(g, total, small, palette.Muted, Box(Pad + 200, ListTop + 10, inner - 200, 18), TextFormatFlags.Right);
            float top = ListTop + 34;
            if (shown.Count == 0) { Draw(g, "まだありません", body, palette.Muted, Box(Pad, top, inner, RowHeight), TextFormatFlags.Left); return; }
            // A thin line through dots, newest at the top in the accent color.
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (shown.Count > 1)
                using (var pen = new Pen(palette.Separator, Math.Max(1f, 1.5f * scale)))
                    g.DrawLine(pen, Px(Pad + 4), Px(top + RowHeight / 2), Px(Pad + 4), Px(top + (shown.Count - 0.5f) * RowHeight));
            for (int i = 0; i < shown.Count; i++)
            {
                float y = top + i * RowHeight;
                using (var dot = new SolidBrush(i == 0 ? palette.Blue : palette.Fill))
                    g.FillEllipse(dot, Px(Pad + 4) - Px(3.5f), Px(y + RowHeight / 2) - Px(3.5f), Px(7), Px(7));
            }
            g.SmoothingMode = SmoothingMode.None;
            for (int i = 0; i < shown.Count; i++)
            {
                float y = top + i * RowHeight;
                Draw(g, shown[i].Time, body, palette.Muted, Box(Pad + 18, y, 48, RowHeight), TextFormatFlags.Left);
                Draw(g, shown[i].Text, body, palette.Ink, Box(Pad + 70, y, inner - 70, RowHeight), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // While the IME is composing, Enter and Esc go to the IME (they arrive as ProcessKey) and never get here.
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == Keys.Enter) { Submit(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        public void Submit()
        {
            string text = NowDoingNote.Clean(input.Text);
            if (text.Length == 0) { Close(); return; }
            NowDoingFailure failure = write(text);
            LastFailure = failure;
            if (failure == NowDoingFailure.None) { Close(); return; }
            // The typed text stays, so another Enter can try again.
            ShowStatus(describe(failure));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tips.Dispose();
                foreach (Font font in new[] { title, strong, body, small, smallStrong }) if (font != null) font.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Runs `AIUsageManager.exe --now-doing`: one window at a time per user, separate from the tray app.
        public static void Run()
        {
            string user;
            using (var identity = WindowsIdentity.GetCurrent()) user = identity.User.Value;
            bool created;
            using (var mutex = new Mutex(true, "Local\\AIUsageManager.NowDoing." + user, out created))
            {
                if (!created) return;
                try
                {
                    // The minute the key was pressed; the record goes to that day's diary even after midnight.
                    DateTime openedAt = DateTime.Now;
                    openedAt = new DateTime(openedAt.Year, openedAt.Month, openedAt.Day, openedAt.Hour, openedAt.Minute, 0);
                    var themes = ThemeSettings.Default();
                    string settingsPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
                    string backup = NowDoingNote.BackupPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                    // The settings read here are used for both what is shown and where it is written, so the window
                    // never writes to a page other than the one it shows.
                    NowDoingSettings settings = NowDoingSettings.Load(settingsPath);
                    NowDoingView view = NowDoingNote.Look(settings, openedAt);
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new NowDoingForm(openedAt, view,
                        text => NowDoingNote.Write(settings, openedAt, text, backup),
                        failure => NowDoingNote.Message(failure, settings), ThemePalette.Resolve(themes.Load())));
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
