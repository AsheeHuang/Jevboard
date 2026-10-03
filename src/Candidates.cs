using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Jevboard
{
    // One character offset of the composition and the IME's first candidate page there (words starting at that offset).
    class Position
    {
        public int Offset;
        public List<string> Items;
        public Native.RECT Rect;
    }

    class Change
    {
        public int Offset;          // character offset in the composition
        public string Text;         // IME candidate text to select there (may be a multi-character word)
        public string Replaced;     // what the reconstructed current sentence has there
        public double Probability;
        public bool Included = true;   // applied by default; optional alternatives start switched off
        public int End { get { return Offset + Text.Length; } }
        public static bool Overlaps(Change a, Change b) { return a.Offset < b.End && b.Offset < a.End; }
    }

    class Snapshot
    {
        public int Generation;
        public IntPtr Foreground, Focus, Popup;
        public Native.RECT PopupRect;
        public List<Position> Positions = new List<Position>();
        public string Current = "";   // the composition: exact when read from the editor's text, else first candidates
        public bool Exact;            // Current came from the editor's TextPattern, so pasting it back is safe
        public string Context = "";
    }

    static class Candidates
    {
        // The Microsoft Bopomofo candidate popup is a CoreWindow of Windows' TextInputHost.exe living in the
        // immersive IME z-band: EnumWindows/EnumChildWindows/EnumThreadWindows/UIA root never list it. Two routes
        // do reach it: UIA raises StructureChanged(ChildAdded) on that CoreWindow element every time the list is
        // (re)built, carrying its HWND; and WindowFromPoint hit-testing near the caret, for apps that have one.
        static IntPtr seenPopup;
        static readonly Dictionary<int, bool> hostPids = new Dictionary<int, bool>();
        static IUIAutomation uia;            // kept alive with the handler for the lifetime of the subscription
        static PopupWatcher watcher;

        // Subscribed from an MTA thread so callbacks arrive on UIA's own threads, never through the UI message pump.
        public static void Watch()
        {
            Thread t = new Thread(delegate()
            {
                try
                {
                    uia = (IUIAutomation)new CUIAutomation();
                    IUIAutomationCacheRequest cache = uia.CreateCacheRequest();
                    cache.AddProperty(Uia.ProcessIdProperty); cache.AddProperty(Uia.ClassNameProperty); cache.AddProperty(Uia.NativeWindowHandleProperty);
                    watcher = new PopupWatcher();
                    uia.AddStructureChangedEventHandler(uia.GetRootElement(), Uia.TreeScopeSubtree, cache, watcher);
                    Log.Write("popup watcher subscribed");
                }
                catch (Exception ex) { Log.Write("popup watcher failed: " + ex.GetType().Name + " " + ex.Message); }
            });
            t.IsBackground = true; t.SetApartmentState(ApartmentState.MTA); t.Start();
        }

        [ComVisible(true)]
        class PopupWatcher : IUIAutomationStructureChangedEventHandler
        {
            public void HandleStructureChangedEvent(IUIAutomationElement sender, int changeType, int[] runtimeId)
            {
                try
                {
                    if (changeType != Uia.ChildAdded || sender == null) return;
                    object hwndValue = sender.GetCachedPropertyValue(Uia.NativeWindowHandleProperty);
                    int hwnd = hwndValue is int ? (int)hwndValue : 0;
                    if (hwnd == 0) return;
                    if (!(sender.GetCachedPropertyValue(Uia.ClassNameProperty) as string == "Windows.UI.Core.CoreWindow")) return;
                    object pidValue = sender.GetCachedPropertyValue(Uia.ProcessIdProperty);
                    if (!(pidValue is int) || !IsTextInputHost((int)pidValue)) return;
                    IntPtr h = new IntPtr(hwnd);
                    if (h != seenPopup) { seenPopup = h; Log.Write("candidate popup window seen via UIA: 0x" + hwnd.ToString("X")); }
                }
                catch (Exception ex) { Log.Write("popup watch error: " + ex.GetType().Name); }
            }
        }

        static bool IsTextInputHost(int pid)
        {
            lock (hostPids)
            {
                bool ok;
                if (hostPids.TryGetValue(pid, out ok)) return ok;
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";
                try
                {
                    string image = Process.GetProcessById(pid).MainModule.FileName;
                    ok = image.StartsWith(windows, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(image).Equals("TextInputHost.exe", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception) { ok = false; }
                if (hostPids.Count > 64) hostPids.Clear();
                hostPids[pid] = ok;
                return ok;
            }
        }

        public static IntPtr FindPopup(Native.RECT caretScreen)
        {
            IntPtr known = seenPopup;
            if (known != IntPtr.Zero && Native.IsWindowVisible(known) && CandidateList(known) != null) return known;
            if (caretScreen.right <= caretScreen.left && caretScreen.bottom <= caretScreen.top) return IntPtr.Zero;
            int[] dx = { 12, 40, 80, -20 };
            int[] dyBelow = { 12, 40, 80, 140 };
            int[] dyAbove = { 20, 60, 120, 200 };
            HashSet<long> seen = new HashSet<long>();
            foreach (int x in dx)
            {
                foreach (int y in dyBelow) { IntPtr p = Probe(caretScreen.left + x, caretScreen.bottom + y, seen); if (p != IntPtr.Zero) return p; }
                foreach (int y in dyAbove) { IntPtr p = Probe(caretScreen.left + x, caretScreen.top - y, seen); if (p != IntPtr.Zero) return p; }
            }
            return IntPtr.Zero;
        }

        static IntPtr Probe(int x, int y, HashSet<long> seen)
        {
            IntPtr hwnd = Native.WindowFromPoint(new Native.POINT { x = x, y = y });
            if (hwnd == IntPtr.Zero || !seen.Add(hwnd.ToInt64())) return IntPtr.Zero;
            if (!Native.IsWindowVisible(hwnd) || Native.ClassName(hwnd) != "Windows.UI.Core.CoreWindow") return IntPtr.Zero;
            uint pid; Native.GetWindowThreadProcessId(hwnd, out pid);
            if (!IsTextInputHost((int)pid)) return IntPtr.Zero;
            return CandidateList(hwnd) == null ? IntPtr.Zero : hwnd;
        }

        // One server-side search finds the candidate panel; one cached FindAll fetches every row's name, visibility and
        // bounds in a single cross-process call instead of three calls per row.
        static readonly Condition PanelCondition = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List), new PropertyCondition(AutomationElement.AutomationIdProperty, "TEMPLATE_PART_CandidatePanel"));
        static readonly Condition RowCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
        static readonly CacheRequest RowCache = BuildRowCache();
        static CacheRequest BuildRowCache()
        {
            CacheRequest cache = new CacheRequest();
            cache.Add(AutomationElement.NameProperty); cache.Add(AutomationElement.IsOffscreenProperty); cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.TreeScope = TreeScope.Element;
            return cache;
        }

        static AutomationElement CandidateList(IntPtr popup)
        {
            try { return AutomationElement.FromHandle(popup).FindFirst(TreeScope.Descendants, PanelCondition); }
            catch (Exception ex) { Log.Write("uia read failed: " + ex.GetType().Name); return null; }
        }

        static bool IsOpen(IntPtr popup) { return CandidateList(popup) != null; }

        // Visible page in visual order (what the IME's digit keys refer to); null when the list is closed.
        public static List<string> ReadPage(IntPtr popup)
        {
            AutomationElement list = CandidateList(popup);
            if (list == null) return null;
            AutomationElementCollection rows;
            try { using (RowCache.Activate()) rows = list.FindAll(TreeScope.Children, RowCondition); }
            catch (Exception ex) { Log.Write("uia rows failed: " + ex.GetType().Name); return null; }
            List<KeyValuePair<double, string>> rowsByTop = new List<KeyValuePair<double, string>>();
            for (int i = 0; i < rows.Count; i++)
            {
                AutomationElement.AutomationElementInformation c = rows[i].Cached;
                if (c.IsOffscreen || string.IsNullOrEmpty(c.Name)) continue;
                rowsByTop.Add(new KeyValuePair<double, string>(c.BoundingRectangle.Top, c.Name));
            }
            rowsByTop.Sort(delegate(KeyValuePair<double, string> x, KeyValuePair<double, string> y) { return x.Key.CompareTo(y.Key); });
            List<string> items = new List<string>();
            foreach (KeyValuePair<double, string> row in rowsByTop) items.Add(row.Value);
            return items;
        }

        static List<string> WaitForPage(IntPtr popup, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                List<string> page = ReadPage(popup);
                if (page != null && page.Count > 0) return page;
                if (sw.ElapsedMilliseconds >= timeoutMs) return null;
                Thread.Sleep(10);
            }
        }

        static bool WaitForClosed(IntPtr popup, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                if (!IsOpen(popup)) return true;
                if (sw.ElapsedMilliseconds >= timeoutMs) return false;
                Thread.Sleep(10);
            }
        }

        // Esc is only safe while the list is open: with no list, Esc cancels the whole composition.
        public static bool CloseList(IntPtr popup)
        {
            if (!IsOpen(popup)) return true;
            Native.Tap(Native.VK_ESCAPE);
            return WaitForClosed(popup, 600);
        }

        static bool Same(Position a, List<string> items, Native.RECT rect)
        {
            if (a.Rect.left != rect.left || a.Rect.top != rect.top || a.Items.Count != items.Count) return false;
            for (int i = 0; i < items.Count; i++) if (a.Items[i] != items[i]) return false;
            return true;
        }

        // A composition position that opens no candidate list: a space, punctuation or an English letter. Its real
        // character is not readable from outside, so an ideographic space stands in for it; it still counts as one
        // character for Left/Right.
        public const string UnknownChar = "　";
        public static Position Unknown(int offset) { return new Position { Offset = offset, Items = new List<string> { UnknownChar } }; }
        const int ListTimeout = 120;   // the list normally appears within ~40ms; this is the pause paid at every unreadable position

        // Walks the composition with the IME's own keys (Home, Down, Esc, Right, End, Left) and reads the first
        // candidate page at every character. The list must be open on entry: that is the proof a composition exists.
        // Forward from the start, a position that opens no list is ambiguous (unreadable character or the end), so
        // the walk then jumps to End and comes back with Left until it meets the last readable position again: the
        // number of Left presses gives the exact length, and every character in between is read on the way.
        // Runs on a worker thread. Returns null on any doubt; never sends Esc without an open list.
        public static List<Position> Harvest(IntPtr popup, Func<bool> aborted)
        {
            List<Position> positions = new List<Position>();
            if (!CloseList(popup)) return null;
            Native.Tap(Native.VK_HOME);
            Position lastList = null;
            int leading = 0;
            for (int k = 0; k < 60; k++)
            {
                if (aborted()) return null;
                Native.Tap(Native.VK_DOWN);
                List<string> page = WaitForPage(popup, ListTimeout);
                if (page != null)
                {
                    Native.RECT rect; Native.GetWindowRect(popup, out rect);
                    bool atEnd = lastList != null && Same(lastList, page, rect);   // Right no longer moved
                    if (!atEnd) { lastList = new Position { Offset = k, Items = page, Rect = rect }; positions.Add(lastList); }
                    if (!CloseList(popup)) { Log.Write("harvest: list did not close at char " + k); return null; }
                    if (atEnd) return positions;
                }
                else if (lastList == null)
                {
                    if (++leading > 8) { Log.Write("harvest: nothing readable at the start"); return null; }
                    positions.Add(Unknown(k));
                }
                else break;   // ambiguous: resolve from the end
                Native.Tap(Native.VK_RIGHT);
            }
            if (lastList == null) return null;
            Native.Tap(Native.VK_END);
            List<KeyValuePair<int, Position>> tail = new List<KeyValuePair<int, Position>>();   // Left presses -> page read there
            for (int m = 1; m <= 60; m++)
            {
                if (aborted()) return null;
                Native.Tap(Native.VK_LEFT);
                Native.Tap(Native.VK_DOWN);
                List<string> page = WaitForPage(popup, ListTimeout);
                if (page == null) { tail.Add(new KeyValuePair<int, Position>(m, null)); continue; }
                Native.RECT rect; Native.GetWindowRect(popup, out rect);
                if (!CloseList(popup)) { Log.Write("harvest: list did not close on the way back"); return null; }
                if (rect.left == lastList.Rect.left && rect.top == lastList.Rect.top)
                {
                    List<Position> all = MergeTail(positions, lastList, tail);
                    Native.Tap(Native.VK_END);
                    return all;
                }
                tail.Add(new KeyValuePair<int, Position>(m, new Position { Items = page, Rect = rect }));
            }
            Log.Write("harvest: never met the last readable position on the way back");
            return null;
        }

        // The anchor was met after `tail.Count + 1` Left presses from the end, so the composition has
        // anchor.Offset + tail.Count + 1 characters; each tail entry m sits at offset length - m.
        public static List<Position> MergeTail(List<Position> forward, Position anchor, List<KeyValuePair<int, Position>> tail)
        {
            int length = anchor.Offset + tail.Count + 1;
            List<Position> all = new List<Position>(forward);
            foreach (KeyValuePair<int, Position> t in tail)
            {
                int offset = length - t.Key;
                if (t.Value == null) all.Add(Unknown(offset));
                else { t.Value.Offset = offset; all.Add(t.Value); }
            }
            all.Sort(delegate(Position a, Position b) { return a.Offset.CompareTo(b.Offset); });
            return all;
        }

        // An open list is the proof that a composition exists. If none is open, press Down (the IME's own key) at
        // the cursor; when the cursor sits after an unreadable character (English letters, punctuation) Down opens
        // nothing there, so try again from the first character with Home. Without a composition these keys merely
        // move the caret (End puts it back at the line end), the accepted cost of not asking the user to press them.
        // Runs on a worker thread.
        public static IntPtr OpenList(Native.RECT caretScreen, Func<bool> aborted)
        {
            IntPtr popup = FindPopup(caretScreen);
            if (popup != IntPtr.Zero) return popup;
            Native.Tap(Native.VK_DOWN);
            popup = WaitForPopup(caretScreen, aborted);
            if (popup != IntPtr.Zero) { Log.Write("opened the candidate list with Down"); return popup; }
            Native.Tap(Native.VK_HOME);
            Native.Tap(Native.VK_DOWN);
            popup = WaitForPopup(caretScreen, aborted);
            if (popup != IntPtr.Zero) { Log.Write("opened the candidate list with Home, Down"); return popup; }
            Native.Tap(Native.VK_END);
            return IntPtr.Zero;
        }

        static IntPtr WaitForPopup(Native.RECT caretScreen, Func<bool> aborted)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 600 && !aborted())
            {
                IntPtr popup = FindPopup(caretScreen);
                if (popup != IntPtr.Zero) return popup;
                Thread.Sleep(15);
            }
            return IntPtr.Zero;
        }

        // Fallback reconstruction: the first candidate at every position. The IME orders candidates by frequency and
        // learning while the sentence conversion uses context, so the first candidate is not always the converted
        // character (ㄧㄠˋ listed 藥 first while the sentence read 要). Preferred: ReadBeforeCaret + ResolveCurrent.
        public static string CurrentText(List<Position> positions)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Position p in positions) sb.Append(p.Items[0].Substring(0, 1));
            return sb.ToString();
        }

        // TSF-aware editors that expose UIA TextPattern (Chromium, Electron, WPF) include the uncommitted composition
        // in their text, and the caret sits at its end once the walk finished with End. Returns the text before the
        // caret, or null when the focused element does not offer it (native EDIT, for one). Memory-only, never logged.
        public static string ReadBeforeCaret(IntPtr focus)
        {
            try
            {
                uint pid; Native.GetWindowThreadProcessId(focus, out pid);
                AutomationElement el = AutomationElement.FocusedElement;
                if (el == null || el.Current.ProcessId != (int)pid) return null;
                object pattern;
                if (!el.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return null;
                TextPattern tp = (TextPattern)pattern;
                TextPatternRange[] selection = tp.GetSelection();
                if (selection == null || selection.Length == 0) return null;
                TextPatternRange prefix = tp.DocumentRange.Clone();
                prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.Start);
                return prefix.GetText(-1);
            }
            catch (Exception ex) { Log.Write("text pattern read failed: " + ex.GetType().Name); return null; }
        }

        // The last positions.Count characters before the caret are the composition, provided every readable position's
        // character is the first character of one of its candidates (anything else means the text does not contain
        // the composition, e.g. an editor that exposes committed text only). Returns the composition or null; `context`
        // is the committed text in front of it.
        public static string ResolveCurrent(List<Position> positions, string beforeCaret, out string context)
        {
            context = "";
            int length = positions.Count;
            if (beforeCaret == null || beforeCaret.Length < length || length == 0) return null;
            string tail = beforeCaret.Substring(beforeCaret.Length - length);
            foreach (Position p in positions)
            {
                if (p.Items.Count == 1 && p.Items[0] == UnknownChar) continue;
                bool found = false;
                foreach (string item in p.Items) if (item.Length > 0 && item[0] == tail[p.Offset]) { found = true; break; }
                if (!found) return null;
            }
            context = beforeCaret.Substring(0, beforeCaret.Length - length);
            return tail;
        }

        // Selects each change with the IME's digit key after re-opening that position's list and locating the text
        // on the live page (the IME re-ranks after every pick, so stored indexes are never reused).
        // Returns null on success, else a user-facing reason. Runs on a worker thread.
        public static string Apply(IntPtr popup, List<Change> changes, Func<bool> aborted)
        {
            foreach (Change c in changes)
            {
                if (aborted()) return "已取消";
                Native.Tap(Native.VK_HOME);
                for (int i = 0; i < c.Offset; i++) Native.Tap(Native.VK_RIGHT);
                Native.Tap(Native.VK_DOWN);
                List<string> page = WaitForPage(popup, 500);
                if (page == null) return "第 " + (c.Offset + 1) + " 字的候選沒有打開，已停止";
                int idx = page.IndexOf(c.Text);
                if (idx < 0 || idx > 8) { CloseList(popup); return "第 " + (c.Offset + 1) + " 字的候選已變動，未套用「" + c.Text + "」"; }
                Native.Tap((ushort)('1' + idx));
                if (!WaitForClosed(popup, 500)) return "選字後候選未關閉，已停止";
            }
            Native.Tap(Native.VK_END);
            return null;
        }

        // Text before the caret in an EDIT/RichEdit-class window; empty elsewhere (WM_GETTEXT would return a title).
        // Memory-only, never logged.
        public static string ReadContext(IntPtr edit, int maxChars)
        {
            if (Native.ClassName(edit).IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) < 0) return "";
            long length = Native.Send(edit, Native.WM_GETTEXTLENGTH, 0, 0);
            if (length <= 0 || length > 65536) return "";
            StringBuilder buffer = new StringBuilder((int)length + 1);
            IntPtr result;
            if (Native.SendMessageTimeoutW(edit, Native.WM_GETTEXT, new IntPtr(buffer.Capacity), buffer, 2, 100, out result) == IntPtr.Zero) return "";
            string text = buffer.ToString();
            long sel = Native.Send(edit, Native.EM_GETSEL, 0, 0);
            int caret = sel < 0 ? text.Length : Math.Min(text.Length, (int)(sel & 0xFFFF));
            int start = Math.Max(0, caret - maxChars);
            return text.Substring(start, caret - start);
        }
    }
}
