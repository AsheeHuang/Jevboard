using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Jevboard
{
    // Low-level keyboard hook: Caps Lock double-tap detection plus key handling while the overlay is open.
    // Runs on the UI thread that installed it, so all callbacks are UI-thread safe.
    class Hotkey : IDisposable
    {
        public const int DoubleTapMs = 350;
        public Func<string> NotApplicable;     // null when enabled + Bopomofo Chinese mode in a usable target, else the reason
        public Action DoubleTap;
        public Func<bool> OverlayShowing;
        public Func<int, bool> KeyWhileOverlay; // returns true to swallow the key (Esc cancels, Tab applies)

        IntPtr hook;
        Native.HookProc proc;                  // keep delegate alive
        Timer timer = new Timer();
        bool capsDown, pending, awaitSecondUp;
        int swallowedVk;                       // a swallowed key-down also swallows its key-up

        public Hotkey()
        {
            proc = Proc;
            timer.Interval = DoubleTapMs;
            timer.Tick += delegate { timer.Stop(); if (pending) { pending = false; Native.Tap(Native.VK_CAPITAL); } };
            hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Marshal.GetHINSTANCE(typeof(Hotkey).Module), 0);
            if (hook == IntPtr.Zero) throw new InvalidOperationException("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
        }

        IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                Native.KBDLLHOOKSTRUCT k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                int msg = wParam.ToInt32();
                bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                bool ours = k.dwExtraInfo == Native.Marker;
                if (!ours && (down || up))
                {
                    try
                    {
                        if (k.vkCode == Native.VK_CAPITAL) { if (Caps(down)) return new IntPtr(1); }
                        else if (down && OverlayShowing()) { if (KeyWhileOverlay(k.vkCode)) { swallowedVk = k.vkCode; return new IntPtr(1); } }
                        else if (up && swallowedVk != 0 && k.vkCode == swallowedVk) { swallowedVk = 0; return new IntPtr(1); }
                    }
                    catch (Exception ex) { Log.Write("hook error " + ex.GetType().Name + ": " + ex.Message); }
                }
            }
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }

        // Returns true when the event must be swallowed.
        bool Caps(bool down)
        {
            if (down)
            {
                if (capsDown) return pending || awaitSecondUp;   // auto-repeat: never counts as a press
                capsDown = true;
                if (OverlayShowing()) KeyWhileOverlay(Native.VK_CAPITAL);
                if (pending)
                {
                    pending = false; timer.Stop(); awaitSecondUp = true;
                    DoubleTap();
                    return true;
                }
                string reason = NotApplicable();
                if (reason == null) { pending = true; timer.Stop(); timer.Start(); return true; }
                Log.Write("caps lock passed through: " + reason);
                return false;
            }
            capsDown = false;
            if (pending) return true;                 // held back until single-tap timeout replays it
            if (awaitSecondUp) { awaitSecondUp = false; return true; }
            return false;
        }

        public void Dispose()
        {
            timer.Dispose();
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }
    }
}
