using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AIUsageManager
{
    // The shortcut key that opens the いまやってること window, from settings.ini (now_doing_hotkey=Ctrl+Alt+N).
    internal sealed class NowDoingHotkey
    {
        public const int Alt = 1, Control = 2, Shift = 4, Win = 8;
        public int Modifiers;
        public Keys Key;
        public string Text;
        // DoubleCtrl, DoubleLeftCtrl, DoubleRightCtrl, DoubleShift, DoubleLeftShift or DoubleRightShift: the key pressed
        // twice opens the window (found by a keyboard hook, not registered with Windows). None for Ctrl+Alt+N and the like.
        public DoubleTapKey Double;

        static readonly string[] DoubleNames = { "DoubleCtrl", "DoubleLeftCtrl", "DoubleRightCtrl", "DoubleShift", "DoubleLeftShift", "DoubleRightShift" };
        static readonly DoubleTapKey[] DoubleKeys = { DoubleTapKey.Ctrl, DoubleTapKey.LeftCtrl, DoubleTapKey.RightCtrl, DoubleTapKey.Shift, DoubleTapKey.LeftShift, DoubleTapKey.RightShift };

        // One of the six double presses, or at least one of Ctrl, Alt or Win (Shift alone would take a key away from
        // typing) and one key: A–Z, 0–9, F1–F24 or Space. Null when the text is not like that.
        public static NowDoingHotkey Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            for (int i = 0; i < DoubleNames.Length; i++)
                if (string.Equals(text.Trim(), DoubleNames[i], StringComparison.OrdinalIgnoreCase)) return new NowDoingHotkey { Double = DoubleKeys[i], Key = Keys.None, Text = text.Trim() };
            int modifiers = 0;
            Keys key = Keys.None;
            foreach (string raw in text.Split('+'))
            {
                string part = raw.Trim().ToUpperInvariant();
                if (part == "CTRL" || part == "CONTROL") modifiers |= Control;
                else if (part == "ALT") modifiers |= Alt;
                else if (part == "SHIFT") modifiers |= Shift;
                else if (part == "WIN") modifiers |= Win;
                else if (key != Keys.None) return null;
                else if (part.Length == 1 && part[0] >= 'A' && part[0] <= 'Z') key = (Keys)part[0];
                else if (part.Length == 1 && part[0] >= '0' && part[0] <= '9') key = Keys.D0 + (part[0] - '0');
                else if (part == "SPACE") key = Keys.Space;
                else if (part.Length >= 2 && part[0] == 'F')
                {
                    int number;
                    if (!int.TryParse(part.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 1 || number > 24) return null;
                    key = Keys.F1 + (number - 1);
                }
                else return null;
            }
            if (key == Keys.None || (modifiers & (Control | Alt | Win)) == 0) return null;
            return new NowDoingHotkey { Modifiers = modifiers, Key = key, Text = text.Trim() };
        }

        // What the tray says when the setting cannot be used; null when there is nothing to say.
        public static string Problem(string text, NowDoingHotkey parsed, bool registered)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (parsed == null) return "settings.ini の now_doing_hotkey「" + text.Trim() + "」の書き方が違います（例: Ctrl+Alt+N、DoubleCtrl）";
            if (!registered && parsed.Double != DoubleTapKey.None) return "「" + parsed.Text + "」を使えませんでした（キーボードの入力を受け取れません）";
            if (!registered) return "ショートカット「" + parsed.Text + "」は、ほかのアプリが使っているため使えません";
            return null;
        }
    }

    // Registers one shortcut key with Windows (RegisterHotKey) and calls back when it is pressed. Windows tells this
    // window only that this one combination was pressed; no other key presses reach it.
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        const int WM_HOTKEY = 0x0312, MOD_NOREPEAT = 0x4000, Id = 1;
        readonly Action pressed;
        public bool Registered { get; private set; }

        public HotkeyWindow(NowDoingHotkey hotkey, Action pressed)
        {
            this.pressed = pressed;
            // A message-only window: it is never shown.
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
            Registered = RegisterHotKey(Handle, Id, hotkey.Modifiers | MOD_NOREPEAT, (int)hotkey.Key);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam == (IntPtr)Id) { pressed(); return; }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                if (Registered) UnregisterHotKey(Handle, Id);
                DestroyHandle();
            }
            Registered = false;
        }

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }
}
