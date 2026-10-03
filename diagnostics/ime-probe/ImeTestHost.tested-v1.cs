using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Input = System.Windows.Input;
using Interop = System.Windows.Interop;

namespace JevboardImeProbe
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : (Path.GetFileNameWithoutExtension(System.Reflection.Assembly.GetExecutingAssembly().Location).StartsWith("Wpf", StringComparison.OrdinalIgnoreCase) ? "wpf" : "winforms");
            string logDir = args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Log.Init(mode, logDir);
            if (mode == "wpf")
            {
                Wpf.Application app = new Wpf.Application();
                app.Run(new WpfHost());
            }
            else
            {
                Forms.Application.EnableVisualStyles();
                Forms.Application.SetCompatibleTextRenderingDefault(false);
                Forms.Application.Run(new FormsHost());
            }
        }
    }

    static class Log
    {
        static StreamWriter writer;
        static JavaScriptSerializer serializer = new JavaScriptSerializer();
        static string framework;
        public static string FilePath;
        public static void Init(string mode, string logDir)
        {
            Directory.CreateDirectory(logDir);
            framework = mode;
            FilePath = Path.Combine(logDir, "host-" + mode + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Process.GetCurrentProcess().Id + ".jsonl");
            writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            writer.AutoFlush = true;
            Write("host_start", new { processId = Process.GetCurrentProcess().Id, os = Environment.OSVersion.ToString(), clr = Environment.Version.ToString(), initialText = "", noNetwork = true });
        }
        public static void Write(string name, object data)
        {
            lock (serializer)
            {
                writer.WriteLine(serializer.Serialize(new { utc = DateTime.UtcNow.ToString("o"), framework = framework, eventName = name, data = data }));
            }
        }
    }

    static class Fixture
    {
        public const string Before = "前文｜台灣｜後文";
        public const string Selected = "台灣";
        public const string Replacement = "臺灣";
        public const int Start = 3;
        public const int Length = 2;
        public static bool Valid(string text, int start, int length)
        {
            return text == Before && start == Start && length == Length;
        }
        public const string Instructions = "空白測試欄位：請只輸入人工測試文字。保留目前的 Microsoft Bopomofo 與設定。\nF6：焦點留在欄位，立即記錄 IMM／文字／選取快照。\nF7：載入固定測試文並選取「台灣」。F8：預覽改成「臺灣」。F9：確認替換。\nF8/F9 只接受完全符合固定測試文與選取範圍的狀態。Enter 只換行。";
    }

    static class Imm
    {
        [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hwnd);
        [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr himc);
        [DllImport("imm32.dll", CharSet = CharSet.Unicode)] static extern int ImmGetCompositionStringW(IntPtr himc, uint index, byte[] buffer, uint length);
        [DllImport("imm32.dll", CharSet = CharSet.Unicode)] static extern uint ImmGetCandidateListW(IntPtr himc, uint index, IntPtr buffer, uint length);
        [DllImport("imm32.dll", CharSet = CharSet.Unicode)] static extern uint ImmGetCandidateListCountW(IntPtr himc, out uint count);
        [DllImport("imm32.dll")] static extern bool ImmGetOpenStatus(IntPtr himc);
        [DllImport("imm32.dll")] static extern bool ImmGetConversionStatus(IntPtr himc, out uint conversion, out uint sentence);
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint threadId);

        static object ReadString(IntPtr himc, uint flag)
        {
            int size = ImmGetCompositionStringW(himc, flag, null, 0);
            if (size <= 0) return new { byteCount = size, value = "" };
            if (size > 65536) return new { byteCount = size, value = "<oversized>" };
            byte[] bytes = new byte[size];
            int read = ImmGetCompositionStringW(himc, flag, bytes, (uint)bytes.Length);
            return new { byteCount = read, value = read > 0 ? Encoding.Unicode.GetString(bytes, 0, read - (read % 2)) : "" };
        }
        static object ReadBytes(IntPtr himc, uint flag)
        {
            int size = ImmGetCompositionStringW(himc, flag, null, 0);
            if (size <= 0 || size > 65536) return new { byteCount = size, base64 = "" };
            byte[] bytes = new byte[size];
            int read = ImmGetCompositionStringW(himc, flag, bytes, (uint)bytes.Length);
            return new { byteCount = read, base64 = read > 0 ? Convert.ToBase64String(bytes, 0, read) : "" };
        }
        static object Candidates(IntPtr himc, uint index)
        {
            uint size = ImmGetCandidateListW(himc, index, IntPtr.Zero, 0);
            if (size < 24 || size > 1048576) return new { listIndex = index, byteCount = size, candidates = new string[0] };
            IntPtr ptr = Marshal.AllocHGlobal((int)size);
            try
            {
                uint read = ImmGetCandidateListW(himc, index, ptr, size);
                uint count = (uint)Marshal.ReadInt32(ptr, 8);
                if (read < 24 || count > 4096 || 24 + count * 4 > read)
                    return new { listIndex = index, byteCount = read, error = "invalid_candidate_header" };
                List<string> values = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    uint offset = (uint)Marshal.ReadInt32(ptr, 24 + i * 4);
                    if (offset >= read || (offset % 2) != 0) { values.Add("<invalid-offset>"); continue; }
                    StringBuilder value = new StringBuilder();
                    for (uint p = offset; p + 1 < read; p += 2)
                    {
                        char c = (char)(ushort)Marshal.ReadInt16(ptr, (int)p);
                        if (c == 0) break;
                        value.Append(c);
                    }
                    values.Add(value.ToString());
                }
                return new { listIndex = index, byteCount = read, style = (uint)Marshal.ReadInt32(ptr, 4), count = count, selection = (uint)Marshal.ReadInt32(ptr, 12), pageStart = (uint)Marshal.ReadInt32(ptr, 16), pageSize = (uint)Marshal.ReadInt32(ptr, 20), candidates = values };
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        public static object Sample(IntPtr hwnd)
        {
            IntPtr himc = ImmGetContext(hwnd);
            if (himc == IntPtr.Zero) return new { hwnd = hwnd.ToInt64().ToString("X"), contextAvailable = false, keyboardLayout = GetKeyboardLayout(0).ToInt64().ToString("X") };
            try
            {
                uint count;
                uint allSize = ImmGetCandidateListCountW(himc, out count);
                uint conversion, sentence;
                bool conversionAvailable = ImmGetConversionStatus(himc, out conversion, out sentence);
                List<object> lists = new List<object>();
                for (uint i = 0; i < Math.Min(count, 8); i++) lists.Add(Candidates(himc, i));
                // Probe index 0 even when count is 0, recording its API return rather than inferring a candidate list.
                if (count == 0) lists.Add(Candidates(himc, 0));
                return new { hwnd = hwnd.ToInt64().ToString("X"), contextAvailable = true, keyboardLayout = GetKeyboardLayout(0).ToInt64().ToString("X"), open = ImmGetOpenStatus(himc), conversionAvailable = conversionAvailable, conversion = conversion, sentence = sentence, composition = ReadString(himc, 8), reading = ReadString(himc, 1), result = ReadString(himc, 0x800), resultReading = ReadString(himc, 0x200), attributes = ReadBytes(himc, 0x10), clauses = ReadBytes(himc, 0x20), cursor = ImmGetCompositionStringW(himc, 0x80, null, 0), deltaStart = ImmGetCompositionStringW(himc, 0x100, null, 0), candidateListCount = count, candidateTotalBytes = allSize, candidateLists = lists };
            }
            finally { ImmReleaseContext(hwnd, himc); }
        }
        public static bool IsImeMessage(int msg)
        {
            return msg == 0x10D || msg == 0x10E || msg == 0x10F || (msg >= 0x281 && msg <= 0x291);
        }
    }

    class ProbeTextBox : Forms.TextBox
    {
        public Action<int> Command;
        public void Snapshot(string reason)
        {
            Log.Write("snapshot", new { reason = reason, text = Text, selectionStart = SelectionStart, selectionLength = SelectionLength, selectedText = SelectedText, focused = Focused, imm = Imm.Sample(Handle) });
        }
        protected override void WndProc(ref Forms.Message m)
        {
            bool ime = Imm.IsImeMessage(m.Msg);
            int msg = m.Msg;
            long w = m.WParam.ToInt64(), l = m.LParam.ToInt64();
            if (ime) Log.Write("wm_ime_before", new { message = msg.ToString("X"), wParam = w, lParam = l, imm = Imm.Sample(Handle) });
            base.WndProc(ref m);
            if (ime)
            {
                Log.Write("wm_ime_after", new { message = msg.ToString("X"), wParam = w, lParam = l, imm = Imm.Sample(Handle) });
            }
        }
        protected override bool ProcessCmdKey(ref Forms.Message msg, Forms.Keys keyData)
        {
            if (keyData >= Forms.Keys.F6 && keyData <= Forms.Keys.F9 && Command != null)
            {
                Command((int)keyData - (int)Forms.Keys.F6 + 6);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    class FormsHost : Forms.Form
    {
        ProbeTextBox box = new ProbeTextBox();
        Forms.Label status = new Forms.Label();
        bool preview;
        public FormsHost()
        {
            Text = "Jevboard IME Probe - WinForms";
            Width = 920; Height = 560; StartPosition = Forms.FormStartPosition.Manual; Location = new Drawing.Point(80, 80);
            Font = new Drawing.Font("Microsoft JhengHei UI", 11);
            Forms.Label help = new Forms.Label { Text = Fixture.Instructions, AutoSize = false, Left = 20, Top = 15, Width = 860, Height = 120 };
            Controls.Add(help);
            box.Name = "ArtificialInput"; box.AccessibleName = "Artificial IME test input WinForms"; box.Left = 20; box.Top = 140; box.Width = 860; box.Height = 210; box.Multiline = true; box.AcceptsReturn = true; box.ScrollBars = Forms.ScrollBars.Vertical; box.Font = new Drawing.Font("Microsoft JhengHei UI", 22); box.ImeMode = Forms.ImeMode.NoControl;
            box.Command = DoCommand;
            box.TextChanged += delegate { preview = false; Log.Write("text_changed", new { text = box.Text, selectionStart = box.SelectionStart, selectionLength = box.SelectionLength }); };
            box.GotFocus += delegate { box.Snapshot("focus"); };
            Controls.Add(box);
            string[] labels = { "F6 快照", "F7 固定測試文", "F8 預覽", "F9 確認替換" };
            for (int i = 0; i < labels.Length; i++)
            {
                int key = i + 6;
                Forms.Button button = new Forms.Button { Text = labels[i], Left = 20 + i * 205, Top = 365, Width = 195, Height = 40 };
                button.Click += delegate { box.Focus(); DoCommand(key); };
                Controls.Add(button);
            }
            status.Left = 20; status.Top = 420; status.Width = 860; status.Height = 80; status.Text = "記錄：" + Log.FilePath;
            Controls.Add(status);
            Shown += delegate { box.Focus(); Log.Write("host_ready", new { title = Text, rootHwnd = Handle.ToInt64().ToString("X"), inputHwnd = box.Handle.ToInt64().ToString("X"), inputText = box.Text }); };
            FormClosed += delegate { Log.Write("host_closed", new { text = box.Text }); };
        }
        void DoCommand(int key)
        {
            if (key == 6) { box.Snapshot("F6"); status.Text = "F6 快照已記錄。焦點：" + box.Focused; return; }
            if (key == 7) { box.Text = Fixture.Before; box.Select(Fixture.Start, Fixture.Length); preview = false; box.Snapshot("fixture_loaded"); status.Text = "已選取台灣；F8 預覽，F9 確認。"; return; }
            bool valid = Fixture.Valid(box.Text, box.SelectionStart, box.SelectionLength);
            if (key == 8)
            {
                preview = valid;
                Log.Write("replacement_preview", new { accepted = valid, before = box.Text, selected = box.SelectedText, replacement = Fixture.Replacement, expectedResult = valid ? Fixture.Before.Substring(0, Fixture.Start) + Fixture.Replacement + Fixture.Before.Substring(Fixture.Start + Fixture.Length) : null });
                status.Text = valid ? "預覽：前文｜台灣｜後文 → 前文｜臺灣｜後文。F9 確認。" : "未預覽：請先按 F7，保留選取範圍。";
            }
            if (key == 9)
            {
                if (!preview || !valid) { Log.Write("replacement_blocked", new { preview = preview, fixtureMatches = valid }); status.Text = "未替換：需先 F7 再 F8。"; return; }
                string before = box.Text;
                box.SelectedText = Fixture.Replacement;
                Log.Write("replacement_applied", new { before = before, after = box.Text, expected = "前文｜臺灣｜後文", matches = box.Text == "前文｜臺灣｜後文", selectionStart = box.SelectionStart, selectionLength = box.SelectionLength });
                preview = false; status.Text = "已確認替換：" + box.Text;
            }
        }
    }

    class WpfHost : Wpf.Window
    {
        Controls.TextBox box = new Controls.TextBox();
        Controls.TextBlock status = new Controls.TextBlock();
        bool preview;
        IntPtr hwnd;
        public WpfHost()
        {
            Title = "Jevboard IME Probe - WPF"; Width = 920; Height = 560; Left = 140; Top = 140; FontFamily = new Wpf.Media.FontFamily("Microsoft JhengHei UI"); FontSize = 15;
            Controls.StackPanel panel = new Controls.StackPanel { Margin = new Wpf.Thickness(20) };
            panel.Children.Add(new Controls.TextBlock { Text = Fixture.Instructions, TextWrapping = Wpf.TextWrapping.Wrap, Margin = new Wpf.Thickness(0, 0, 0, 15) });
            box.Name = "ArtificialInput"; Wpf.Automation.AutomationProperties.SetName(box, "Artificial IME test input WPF"); box.Height = 210; box.FontSize = 29; box.AcceptsReturn = true; box.TextWrapping = Wpf.TextWrapping.Wrap; box.VerticalScrollBarVisibility = Controls.ScrollBarVisibility.Auto;
            box.TextChanged += delegate { preview = false; Log.Write("text_changed", new { text = box.Text, selectionStart = box.SelectionStart, selectionLength = box.SelectionLength }); };
            box.SelectionChanged += delegate { Log.Write("selection_changed", new { selectionStart = box.SelectionStart, selectionLength = box.SelectionLength, caretIndex = box.CaretIndex }); };
            box.PreviewKeyDown += delegate(object sender, Input.KeyEventArgs e)
            {
                if (e.Key >= Input.Key.F6 && e.Key <= Input.Key.F9) { DoCommand((int)e.Key - (int)Input.Key.F6 + 6); e.Handled = true; }
            };
            Input.TextCompositionManager.AddPreviewTextInputStartHandler(box, delegate(object sender, Input.TextCompositionEventArgs e) { Composition("composition_start", e); });
            Input.TextCompositionManager.AddPreviewTextInputUpdateHandler(box, delegate(object sender, Input.TextCompositionEventArgs e) { Composition("composition_update", e); });
            box.PreviewTextInput += delegate(object sender, Input.TextCompositionEventArgs e) { Composition("text_input", e); };
            panel.Children.Add(box);
            Controls.StackPanel buttons = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal, Margin = new Wpf.Thickness(0, 15, 0, 15) };
            string[] labels = { "F6 快照", "F7 固定測試文", "F8 預覽", "F9 確認替換" };
            for (int i = 0; i < labels.Length; i++)
            {
                int key = i + 6;
                Controls.Button button = new Controls.Button { Content = labels[i], Width = 195, Height = 40, Margin = new Wpf.Thickness(0, 0, 10, 0) };
                button.Click += delegate { box.Focus(); DoCommand(key); };
                buttons.Children.Add(button);
            }
            panel.Children.Add(buttons);
            status.Text = "記錄：" + Log.FilePath; status.TextWrapping = Wpf.TextWrapping.Wrap; panel.Children.Add(status); Content = panel;
            SourceInitialized += delegate { hwnd = new Interop.WindowInteropHelper(this).Handle; Interop.HwndSource.FromHwnd(hwnd).AddHook(Hook); };
            Loaded += delegate { box.Focus(); Log.Write("host_ready", new { title = Title, rootHwnd = hwnd.ToInt64().ToString("X"), inputHwnd = "WPF TextBox has no child HWND", inputText = box.Text }); };
            Closed += delegate { Log.Write("host_closed", new { text = box.Text }); };
        }
        IntPtr Hook(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
        {
            if (Imm.IsImeMessage(msg)) Log.Write("wm_ime", new { message = msg.ToString("X"), wParam = wp.ToInt64(), lParam = lp.ToInt64(), imm = Imm.Sample(h) });
            return IntPtr.Zero;
        }
        void Composition(string name, Input.TextCompositionEventArgs e)
        {
            Log.Write(name, new { text = e.Text, compositionText = e.TextComposition.CompositionText, systemCompositionText = e.TextComposition.SystemCompositionText, textBoxValue = box.Text, selectionStart = box.SelectionStart, selectionLength = box.SelectionLength, caretIndex = box.CaretIndex, imm = Imm.Sample(hwnd) });
        }
        void Snapshot(string reason)
        {
            Log.Write("snapshot", new { reason = reason, text = box.Text, selectionStart = box.SelectionStart, selectionLength = box.SelectionLength, selectedText = box.SelectedText, caretIndex = box.CaretIndex, focused = box.IsKeyboardFocused, imm = Imm.Sample(hwnd) });
        }
        void DoCommand(int key)
        {
            if (key == 6) { Snapshot("F6"); status.Text = "F6 快照已記錄。焦點：" + box.IsKeyboardFocused; return; }
            if (key == 7) { box.Text = Fixture.Before; box.Select(Fixture.Start, Fixture.Length); preview = false; Snapshot("fixture_loaded"); status.Text = "已選取台灣；F8 預覽，F9 確認。"; return; }
            bool valid = Fixture.Valid(box.Text, box.SelectionStart, box.SelectionLength);
            if (key == 8)
            {
                preview = valid;
                Log.Write("replacement_preview", new { accepted = valid, before = box.Text, selected = box.SelectedText, replacement = Fixture.Replacement, expectedResult = valid ? Fixture.Before.Substring(0, Fixture.Start) + Fixture.Replacement + Fixture.Before.Substring(Fixture.Start + Fixture.Length) : null });
                status.Text = valid ? "預覽：前文｜台灣｜後文 → 前文｜臺灣｜後文。F9 確認。" : "未預覽：請先按 F7，保留選取範圍。";
            }
            if (key == 9)
            {
                if (!preview || !valid) { Log.Write("replacement_blocked", new { preview = preview, fixtureMatches = valid }); status.Text = "未替換：需先 F7 再 F8。"; return; }
                string before = box.Text;
                box.SelectedText = Fixture.Replacement;
                Log.Write("replacement_applied", new { before = before, after = box.Text, expected = "前文｜臺灣｜後文", matches = box.Text == "前文｜臺灣｜後文", selectionStart = box.SelectionStart, selectionLength = box.SelectionLength });
                preview = false; status.Text = "已確認替換：" + box.Text;
            }
        }
    }
}
