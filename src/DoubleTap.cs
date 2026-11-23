using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace AIUsageManager
{
    // now_doing_hotkey=DoubleCtrl …: which key opens the いまやってること window when pressed twice. Alt was added at the
    // user's request (2026-11-23); pressing Alt alone makes many apps show their menu or key tips, and the hook does not
    // stop that.
    internal enum DoubleTapKey { None, Ctrl, LeftCtrl, RightCtrl, Shift, LeftShift, RightShift, Alt, LeftAlt, RightAlt }

    // What the keyboard hook tells the detector about a key: one of the six keys a double press can use, or "another key".
    internal enum TapKey { LeftCtrl, RightCtrl, LeftShift, RightShift, LeftAlt, RightAlt, Other }

    // Finds the chosen key pressed twice: each press at most 300 ms long, the second press at most 400 ms after the first
    // one was let go, and no other key pressed in between or while the key is held (so Ctrl+C or Shift+A never count).
    internal sealed class DoubleTapDetector
    {
        public const int MaxHoldMs = 300, MaxGapMs = 400;
        readonly DoubleTapKey key;
        TapKey held;
        bool holding, spoiled, firstDone;
        uint downAt, firstUpAt;

        public DoubleTapDetector(DoubleTapKey key) { this.key = key; }

        bool Chosen(TapKey pressed)
        {
            switch (key)
            {
                case DoubleTapKey.Ctrl: return pressed == TapKey.LeftCtrl || pressed == TapKey.RightCtrl;
                case DoubleTapKey.LeftCtrl: return pressed == TapKey.LeftCtrl;
                case DoubleTapKey.RightCtrl: return pressed == TapKey.RightCtrl;
                case DoubleTapKey.Shift: return pressed == TapKey.LeftShift || pressed == TapKey.RightShift;
                case DoubleTapKey.LeftShift: return pressed == TapKey.LeftShift;
                case DoubleTapKey.RightShift: return pressed == TapKey.RightShift;
                case DoubleTapKey.Alt: return pressed == TapKey.LeftAlt || pressed == TapKey.RightAlt;
                case DoubleTapKey.LeftAlt: return pressed == TapKey.LeftAlt;
                case DoubleTapKey.RightAlt: return pressed == TapKey.RightAlt;
                default: return false;
            }
        }

        // True when this event completes a double press. `time` is the key event's time in milliseconds (it wraps around
        // after about 49 days, so only differences are used).
        public bool Observe(TapKey pressed, bool down, uint time)
        {
            if (!Chosen(pressed))
            {
                // Another key pressed starts again; letting go of one is not pressing it.
                if (down) { firstDone = false; if (holding) spoiled = true; }
                return false;
            }
            if (down)
            {
                if (holding)
                {
                    // The same key while it is held is the key repeating; the other side's key is another key.
                    if (pressed != held) { firstDone = false; spoiled = true; }
                    return false;
                }
                holding = true; held = pressed; spoiled = false; downAt = time;
                if (firstDone && unchecked(time - firstUpAt) > MaxGapMs) firstDone = false;
                return false;
            }
            if (!holding || pressed != held) return false;
            holding = false;
            if (spoiled || unchecked(time - downAt) > MaxHoldMs) { firstDone = false; return false; }
            if (firstDone) { firstDone = false; return true; }
            firstDone = true; firstUpAt = time;
            return false;
        }
    }

    // A low-level keyboard hook (WH_KEYBOARD_LL) for the double press. Of every key event it uses only whether the key is
    // the left or right Ctrl, Shift or Alt, whether it went down or up, and its time; for any other key only that one was
    // pressed. Nothing is kept or sent, and every event goes on to the other apps unchanged.
    internal sealed class DoubleTapHook : IDisposable
    {
        const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        // Kept in a field so it is not collected while Windows still calls it.
        readonly HookProc proc;
        readonly DoubleTapDetector detector;
        readonly SynchronizationContext context;
        readonly Action pressed;
        IntPtr hook;
        Thread thread;

        public bool Installed { get { return hook != IntPtr.Zero; } }
        // The Windows thread whose message loop runs the hook.
        internal uint HookThreadId { get; private set; }

        // The hook runs on a thread of its own: every key on the PC waits for it, so it must not wait for the tray app's
        // window thread. `pressed` runs later on the thread that made the hook (the tray's), never inside the hook.
        public DoubleTapHook(DoubleTapKey key, Action pressed)
        {
            detector = new DoubleTapDetector(key);
            this.pressed = pressed;
            context = SynchronizationContext.Current;
            proc = Hook;
            using (var ready = new ManualResetEvent(false))
            {
                thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "AIUsageManager keyboard hook" };
                thread.Start();
                ready.WaitOne(5000);
            }
        }

        void Loop(ManualResetEvent ready)
        {
            Message message;
            // A message queue first, so Dispose can always post WM_QUIT to this thread.
            PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
            HookThreadId = GetCurrentThreadId();
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            ready.Set();
            if (hook == IntPtr.Zero) return;
            while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0) DispatchMessage(ref message);
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        public static TapKey Classify(int virtualKey)
        {
            switch (virtualKey)
            {
                case 0xA2: return TapKey.LeftCtrl;
                case 0xA3: return TapKey.RightCtrl;
                case 0xA0: return TapKey.LeftShift;
                case 0xA1: return TapKey.RightShift;
                case 0xA4: return TapKey.LeftAlt;
                case 0xA5: return TapKey.RightAlt;
                default: return TapKey.Other;
            }
        }

        IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int message = wParam.ToInt32();
                bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
                if (down || message == WM_KEYUP || message == WM_SYSKEYUP)
                {
                    // KBDLLHOOKSTRUCT: the virtual key is first, the time is at byte 12.
                    TapKey key = Classify(Marshal.ReadInt32(lParam));
                    uint time = unchecked((uint)Marshal.ReadInt32(lParam, 12));
                    if (detector.Observe(key, down, time))
                    {
                        if (context != null) context.Post(state => pressed(), null);
                        else ThreadPool.QueueUserWorkItem(state => pressed());
                    }
                }
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (thread == null) return;
            // The hook thread removes the hook itself and ends.
            if (HookThreadId != 0) PostThreadMessage(HookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(2000);
            thread = null;
            hook = IntPtr.Zero;
        }

        const uint WM_QUIT = 0x12;
        [StructLayout(LayoutKind.Sequential)]
        struct Message { public IntPtr Window; public uint Id; public IntPtr WParam, LParam; public uint Time; public int X, Y, Private; }
        [DllImport("user32.dll")] static extern bool PeekMessage(out Message message, IntPtr window, uint first, uint last, uint remove);
        [DllImport("user32.dll")] static extern int GetMessage(out Message message, IntPtr window, uint first, uint last);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref Message message);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    }

    // Brings the いまやってること window to the front so it can be typed into without a click. A window opened from the
    // keyboard hook usually has no permission from Windows to come to the front, so ways that send no key are tried first;
    // only for a double press, and only when those fail, one key that no app uses (virtual key 0xE8, unassigned) is sent.
    internal static class ForegroundWindow
    {
        const byte UnassignedKey = 0xE8;

        public static string Bring(IntPtr window, bool mayPressKey)
        {
            string method = Try(window, mayPressKey);
            // For checking on a real PC which way worked (DebugView and similar tools show it); nothing is saved.
            OutputDebugString("AIUsageManager now-doing foreground: " + method);
            return method;
        }

        static string Try(IntPtr window, bool mayPressKey)
        {
            if (SetForegroundWindow(window) && GetForegroundWindow() == window) return "SetForegroundWindow";
            IntPtr front = GetForegroundWindow();
            uint own = GetCurrentThreadId(), other = front == IntPtr.Zero ? 0 : GetWindowThreadProcessId(front, IntPtr.Zero);
            if (other != 0 && other != own && AttachThreadInput(own, other, true))
            {
                try { BringWindowToTop(window); SetForegroundWindow(window); }
                finally { AttachThreadInput(own, other, false); }
                if (GetForegroundWindow() == window) return "AttachThreadInput";
            }
            if (!mayPressKey) return "none";
            var inputs = new[] { Key(UnassignedKey, 0), Key(UnassignedKey, KeyUp) };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input)));
            SetForegroundWindow(window);
            return GetForegroundWindow() == window ? "SendInput" : "none";
        }

        const uint InputKeyboard = 1, KeyUp = 2;
        static Input Key(byte virtualKey, uint flags)
        {
            return new Input { Type = InputKeyboard, Data = new InputUnion { Key = new KeyInput { Vk = virtualKey, Flags = flags } } };
        }

        [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int Dx, Dy; public uint Data, Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] struct KeyInput { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
        // The mouse member makes the union as large as Windows expects.
        [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyInput Key; }
        [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public InputUnion Data; }

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr process);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint to, bool on);
        [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern void OutputDebugString(string text);
    }
}
