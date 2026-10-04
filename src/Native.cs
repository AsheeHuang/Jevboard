using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Jevboard
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }
        [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public int vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit, Size = 40)] public struct INPUT { [FieldOffset(0)] public int type; [FieldOffset(8)] public KEYBDINPUT ki; }

        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        public const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        public const int VK_TAB = 0x09, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B, VK_END = 0x23, VK_HOME = 0x24, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_NUMPAD1 = 0x61;
        public const int LLKHF_INJECTED = 0x10;
        public const uint KEYEVENTF_KEYUP = 2;
        public const int WM_IME_CONTROL = 0x283, IMC_GETCONVERSIONMODE = 1, IMC_GETOPENSTATUS = 5, IME_CMODE_NATIVE = 1;
        public const int WM_GETTEXT = 0xD, WM_GETTEXTLENGTH = 0xE, EM_GETSEL = 0xB0;
        public const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        public const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        public const uint SWP_NOSIZE = 1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        // Marks keyboard events Jevboard injects itself so the hook never treats them as user presses.
        public static readonly IntPtr Marker = new IntPtr(0x4A455642);

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int size);
        [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint threadId);
        [DllImport("imm32.dll")] public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, int msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern uint MapVirtualKeyW(uint code, uint type);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public static string ClassName(IntPtr hwnd)
        {
            StringBuilder s = new StringBuilder(256);
            GetClassNameW(hwnd, s, s.Capacity);
            return s.ToString();
        }

        public static IntPtr FocusOf(IntPtr foreground, out RECT caret, out IntPtr caretWindow)
        {
            GUITHREADINFO info = new GUITHREADINFO();
            info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            uint pid;
            uint thread = GetWindowThreadProcessId(foreground, out pid);
            caret = new RECT(); caretWindow = IntPtr.Zero;
            if (thread == 0 || !GetGUIThreadInfo(thread, ref info)) return IntPtr.Zero;
            caret = info.rcCaret; caretWindow = info.hwndCaret;
            return info.hwndFocus;
        }

        public static long Send(IntPtr hwnd, int msg, int wParam, int lParam)
        {
            IntPtr result;
            if (SendMessageTimeoutW(hwnd, msg, new IntPtr(wParam), new IntPtr(lParam), 2 /*SMTO_ABORTIFHUNG*/, 100, out result) == IntPtr.Zero) return -1;
            return result.ToInt64();
        }

        // Modifier + key, e.g. Ctrl+V, marked like every other injected event.
        public static void Chord(ushort modifier, ushort vk)
        {
            INPUT[] inputs = new INPUT[4];
            ushort[] keys = { modifier, vk, vk, modifier };
            for (int i = 0; i < 4; i++)
            {
                inputs[i].type = 1;
                inputs[i].ki.wVk = keys[i];
                inputs[i].ki.wScan = (ushort)MapVirtualKeyW(keys[i], 0);
                inputs[i].ki.dwFlags = i >= 2 ? KEYEVENTF_KEYUP : 0;
                inputs[i].ki.dwExtraInfo = Marker;
            }
            SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void Tap(ushort vk)
        {
            INPUT[] inputs = new INPUT[2];
            for (int i = 0; i < 2; i++)
            {
                inputs[i].type = 1;
                inputs[i].ki.wVk = vk;
                inputs[i].ki.wScan = (ushort)MapVirtualKeyW(vk, 0);
                inputs[i].ki.dwFlags = i == 1 ? KEYEVENTF_KEYUP : 0;
                inputs[i].ki.dwExtraInfo = Marker;
            }
            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
