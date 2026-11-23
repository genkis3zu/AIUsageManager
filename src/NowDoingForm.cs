using System;
using System.Drawing;
using System.Globalization;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace AIUsageManager
{
    // The いまやってること window: one line of text, Enter writes it to the day's diary, Esc closes without writing.
    // It shows nothing from the diary and reads nothing but what is typed into it.
    internal sealed class NowDoingForm : Form
    {
        readonly DateTime openedAt;
        readonly Func<string, NowDoingFailure> write;
        readonly Func<NowDoingFailure, string> describe;
        readonly ThemePalette palette;
        readonly Label header = new Label();
        readonly TextBox input = new TextBox();
        public NowDoingFailure LastFailure { get; private set; }

        public NowDoingForm(DateTime openedAt, Func<string, NowDoingFailure> write, Func<NowDoingFailure, string> describe, ThemePalette palette)
        {
            this.openedAt = openedAt; this.write = write; this.describe = describe; this.palette = palette;
            Text = "いまやってること"; AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = true; TopMost = true; StartPosition = FormStartPosition.Manual; KeyPreview = true;
            BackColor = palette.Surface;
            header.AutoSize = false; header.TextAlign = ContentAlignment.MiddleLeft; header.BackColor = palette.Surface;
            input.MaxLength = NowDoingNote.MaxLength; input.Multiline = false; input.BorderStyle = BorderStyle.FixedSingle;
            input.BackColor = palette.Surface; input.ForeColor = palette.Ink;
            Controls.Add(header); Controls.Add(input);
            ShowHeader(null);
            try { Icon = new Icon(typeof(NowDoingForm).Assembly.GetManifestResourceStream("AIUsageManager.App.ico")); } catch (ArgumentException) { }
        }

        public string Heading { get { return header.Text; } }
        public string Input { get { return input.Text; } set { input.Text = value; } }

        void ShowHeader(string failure)
        {
            header.Text = failure ?? openedAt.ToString("HH:mm", CultureInfo.InvariantCulture) + " の記録　Enter で日誌へ ／ Esc でやめる";
            header.ForeColor = failure == null ? palette.Muted : palette.Red;
        }

        void Arrange()
        {
            float scale = DeviceDpi / 96f;
            var old = Font; Font = new Font("Yu Gothic UI", 10f * scale, FontStyle.Regular, GraphicsUnit.Pixel); if (old != Control.DefaultFont) old.Dispose();
            old = input.Font; input.Font = new Font("Yu Gothic UI", 15f * scale, FontStyle.Regular, GraphicsUnit.Pixel); if (old != Control.DefaultFont && old != Font) old.Dispose();
            int pad = (int)(12 * scale), width = (int)(420 * scale);
            ClientSize = new Size(width, (int)(84 * scale));
            header.SetBounds(pad, (int)(8 * scale), width - 2 * pad, (int)(24 * scale));
            input.SetBounds(pad, (int)(38 * scale), width - 2 * pad, input.PreferredHeight);
            // In the middle of the screen that has the mouse pointer.
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        }

        protected override void OnLoad(EventArgs e) { Arrange(); base.OnLoad(e); }
        // Moving to the mouse's screen can change the scale; the window is laid out again for it.
        protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Arrange(); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); input.Focus(); }

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
            ShowHeader(describe(failure));
        }

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
                    // The minute the key was pressed; the line goes to that day's diary even after midnight.
                    DateTime openedAt = DateTime.Now;
                    openedAt = new DateTime(openedAt.Year, openedAt.Month, openedAt.Day, openedAt.Hour, openedAt.Minute, 0);
                    var themes = ThemeSettings.Default();
                    string settingsPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
                    string backup = NowDoingNote.BackupPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                    NowDoingSettings shown = NowDoingSettings.Load(settingsPath);
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new NowDoingForm(openedAt,
                        text => { shown = NowDoingSettings.Load(settingsPath); return NowDoingNote.Write(shown, openedAt, text, backup); },
                        failure => NowDoingNote.Message(failure, shown), ThemePalette.Resolve(themes.Load())));
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
