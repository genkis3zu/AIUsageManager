using System;
using System.Drawing;
using System.IO;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AIUsageManager
{
    public enum ThemeMode { System, Light, Dark }

    internal sealed class ThemeSettings
    {
        readonly string path;
        public ThemeSettings(string path) { this.path = path; }
        public static ThemeSettings Default()
        { return new ThemeSettings(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini")); }
        string ReadSettings()
        {
            try
            {
                using (var reader = new StreamReader(path))
                {
                    char[] buffer = new char[4097];
                    int read=reader.ReadBlock(buffer,0,buffer.Length);
                    return read>4096?"":new string(buffer,0,read);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            return "";
        }
        public ThemeMode Load()
        {
            foreach(string line in ReadSettings().Split('\n')) {
                if(line.Trim()=="theme=dark")return ThemeMode.Dark;
                if(line.Trim()=="theme=light")return ThemeMode.Light;
            }
            return ThemeMode.System;
        }
        public NotificationMode LoadNotifications()
        {
            foreach(string line in ReadSettings().Split('\n')) {
                if(line.Trim()=="notifications=off")return NotificationMode.Off;
                if(line.Trim()=="notifications=thresholds")return NotificationMode.Thresholds;
            }
            return NotificationMode.All;
        }
        public bool Save(ThemeMode mode)
        { return Save(mode,LoadNotifications()); }
        public bool SaveNotifications(NotificationMode mode)
        { return Save(Load(),mode); }
        bool Save(ThemeMode mode,NotificationMode notifications)
        {
            string value = mode == ThemeMode.Dark ? "dark" : mode == ThemeMode.Light ? "light" : "system";
            string alertValue=notifications==NotificationMode.Off?"off":notifications==NotificationMode.Thresholds?"thresholds":"all";
            try
            {
                File.WriteAllText(path + ".tmp", "theme=" + value + Environment.NewLine+"notifications="+alertValue+Environment.NewLine);
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
                else File.Move(path + ".tmp", path);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (SecurityException) { return false; }
        }
    }

    internal sealed class ThemePalette
    {
        public bool IsDark;
        public Color Surface, Ink, Muted, Border, Separator, Track, Fill, Red, Blue, Warning, Hover, IconBackground, ClaudeBackground, ClaudeInk;
        public static readonly ThemePalette Light = new ThemePalette {
            Surface = Color.White, Ink = Color.FromArgb(35,39,46), Muted = Color.FromArgb(100,107,120),
            Border = Color.FromArgb(219,222,228), Separator = Color.FromArgb(237,238,241),
            Track = Color.FromArgb(235,237,241), Fill = Color.FromArgb(108,117,132),
            Red = Color.FromArgb(209,42,59), Blue = Color.FromArgb(55,111,210), Warning = Color.FromArgb(143,94,53),
            Hover = Color.FromArgb(240,242,246), IconBackground = Color.FromArgb(239,241,244),
            ClaudeBackground = Color.FromArgb(251,237,230), ClaudeInk = Color.FromArgb(196,109,80)
        };
        public static readonly ThemePalette Dark = new ThemePalette {
            IsDark = true, Surface = Color.FromArgb(32,34,39), Ink = Color.FromArgb(240,242,247), Muted = Color.FromArgb(173,181,195),
            Border = Color.FromArgb(71,75,86), Separator = Color.FromArgb(53,57,65),
            Track = Color.FromArgb(60,65,76), Fill = Color.FromArgb(166,178,198),
            Red = Color.FromArgb(255,113,128), Blue = Color.FromArgb(129,178,255), Warning = Color.FromArgb(236,185,131),
            Hover = Color.FromArgb(53,58,68), IconBackground = Color.FromArgb(54,59,69),
            ClaudeBackground = Color.FromArgb(68,48,43), ClaudeInk = Color.FromArgb(237,164,133)
        };
        public static ThemePalette Select(ThemeMode mode, bool systemDark)
        { return mode == ThemeMode.Dark || (mode == ThemeMode.System && systemDark) ? Dark : Light; }
        public static ThemePalette Resolve(ThemeMode mode)
        {
            // Respect Windows contrast themes even when a fixed appearance was selected.
            if (SystemInformation.HighContrast)
                return new ThemePalette {
                    IsDark = SystemColors.Window.GetBrightness() < 0.5f,
                    Surface = SystemColors.Window, Ink = SystemColors.WindowText, Muted = SystemColors.WindowText,
                    Border = SystemColors.WindowText, Separator = SystemColors.WindowText, Track = SystemColors.ControlDark,
                    Fill = SystemColors.Highlight, Red = SystemColors.WindowText, Blue = SystemColors.WindowText,
                    Warning = SystemColors.WindowText, Hover = SystemColors.Control, IconBackground = SystemColors.Window,
                    ClaudeBackground = SystemColors.Window, ClaudeInk = SystemColors.WindowText
                };
            bool dark = false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
                {
                    object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    dark = value is int && (int)value == 0;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            return Select(mode, dark);
        }
    }

    internal sealed class ThemeMenuRenderer : ToolStripProfessionalRenderer
    {
        readonly ThemePalette palette;
        public ThemeMenuRenderer(ThemePalette palette) { this.palette = palette; RoundedEdges = false; }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        { using (var b = new SolidBrush(palette.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        { using (var p = new Pen(palette.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        { using (var b = new SolidBrush(e.Item.Selected ? palette.Hover : palette.Surface)) e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size)); }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        { e.TextColor = e.Item.Enabled ? palette.Ink : palette.Muted; base.OnRenderItemText(e); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        { e.ArrowColor = palette.Ink; base.OnRenderArrow(e); }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        { using (var p = new Pen(palette.Separator)) e.Graphics.DrawLine(p, 8, e.Item.Height / 2, e.Item.Width - 8, e.Item.Height / 2); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            using (var p = new Pen(palette.Blue, Math.Max(1.5f, r.Height / 8f)))
                e.Graphics.DrawLines(p, new[] { new PointF(r.Left + r.Width * .2f, r.Top + r.Height * .5f), new PointF(r.Left + r.Width * .43f, r.Top + r.Height * .73f), new PointF(r.Left + r.Width * .85f, r.Top + r.Height * .25f) });
        }
    }
}
