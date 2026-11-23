using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AIUsageManager
{
    // What the いまやってること window can do. Run() builds it from settings.ini; the tests give their own.
    internal sealed class NowDoingActions
    {
        // Writes one line to the 日中ログ (with 📝).
        public Func<string, NowDoingFailure> Write;
        public Func<NowDoingFailure, string> Describe;
        // Hands a text to Claudian (Ctrl+Enter); null when now_doing_claudian is not on.
        public Func<string, bool> Send;
        // The after script (now_doing_after): (mode "record" or "send", text). Null when it is not set.
        public Func<string, string, Task<NowDoingScriptResult>> After;
        // How long a script's message stays before the window closes.
        public int ShowOutputMs = 1500;
    }

    // The input box. Pasted text keeps its line breaks, also LF-only ones from browsers and AI apps (an edit box shows a
    // line break only for CRLF); anything that is not text is not pasted.
    internal sealed class NowDoingTextBox : TextBox
    {
        const int WM_PASTE = 0x0302;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PASTE)
            {
                if (ReadOnly) return;
                string text = null;
                try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
                catch (ExternalException) { }
                if (!string.IsNullOrEmpty(text)) Paste(text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n"));
                return;
            }
            base.WndProc(ref m);
        }
    }

    // The いまやってること window. It shows the diary page the record goes to and today's 📝 records, and takes text:
    // Enter writes it to that page as one line, Ctrl+Enter also hands it to Claudian, and Esc closes without writing.
    // After writing it can run the user's after script. It reads only that diary and what is typed.
    internal sealed class NowDoingForm : Form
    {
        public const int MaxShown = 6, MaxInputLines = 6;
        public const string RecordHint = "Enter で日誌へ ／ Shift+Enter で改行 ／ Esc でやめる";
        public const string ClaudianHint = "Enter で日誌へ ／ Ctrl+Enter で Claudian へも ／ Shift+Enter で改行";
        public const string ClaudianFailed = "日誌には書きました。Claudian には送れませんでした（Obsidian を開けません）";
        public const string TooLongToRecord = "日誌に書けるのは200文字までです";
        public const string TooLongToSend = "4000文字までです";
        public const string Working = "日誌に書きました。そのあとの処理をしています…";
        public static string AfterFailed(string reason) { return "日誌には書きました。そのあとの処理に失敗しました: " + reason; }
        // Layout in 96-DPI units; everything is multiplied by the screen's scale.
        const float Width96 = 520, Pad = 20, FieldTop = 98, FieldPadding = 20, RowHeight = 26;

        readonly DateTime openedAt;
        readonly NowDoingView view;
        readonly NowDoingActions actions;
        readonly ThemePalette palette;
        readonly NowDoingTextBox input = new NowDoingTextBox();
        readonly Button close = new Button();
        readonly ToolTip tips = new ToolTip();
        readonly List<NowDoingRecord> shown = new List<NowDoingRecord>();
        Font title, strong, body, small, smallStrong;
        float scale = 1;
        // Where the parts are, from the top (96-DPI units); they move as the box grows.
        float fieldHeight = 42, statusTop = 146, listTop = 176;
        int lines = 1;
        string status;
        bool statusIsFailure, statusIsHint, busy;
        public NowDoingFailure LastFailure { get; private set; }

        // `send` hands a written line to Claudian (Ctrl+Enter); null when now_doing_claudian is not on.
        public NowDoingForm(DateTime openedAt, NowDoingView view, Func<string, NowDoingFailure> write, Func<NowDoingFailure, string> describe, ThemePalette palette, Func<string, bool> send = null)
            : this(openedAt, view, new NowDoingActions { Write = write, Describe = describe, Send = send }, palette) { }

        public NowDoingForm(DateTime openedAt, NowDoingView view, NowDoingActions actions, ThemePalette palette)
        {
            this.openedAt = openedAt; this.view = view; this.actions = actions; this.palette = palette;
            Text = "いまやってること"; AccessibleName = Text; AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None; MaximizeBox = false; MinimizeBox = false; DoubleBuffered = true;
            ShowInTaskbar = true; TopMost = true; StartPosition = FormStartPosition.Manual; KeyPreview = true;
            BackColor = palette.Surface; ForeColor = palette.Ink;
            // Enter and Shift+Enter are handled by the window; the box only shows the lines.
            input.Multiline = true; input.WordWrap = true; input.AcceptsReturn = false; input.MaxLength = 32767; input.ScrollBars = ScrollBars.None;
            input.BorderStyle = BorderStyle.None; input.BackColor = palette.Hover; input.ForeColor = palette.Ink; input.AccessibleName = "いまやってること";
            input.AccessibleDescription = view.Path == null ? null : "書き込み先 " + view.Path;
            input.GotFocus += (s, e) => Invalidate(); input.LostFocus += (s, e) => Invalidate();
            input.TextChanged += (s, e) => FitLines();
            close.Text = "×"; close.FlatStyle = FlatStyle.Flat; close.FlatAppearance.BorderSize = 0; close.TabStop = false;
            close.BackColor = palette.Surface; close.ForeColor = palette.Muted; close.Cursor = Cursors.Hand;
            close.FlatAppearance.MouseOverBackColor = palette.Hover; close.FlatAppearance.MouseDownBackColor = palette.Track;
            close.AccessibleName = "閉じる (Esc)"; tips.SetToolTip(close, close.AccessibleName);
            close.Click += (s, e) => { if (!busy) Close(); };
            Controls.Add(input); Controls.Add(close);
            // Newest first; only the last few fit.
            for (int i = view.Records.Count - 1; i >= 0 && shown.Count < MaxShown; i--) shown.Add(view.Records[i]);
            if (view.Failure == NowDoingFailure.None) ShowHint(); else ShowStatus(actions.Describe(view.Failure), true);
            try { Icon = new Icon(typeof(NowDoingForm).Assembly.GetManifestResourceStream("AIUsageManager.App.ico")); } catch (ArgumentException) { }
        }

        public string Input { get { return input.Text; } set { input.Text = value; } }
        public string Status { get { return status; } }
        public bool StatusIsFailure { get { return statusIsFailure; } }
        // True while a script runs after writing; the window then takes no keys.
        public bool Busy { get { return busy; } }
        // The lines of the box that are shown (1 to 6).
        public int InputLines { get { return lines; } }
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

        void ShowHint() { ShowStatus(actions.Send == null ? RecordHint : ClaudianHint, false); statusIsHint = true; }
        void ShowStatus(string text, bool failure)
        {
            status = text; statusIsFailure = failure; statusIsHint = false;
            Invalidate();
        }
        void SetBusy(bool value)
        {
            busy = value; input.ReadOnly = value; close.Enabled = !value;
        }

        int Px(float value) { return (int)Math.Round(value * scale); }
        Rectangle Box(float x, float y, float w, float h) { return new Rectangle(Px(x), Px(y), Px(w), Px(h)); }
        static Font Make(float size, bool bold) { return new Font("Yu Gothic UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }
        float ContentHeight { get { return HasList ? listTop + 34 + Math.Max(1, shown.Count) * RowHeight + 14 : statusTop + 20 + 16; } }

        bool placed;
        void Arrange()
        {
            scale = DeviceDpi / 96f;
            foreach (Font old in new[] { title, strong, body, small, smallStrong }) if (old != null) old.Dispose();
            title = Make(15 * scale, true); strong = Make(12.5f * scale, true); body = Make(12.5f * scale, false);
            small = Make(10.5f * scale, false); smallStrong = Make(10.5f * scale, true);
            Font oldInput = input.Font; input.Font = Make(16 * scale, false); if (oldInput != Control.DefaultFont) oldInput.Dispose();
            Font oldClose = close.Font; close.Font = Make(16 * scale, false); if (oldClose != Control.DefaultFont) oldClose.Dispose();
            Place();
        }

        // Lays the parts out for the current number of lines in the box.
        void Place()
        {
            int lineHeight = input.Font.Height;
            fieldHeight = (Px(FieldPadding) + lineHeight * lines) / scale;
            statusTop = FieldTop + fieldHeight + 6;
            listTop = statusTop + 30;
            ClientSize = new Size(Px(Width96), Px(ContentHeight));
            Rectangle field = Box(Pad, FieldTop, Width96 - 2 * Pad, fieldHeight);
            input.SetBounds(field.Left + Px(12), field.Top + Px(FieldPadding / 2), field.Width - Px(24), lineHeight * lines);
            close.SetBounds(Px(Width96 - 12 - 30), Px(10), Px(30), Px(30));
            Rectangle area = Screen.FromPoint(placed ? Location : Cursor.Position).WorkingArea;
            // At first in the middle of the screen that has the mouse pointer; once shown, it stays where it is (it may
            // be dragged), and only moves up when it grows past the bottom of the screen.
            if (!placed) Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            else if (Bottom > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height);
            Invalidate();
        }

        // The box grows with its lines up to six; past that it scrolls.
        void FitLines()
        {
            if (!input.IsHandleCreated || title == null) return;
            int count = input.GetLineFromCharIndex(input.TextLength) + 1;
            ScrollBars bars = count > MaxInputLines ? ScrollBars.Vertical : ScrollBars.None;
            if (input.ScrollBars != bars) input.ScrollBars = bars;
            int visible = Math.Max(1, Math.Min(MaxInputLines, count));
            if (visible != lines) { lines = visible; Place(); }
        }

        protected override void OnLoad(EventArgs e) { Arrange(); base.OnLoad(e); }
        // Moving to a screen with another scale lays the window out again for it.
        protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Arrange(); Invalidate(); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); placed = true; Activate(); input.Focus(); FitLines(); }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // While a script runs after writing, the window waits for it (at most 10 s), so a send to Claudian is not lost.
            if (busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
            base.OnFormClosing(e);
        }
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
            Rectangle field = Box(Pad, FieldTop, inner, fieldHeight);
            using (GraphicsPath path = Rounded(field, Px(8)))
            {
                using (var fill = new SolidBrush(palette.Hover)) g.FillPath(fill, path);
                using (var pen = new Pen(input.Focused ? palette.Blue : palette.Border, Math.Max(1f, 1.5f * scale))) g.DrawPath(pen, path);
            }
            g.SmoothingMode = SmoothingMode.None;
            Color statusColor = statusIsFailure ? palette.Red : statusIsHint ? palette.Muted : palette.Ink;
            Draw(g, status, small, statusColor, Box(Pad, statusTop, inner, 20), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (HasList) PaintList(g, inner);
            using (var pen = new Pen(palette.Border)) g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        void PaintList(Graphics g, float inner)
        {
            using (var pen = new Pen(palette.Separator)) g.DrawLine(pen, Px(Pad), Px(listTop), Px(Pad + inner), Px(listTop));
            Draw(g, "今日の記録", smallStrong, palette.Muted, Box(Pad, listTop + 10, 200, 18), TextFormatFlags.Left);
            int count = view.Records.Count;
            string total = count == 0 ? "" : count > shown.Count ? count + " 件（新しい " + shown.Count + " 件）" : count + " 件";
            Draw(g, total, small, palette.Muted, Box(Pad + 200, listTop + 10, inner - 200, 18), TextFormatFlags.Right);
            float top = listTop + 34;
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
            if (keyData == Keys.Escape) { if (!busy) Close(); return true; }
            if (keyData == Keys.Enter) { Submit(false); return true; }
            if (keyData == (Keys.Shift | Keys.Enter)) { if (!busy) input.Paste("\r\n"); return true; }
            // Ctrl+Enter is a plain Enter when sending to Claudian is not on.
            if (keyData == (Keys.Control | Keys.Enter)) { Submit(actions.Send != null); return true; }
            // A multi-line edit box does not select everything with Ctrl+A by itself.
            if (keyData == (Keys.Control | Keys.A)) { input.SelectAll(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // The same as pressing the keys in the window (for the tests).
        internal bool Press(Keys keyData) { var message = new Message(); return ProcessCmdKey(ref message, keyData); }

        public void Submit() { Submit(false); }

        public void Submit(bool toClaudian)
        {
            if (busy) return;
            string mode, line, text, sendText = null;
            if (toClaudian && actions.Send != null)
            {
                // The diary gets the first line; Claudian gets the whole text.
                text = NowDoingNote.ForClaudian(input.Text);
                if (text.Length == 0) { Close(); return; }
                if (text.Length > NowDoingNote.MaxSendLength) { ShowStatus(TooLongToSend, true); return; }
                mode = "send"; line = NowDoingNote.SendLine(text); sendText = text;
            }
            else
            {
                // Several lines become one; a longer text is not cut without a word, it stays for the user to shorten.
                text = NowDoingNote.Clean(input.Text, int.MaxValue);
                if (text.Length == 0) { Close(); return; }
                if (text.Length > NowDoingNote.MaxLength) { ShowStatus(TooLongToRecord, true); return; }
                mode = "record"; line = text;
            }
            NowDoingFailure failure = actions.Write(line);
            LastFailure = failure;
            // The typed text stays, so another Enter can try again.
            if (failure != NowDoingFailure.None) { ShowStatus(actions.Describe(failure), true); return; }
            Finish(mode, text, sendText);
        }

        // After the line is in the diary: the after script, then (Ctrl+Enter) Claudian, even when the script failed.
        async void Finish(string mode, string text, string sendText)
        {
            NowDoingScriptResult result = null;
            if (actions.After != null)
            {
                SetBusy(true);
                ShowStatus(Working, false);
                result = await actions.After(mode, text);
                if (IsDisposed) return;
            }
            bool sent = sendText == null || actions.Send(sendText);
            string problem = !sent ? ClaudianFailed : result != null && !result.Ok ? AfterFailed(result.Reason) : null;
            if (problem != null)
            {
                // It is in the diary already, so the box is emptied and another Enter cannot write it twice.
                SetBusy(false);
                input.Text = "";
                ShowStatus(problem, true);
                return;
            }
            string first = result == null ? "" : result.FirstLine.Trim();
            if (first.Length > 0)
            {
                SetBusy(true);
                ShowStatus(first, false);
                await Task.Delay(actions.ShowOutputMs);
                if (IsDisposed) return;
            }
            SetBusy(false);
            Close();
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
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string backup = NowDoingNote.BackupPath(localAppData);
                    // The settings read here are used for both what is shown and where it is written, so the window
                    // never writes to a page other than the one it shows.
                    NowDoingSettings settings = NowDoingSettings.Load(settingsPath);
                    NowDoingView view = NowDoingNote.Look(settings, openedAt);
                    string message = NowDoingNote.ClaudianMessagePath(localAppData);
                    NowDoingNote.DropStaleMessage(message, DateTime.UtcNow);
                    string workdir = string.IsNullOrEmpty(settings.Workdir) ? AppDomain.CurrentDomain.BaseDirectory : settings.Workdir;
                    var actions = new NowDoingActions
                    {
                        Write = text => NowDoingNote.Write(settings, openedAt, text, backup),
                        Describe = failure => NowDoingNote.Message(failure, settings)
                    };
                    string note = view.Path;
                    if (settings.Claudian && note != null)
                        actions.Send = text => NowDoingNote.HandToClaudian(text, message, NowDoingNote.VaultName(note), NowDoingNote.OpenLink, note: note);
                    if (settings.After != null && note != null)
                    {
                        string time = openedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
                        actions.After = (mode, text) => NowDoingScript.RunAsync(settings.After, new[] { "--note", note, "--time", time, "--mode", mode, "--text", text },
                            workdir, NowDoingScriptLimits.After, CancellationToken.None);
                    }
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new NowDoingForm(openedAt, view, actions, ThemePalette.Resolve(themes.Load())));
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
