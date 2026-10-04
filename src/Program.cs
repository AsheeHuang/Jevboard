using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Jevboard
{
    static class Log
    {
        static readonly string path = Path.Combine(Settings.Dir, "jevboard.log");
        public static void Write(string line)
        {
            // Never log the API key, candidate text, sentences or editor content.
            try { Directory.CreateDirectory(Settings.Dir); File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff ") + line + "\r\n", Encoding.UTF8); } catch { }
        }
    }

    // Key is DPAPI-encrypted for the current Windows user; enabled flag is a plain file.
    static class Settings
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jevboard");
        static readonly string keyFile = Path.Combine(Dir, "key.bin"), enabledFile = Path.Combine(Dir, "enabled");
        public static bool Enabled { get { return File.Exists(enabledFile); } set { Directory.CreateDirectory(Dir); if (value) File.WriteAllText(enabledFile, "1"); else File.Delete(enabledFile); } }
        public static bool HasKey { get { return File.Exists(keyFile); } }
        public static string LoadKey()
        {
            try { return HasKey ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(keyFile), null, DataProtectionScope.CurrentUser)) : null; }
            catch (Exception ex) { Log.Write("key unreadable: " + ex.GetType().Name); return null; }
        }
        public static void SaveKey(string key)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(keyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
        }
    }

    class SettingsForm : Form
    {
        public SettingsForm()
        {
            Text = "Jevboard 設定"; Width = 420; Height = 190; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft JhengHei UI", 10);
            Label label = new Label { Left = 16, Top = 18, Width = 370, Text = Settings.HasKey ? "Jev API key（已儲存；輸入新值可覆蓋）" : "Jev API key（尚未設定）" };
            TextBox key = new TextBox { Left = 16, Top = 44, Width = 370, UseSystemPasswordChar = true };
            Button save = new Button { Left = 286, Top = 76, Width = 100, Text = "儲存" };
            save.Click += delegate
            {
                // A newly saved key means the user wants Jevboard on; pausing lives in the tray menu.
                if (key.Text.Trim().Length > 0) { Settings.SaveKey(key.Text); Settings.Enabled = true; key.Text = ""; label.Text = "Jev API key（已儲存；輸入新值可覆蓋）"; Log.Write("key saved, enabled"); }
                Close();
            };
            Controls.AddRange(new Control[] { label, key, save });
        }
    }

    class App : ApplicationContext
    {
        // A different candidate must reach ProposeThreshold and get at least ProposeMargin times the probability of keeping
        // the sentence; anything closer is noise (觀察到的誤判多在 0.4–0.55 對 0.4–0.5 之間，正確修正多在 0.6 以上).
        internal const double ProposeThreshold = 0.5, ProposeMargin = 2.0;
        NotifyIcon tray = new NotifyIcon();
        MenuItem enableItem;
        Hotkey hotkey;
        Overlay overlay = new Overlay();
        System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer();
        Snapshot current;
        int generation;
        bool busy;   // a worker thread is driving the IME (harvest or apply)

        public App()
        {
            overlay.ApplyClicked = ApplyProposal;
            overlay.CancelClicked = delegate { Cancel("cancel clicked"); };
            overlay.CreateControl(); IntPtr handle = overlay.Handle;   // BeginInvoke needs a live handle before the first show
            watch.Interval = 250; watch.Tick += delegate { if (current != null && !TargetUnchanged(current)) Cancel("target window or focus changed"); };
            enableItem = new MenuItem("啟用", delegate { Settings.Enabled = !Settings.Enabled; enableItem.Checked = Settings.Enabled; }) { Checked = Settings.Enabled };
            tray.ContextMenu = new ContextMenu(new[] { enableItem, new MenuItem("設定…", delegate { OpenSettings(); }), new MenuItem("結束", delegate { ExitThread(); }) });
            tray.Icon = SystemIcons.Application; tray.Text = "Jevboard"; tray.Visible = true;
            tray.DoubleClick += delegate { OpenSettings(); };
            // Leave the hook callback before touching UIA: cross-process reads inside a low-level hook stall.
            hotkey = new Hotkey { NotApplicable = NotApplicable, DoubleTap = delegate { overlay.BeginInvoke(new Action(Trigger)); }, OverlayShowing = delegate { return overlay.Visible; }, KeyWhileOverlay = KeyWhileOverlay };
            Candidates.Watch();
            Log.Write("started enabled=" + Settings.Enabled + " hasKey=" + Settings.HasKey);
        }

        void OpenSettings()
        {
            if (overlay.Visible) return;           // never interfere with an in-progress selection
            using (SettingsForm f = new SettingsForm()) f.ShowDialog();
            enableItem.Checked = Settings.Enabled;
        }

        // Null when enabled, another process has a focused window, and Microsoft Bopomofo is open there in Chinese
        // mode; otherwise a short reason for the log (class names only, never content). English mode passes Caps Lock
        // through undelayed, at the cost of not triggering on a composition that ends in English.
        string NotApplicable()
        {
            if (!Settings.Enabled) return "disabled";
            IntPtr fg = Native.GetForegroundWindow();
            uint pid; uint thread = Native.GetWindowThreadProcessId(fg, out pid);
            if (fg == IntPtr.Zero) return "no foreground window";
            if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return "Jevboard itself is foreground";
            Native.RECT caret; IntPtr caretWindow;
            IntPtr focus = Native.FocusOf(fg, out caret, out caretWindow);
            if (focus == IntPtr.Zero) return "no focused window in " + Native.ClassName(fg);
            string cls = Native.ClassName(focus);
            long layout = Native.GetKeyboardLayout(thread).ToInt64() & 0xFFFF;
            if (layout != 0x0404) return "keyboard layout 0x" + layout.ToString("X4") + " in " + cls;
            IntPtr ime = Native.ImmGetDefaultIMEWnd(focus);
            if (ime == IntPtr.Zero) return "no IME window for " + cls;
            if (Native.Send(ime, Native.WM_IME_CONTROL, Native.IMC_GETOPENSTATUS, 0) == 0) return "IME closed in " + cls;
            if (!InChineseMode(focus)) return "IME in English mode in " + cls;
            return null;
        }

        static bool InChineseMode(IntPtr focus)
        {
            IntPtr ime = Native.ImmGetDefaultIMEWnd(focus);
            return ime != IntPtr.Zero && (Native.Send(ime, Native.WM_IME_CONTROL, Native.IMC_GETCONVERSIONMODE, 0) & Native.IME_CMODE_NATIVE) != 0;
        }

        static bool TargetUnchanged(Snapshot s)
        {
            if (Native.GetForegroundWindow() != s.Foreground) return false;
            Native.RECT caret; IntPtr caretWindow;
            return Native.FocusOf(s.Foreground, out caret, out caretWindow) == s.Focus;
        }

        // Double-tap: the candidate list must already be open (proof of a composition); then read the whole
        // composition position by position on a worker thread, ask Jev once, and show the proposal.
        void Trigger()
        {
            if (busy) { Log.Write("trigger ignored: busy"); return; }
            Cancel(null);
            int gen = ++generation;
            IntPtr fg = Native.GetForegroundWindow();
            Native.RECT caret; IntPtr caretWindow;
            IntPtr focus = Native.FocusOf(fg, out caret, out caretWindow);
            Native.RECT caretScreen = ToScreen(caretWindow, caret);
            Point anchor = caretWindow != IntPtr.Zero ? new Point(caretScreen.left, caretScreen.bottom + 4) : Cursor.Position;
            Snapshot s = new Snapshot { Generation = gen, Foreground = fg, Focus = focus };
            s.Context = Candidates.ReadContext(focus, 40);
            current = s; watch.Start(); busy = true;
            overlay.ShowStatus("讀取整句候選中…（請勿操作鍵盤）", anchor);
            Log.Write("gen " + gen + " harvest start");
            Worker(delegate
            {
                Func<bool> aborted = delegate { return gen != generation; };
                // The mode was checked at the first tap; Shift between the two taps can still switch it.
                if (!InChineseMode(focus)) { overlay.BeginInvoke(new Action(delegate { busy = false; if (gen == generation) Fail("輸入法在英數模式，請先切回中文再雙擊"); })); return; }
                IntPtr popup = Candidates.OpenList(caretScreen, aborted);
                List<Position> positions = popup == IntPtr.Zero ? null : Candidates.Harvest(popup, aborted);
                overlay.BeginInvoke(new Action(delegate { busy = false; OnHarvested(gen, popup, positions); }));
            });
        }

        void OnHarvested(int gen, IntPtr popup, List<Position> positions)
        {
            if (gen != generation || current == null) return;
            if (popup == IntPtr.Zero) { Log.Write("gen " + gen + " no candidate list after Down: no composition"); Fail("沒有偵測到注音組字；請先打字再雙擊 Caps Lock"); return; }
            if (positions == null) { Log.Write("gen " + gen + " harvest failed"); Fail("讀不到整句候選，請再試一次"); return; }
            current.Popup = popup; current.PopupRect = positions[0].Rect;
            current.Positions = positions;
            string committed;
            string resolved = Candidates.ResolveCurrent(positions, Candidates.ReadBeforeCaret(current.Focus), out committed);
            current.Current = resolved ?? Candidates.CurrentText(positions);
            current.Exact = resolved != null;
            current.Context = Candidates.ContextFor(current.Context, resolved != null ? committed : null, positions, 40);
            Log.Write("gen " + gen + " harvested chars=" + positions.Count + " current=" + (resolved != null ? "text pattern" : "first candidates") + " context=" + current.Context.Length);
            overlay.MoveTo(new Point(current.PopupRect.left, current.PopupRect.top));
            overlay.ShowSentence(current.Current, "Jev 分析整句中…");
            string key = Settings.LoadKey();
            if (key == null) { overlay.SetStatus("未設定 Jev API key（托盤 → 設定）；按 Esc 關閉"); return; }
            Snapshot s = current;
            Worker(delegate
            {
                string error = null;
                List<Change> changes = Refine(s, delegate(string state, List<JevClient.Question> questions)
                {
                    if (gen != generation) return null;
                    Dictionary<string, JevClient.Answer> answers = JevClient.AskSync(key, state, questions, 8000, out error);
                    if (answers != null) Log.Write("gen " + gen + " jev answered questions=" + answers.Count + " |" + Describe(answers));
                    return answers;
                }, MaxRounds);
                overlay.BeginInvoke(new Action(delegate { OnProposal(gen, changes, error); }));
            });
        }

        // Candidate ids and probabilities only, never text.
        static string Describe(Dictionary<string, JevClient.Answer> answers)
        {
            StringBuilder detail = new StringBuilder();
            foreach (KeyValuePair<string, JevClient.Answer> kv in answers)
            {
                double p; kv.Value.Probabilities.TryGetValue(kv.Value.Choice ?? "", out p);
                detail.Append(" " + kv.Key + ":" + (kv.Value.Ok ? kv.Value.Choice + "=" + p.ToString("0.00") : "invalid"));
            }
            return detail.ToString();
        }

        void OnProposal(int gen, List<Change> changes, string error)
        {
            if (gen != generation || current == null || !overlay.Visible) { Log.Write("gen " + gen + " late/stale response dropped"); return; }
            if (changes == null) { Log.Write("gen " + gen + " jev failed: " + error); overlay.SetStatus("AI 暫不可用（" + error + "）；按 Esc 關閉"); return; }
            int on = IncludedCount(changes);
            Log.Write("gen " + gen + " proposal changes=" + on + " optional=" + (changes.Count - on));
            if (changes.Count == 0) { overlay.ShowSentence(current.Current, "Jev 沒有建議修改"); overlay.AutoHide(); return; }
            overlay.ShowProposal(on == 0 ? "Jev 沒有把握的替代字（預設不改）" : "Jev 建議修改 " + on + " 處" + (changes.Count > on ? "，另有 " + (changes.Count - on) + " 個可選" : ""), changes);
        }

        internal const int MaxRounds = 3;

        // Rounds of questions: each round's accepted changes are applied to the sentence before asking again, so a
        // correction that only makes sense once a neighbouring character is fixed (蜚語 once 與 became 語) can surface
        // in the next round. Changes stay expressed against the original composition, which is what Apply works on.
        // Returns null when the first request fails; a later failure keeps what was accepted so far.
        internal static List<Change> Refine(Snapshot s, Func<string, List<JevClient.Question>, Dictionary<string, JevClient.Answer>> ask, int maxRounds)
        {
            List<Change> accepted = new List<Change>(), optional = new List<Change>();
            string baseText = s.Current;
            string firstState = null; List<JevClient.Question> firstQuestions = null; Dictionary<string, JevClient.Answer> firstAnswers = null;
            for (int round = 1; round <= maxRounds; round++)
            {
                Snapshot view = new Snapshot { Current = baseText, Context = s.Context, Positions = s.Positions };
                string state;
                List<JevClient.Question> questions = BuildQuestions(view, out state);
                if (questions.Count == 0) break;
                Dictionary<string, JevClient.Answer> answers = AskBothOrders(ask, state, questions);
                if (answers == null) return round == 1 ? null : Finish(accepted, optional);
                if (round == 1) { firstState = state; firstQuestions = questions; firstAnswers = answers; }
                List<Change> changes = Propose(view, questions, answers);
                int included = 0;
                foreach (Change c in changes)
                {
                    if (c.Included) { included++; Merge(accepted, c, s.Current); }
                    else Remember(optional, c, s.Current);
                }
                if (included == 0) break;
                baseText = ApplyChanges(s.Current, accepted);
            }
            if (accepted.Count == 0 && firstAnswers != null)
            {
                List<Change> pair = PairStage(s, firstState, firstQuestions, firstAnswers, ask);
                if (pair != null) foreach (Change c in pair) Merge(accepted, c, s.Current);
            }
            return Finish(accepted, optional);
        }

        // Jev penalises whichever option is listed first when the options are otherwise close: for 約會要戴保險套 the
        // second slot took 0.9 whether it held 戴 or 帶, while clear cases (魷魚) are order-independent. Every question is
        // therefore sent twice in one request, in the original and the reversed order, and the two distributions are
        // averaged before anything is decided.
        internal static Dictionary<string, JevClient.Answer> AskBothOrders(Func<string, List<JevClient.Question>, Dictionary<string, JevClient.Answer>> ask, string state, List<JevClient.Question> questions)
        {
            List<JevClient.Question> both = new List<JevClient.Question>(questions);
            foreach (JevClient.Question q in questions)
            {
                JevClient.Question mirror = new JevClient.Question { Id = q.Id + "m", Offset = q.Offset, KeepId = q.KeepId, Instructions = q.Instructions };
                for (int i = q.Options.Count - 1; i >= 0; i--) mirror.Options.Add(q.Options[i]);
                both.Add(mirror);
            }
            Dictionary<string, JevClient.Answer> raw = ask(state, both);
            if (raw == null) return null;
            Dictionary<string, JevClient.Answer> merged = new Dictionary<string, JevClient.Answer>();
            foreach (JevClient.Question q in questions)
            {
                JevClient.Answer a, b;
                raw.TryGetValue(q.Id, out a); raw.TryGetValue(q.Id + "m", out b);
                merged[q.Id] = Average(a, b);
            }
            return merged;
        }

        internal static JevClient.Answer Average(JevClient.Answer a, JevClient.Answer b)
        {
            bool okA = a != null && a.Ok, okB = b != null && b.Ok;
            if (!okA && !okB) return new JevClient.Answer();
            if (!okA) return b;
            if (!okB) return a;
            JevClient.Answer r = new JevClient.Answer { Ok = true, Confidence = (a.Confidence + b.Confidence) / 2 };
            HashSet<string> ids = new HashSet<string>(a.Probabilities.Keys);
            ids.UnionWith(b.Probabilities.Keys);
            string best = null; double bestP = -1;
            foreach (string id in ids)
            {
                double pa, pb; a.Probabilities.TryGetValue(id, out pa); b.Probabilities.TryGetValue(id, out pb);
                double p = (pa + pb) / 2;
                r.Probabilities[id] = p;
                if (p > bestP) { bestP = p; best = id; }
            }
            r.Choice = best;
            return r;
        }

        internal const double PairFloor = 0.15;   // an alternative this likely on its own is worth trying together with its neighbour

        // Two wrong characters side by side (在是一次 for 再試一次) defeat per-position questions: neither 再是一次 nor
        // 在試一次 reads well alone. When nothing was accepted, one more question compares the sentence as it stands
        // with every position's best alternative applied alone and with each adjacent pair applied together.
        internal static List<Change> PairStage(Snapshot s, string state, List<JevClient.Question> questions, Dictionary<string, JevClient.Answer> answers, Func<string, List<JevClient.Question>, Dictionary<string, JevClient.Answer>> ask)
        {
            List<Change> best = new List<Change>();
            foreach (JevClient.Question q in questions)
            {
                JevClient.Answer a;
                if (!answers.TryGetValue(q.Id, out a) || !a.Ok) continue;
                if (InsideImeWord(s, q.Offset)) continue;   // a name or idiom the IME knows (羅密歐) is not second-guessed here
                Position p = null;
                foreach (Position candidate in s.Positions) if (candidate.Offset == q.Offset) { p = candidate; break; }
                if (p == null) continue;
                string bestId = null; double bestProb = 0;
                foreach (KeyValuePair<string, double> kv in a.Probabilities)
                    if (kv.Key != q.KeepId && kv.Key.StartsWith("c") && kv.Value > bestProb) { bestId = kv.Key; bestProb = kv.Value; }
                if (bestId == null || bestProb < PairFloor) continue;
                int idx = int.Parse(bestId.Substring(1)) - 1;
                if (idx < 0 || idx >= p.Items.Count) continue;
                string text = p.Items[idx];
                if (q.Offset + text.Length > s.Current.Length) continue;
                string replaced = s.Current.Substring(q.Offset, text.Length);
                if (text == replaced || IsGenderSwap(text, replaced)) continue;
                best.Add(new Change { Offset = q.Offset, Text = text, Replaced = replaced, Probability = bestProb });
            }
            if (best.Count < 2) return null;
            JevClient.Question pairQuestion = new JevClient.Question { Id = "pair", Offset = -1, KeepId = "keep", Instructions = "以下各句是同一句話的幾種寫法，有些同時換了相鄰的兩個字（讀音都相同）。哪一句語意正確、通順，最可能是使用者想打的？" };
            Dictionary<string, List<Change>> options = new Dictionary<string, List<Change>>();
            HashSet<string> sentences = new HashSet<string>();
            sentences.Add(s.Current);
            pairQuestion.Options.Add(new KeyValuePair<string, string>("keep", s.Current));
            for (int i = 0; i < best.Count; i++)
            {
                AddVariant(s, pairQuestion, options, sentences, "s" + i, new List<Change> { best[i] });
                if (i + 1 < best.Count && best[i].End == best[i + 1].Offset) AddVariant(s, pairQuestion, options, sentences, "d" + i, new List<Change> { best[i], best[i + 1] });
            }
            if (options.Count == 0) return null;
            Dictionary<string, JevClient.Answer> reply = AskBothOrders(ask, state, new List<JevClient.Question> { pairQuestion });
            JevClient.Answer answer;
            if (reply == null || !reply.TryGetValue("pair", out answer) || !answer.Ok || answer.Choice == "keep") return null;
            double prob, keep; answer.Probabilities.TryGetValue(answer.Choice, out prob); answer.Probabilities.TryGetValue("keep", out keep);
            if (prob < ProposeThreshold || prob < ProposeMargin * keep) return null;
            List<Change> chosen;
            if (!options.TryGetValue(answer.Choice, out chosen)) return null;
            foreach (Change c in chosen) { c.Probability = prob; c.Included = true; }
            return chosen;
        }

        // True when the IME converted this offset as part of a word of three or more characters (its first candidate at
        // an earlier or the same offset spans it). Such words come from the IME's dictionary and are rarely homophone
        // mistakes, unlike characters the IME converted one by one.
        internal static bool InsideImeWord(Snapshot s, int offset)
        {
            foreach (Position p in s.Positions)
            {
                string first = p.Items[0];
                if (first.Length >= 3 && p.Offset <= offset && offset < p.Offset + first.Length) return true;
            }
            return false;
        }

        static void AddVariant(Snapshot s, JevClient.Question q, Dictionary<string, List<Change>> options, HashSet<string> sentences, string id, List<Change> changes)
        {
            string sentence = ApplyChanges(s.Current, changes);
            if (!sentences.Add(sentence)) return;
            q.Options.Add(new KeyValuePair<string, string>(id, sentence));
            options[id] = changes;
        }

        // Accepted rows first at each offset, then optional ones by probability; an optional row that duplicates an
        // accepted one is dropped.
        static List<Change> Finish(List<Change> accepted, List<Change> optional)
        {
            List<Change> all = new List<Change>(accepted);
            foreach (Change o in optional)
            {
                bool duplicate = false;
                foreach (Change c in accepted) if (c.Offset == o.Offset && c.Text == o.Text) { duplicate = true; break; }
                if (!duplicate) all.Add(o);
            }
            all.Sort(delegate(Change a, Change b)
            {
                if (a.Offset != b.Offset) return a.Offset.CompareTo(b.Offset);
                if (a.Included != b.Included) return a.Included ? -1 : 1;
                return b.Probability.CompareTo(a.Probability);
            });
            return all;
        }

        // Optional rows are kept across rounds (best probability wins) and always describe the original composition.
        static void Remember(List<Change> optional, Change c, string original)
        {
            c.Replaced = original.Substring(c.Offset, c.Text.Length);
            if (c.Text == c.Replaced) return;
            foreach (Change o in optional) if (o.Offset == c.Offset && o.Text == c.Text) { if (c.Probability > o.Probability) o.Probability = c.Probability; return; }
            optional.Add(c);
        }

        // A new change replaces any accepted change it overlaps; choosing the original text again just drops them.
        static void Merge(List<Change> accepted, Change c, string original)
        {
            accepted.RemoveAll(delegate(Change old) { return old.Offset < c.Offset + c.Text.Length && c.Offset < old.Offset + old.Text.Length; });
            c.Replaced = original.Substring(c.Offset, c.Text.Length);
            if (c.Text != c.Replaced) accepted.Add(c);
        }

        internal static string ApplyChanges(string original, List<Change> changes)
        {
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < original.Length)
            {
                Change hit = null;
                foreach (Change c in changes) if (c.Offset == i) { hit = c; break; }
                if (hit != null) { sb.Append(hit.Text); i += hit.Text.Length; }
                else { sb.Append(original[i]); i++; }
            }
            return sb.ToString();
        }

        // One Choice question per composition offset. Every option is the whole sentence with that offset's candidate
        // substituted, so Jev judges sentences rather than bare characters. Candidates that yield the same sentence (the
        // word 由於 and the single 由) are merged into the first id, otherwise they split the probability of keeping it.
        // The current conversion is deliberately absent from the state: with it Jev anchors on it and never changes a thing.
        // Wording chosen on the 16-sentence evaluation in tests\JevboardTests.cs (--live).
        internal static List<JevClient.Question> BuildQuestions(Snapshot s, out string state)
        {
            state = (s.Context.Length > 0 ? "這句話前面已輸入的文字：「" + s.Context + "」\n" : "")
                + "使用者正在用注音輸入法打一句台灣繁體中文。注音輸入法只能靠讀音猜字，常把同音的詞選錯。每一題的所有選項讀音完全相同、只有用字不同，請依整句的語意判斷哪一句才是使用者想打的。";
            List<JevClient.Question> questions = new List<JevClient.Question>();
            foreach (Position p in s.Positions)
            {
                if (!HasHanzi(p.Items[0])) continue;   // unknown (space/punctuation) or symbol positions get no question
                JevClient.Question q = new JevClient.Question { Id = "p" + p.Offset, Offset = p.Offset, Instructions = "以下各句只有第 " + (p.Offset + 1) + " 個字開始的詞不同（讀音相同）。哪一句語意正確、通順，且用的是台灣常用的詞？不要選簡體字、生僻字或不成詞的組合。" };
                HashSet<string> sentences = new HashSet<string>();
                for (int i = 0; i < p.Items.Count; i++)
                {
                    string text = p.Items[i];
                    if (IsBopomofo(text) || p.Offset + text.Length > s.Current.Length) continue;
                    if (i > 0 && !AllowedAlternative(text)) continue;
                    string sentence = s.Current.Substring(0, p.Offset) + text + s.Current.Substring(p.Offset + text.Length);
                    if (!sentences.Add(sentence)) continue;
                    if (sentence == s.Current) q.KeepId = "c" + (i + 1);   // the option that leaves the sentence as it is
                    q.Options.Add(new KeyValuePair<string, string>("c" + (i + 1), sentence));
                }
                // The converted character may sit on a later page; then no candidate reproduces the sentence and
                // "keep" stands in for it (it maps to no key and is never a change).
                if (!sentences.Contains(s.Current) && q.Options.Count > 0) { q.KeepId = "keep"; q.Options.Insert(0, new KeyValuePair<string, string>("keep", s.Current)); }
                if (q.Options.Count > 1) questions.Add(q);
            }
            return questions;
        }

        // Left to right; a multi-character candidate covers the following offsets. A change needs Jev to pick something
        // other than the option that keeps the sentence, with at least ProposeThreshold and more than that option gets.
        internal const double OptionalThreshold = 0.3;   // alternatives at least this likely are shown, switched off
        internal const int OptionalPerPosition = 2;

        internal static List<Change> Propose(Snapshot s, List<JevClient.Question> questions, Dictionary<string, JevClient.Answer> answers)
        {
            List<Change> changes = new List<Change>();
            foreach (JevClient.Question q in questions)
            {
                JevClient.Answer a;
                if (!answers.TryGetValue(q.Id, out a) || !a.Ok) continue;
                Position p = null;
                foreach (Position candidate in s.Positions) if (candidate.Offset == q.Offset) { p = candidate; break; }
                if (p == null) continue;
                double keep; a.Probabilities.TryGetValue(q.KeepId, out keep);
                List<KeyValuePair<string, double>> ranked = new List<KeyValuePair<string, double>>();
                foreach (KeyValuePair<string, double> kv in a.Probabilities)
                    if (kv.Key != q.KeepId && kv.Key.StartsWith("c") && kv.Value >= OptionalThreshold) ranked.Add(kv);
                ranked.Sort(delegate(KeyValuePair<string, double> x, KeyValuePair<string, double> y) { return y.Value.CompareTo(x.Value); });
                int shown = 0;
                for (int r = 0; r < ranked.Count && shown < OptionalPerPosition; r++)
                {
                    int idx = int.Parse(ranked[r].Key.Substring(1)) - 1;
                    if (idx < 0 || idx >= p.Items.Count) continue;
                    string text = p.Items[idx];
                    if (q.Offset + text.Length > s.Current.Length) continue;
                    string replaced = s.Current.Substring(q.Offset, text.Length);
                    if (text == replaced || IsGenderSwap(text, replaced)) continue;
                    double prob = ranked[r].Value;
                    Change c = new Change { Offset = q.Offset, Text = text, Replaced = replaced, Probability = prob, Included = false };
                    bool covered = false;
                    foreach (Change other in changes) if (other.Included && Change.Overlaps(other, c)) { covered = true; break; }
                    c.Included = r == 0 && prob >= ProposeThreshold && prob >= ProposeMargin * keep && !covered;
                    changes.Add(c);
                    shown++;
                }
            }
            return changes;
        }

        internal static int IncludedCount(List<Change> changes) { int n = 0; foreach (Change c in changes) if (c.Included) n++; return n; }

        // Switching a row on switches off every included row whose range overlaps it (same position, or a word that
        // covers it); switching off never touches the others.
        internal static void Toggle(List<Change> changes, int index)
        {
            Change c = changes[index];
            c.Included = !c.Included;
            if (!c.Included) return;
            foreach (Change other in changes) if (other != c && other.Included && Change.Overlaps(other, c)) other.Included = false;
        }

        // 你/妳 and 他/她 differ only by the addressee's gender, which the sentence rarely tells; never guess it.
        // (它/牠 → 他/她 is a real correction and stays allowed.)
        internal static bool IsGenderSwap(string text, string replaced)
        {
            if (text.Length != 1 || replaced.Length != 1) return false;
            string pair = text + replaced;
            return pair == "你妳" || pair == "妳你" || pair == "他她" || pair == "她他";
        }

        // A single character offered as an alternative must be frequent (Big5 common block), so obscure variants such
        // as 懽 never appear; a word the IME lists (蜚語, 流言蜚語) may contain a less frequent character, but nothing
        // outside Big5 (simplified forms such as 办).
        internal static bool AllowedAlternative(string text) { return text.Length > 1 ? InBig5(text) : IsCommonHanzi(text); }

        static readonly Encoding big5 = Encoding.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        internal static bool InBig5(string text)
        {
            try { big5.GetBytes(text); return true; } catch (EncoderFallbackException) { return false; }
        }

        // True when every character is in Big5's frequently-used block (A440–C67E), a symbol, or ASCII. Characters
        // outside Big5 (simplified forms such as 办) and in the less-frequent block (variants such as 懽) are rejected,
        // because the IME lists them and Jev occasionally prefers them.
        internal static bool IsCommonHanzi(string text)
        {
            foreach (char ch in text)
            {
                if (ch < 0x80) continue;
                byte[] bytes;
                try { bytes = big5.GetBytes(new string(ch, 1)); } catch (EncoderFallbackException) { return false; }
                if (bytes.Length != 2 || bytes[0] < 0xA4) continue;   // symbols and punctuation
                if (bytes[0] > 0xC6 || (bytes[0] == 0xC6 && bytes[1] > 0x7E)) return false;
            }
            return true;
        }

        internal static bool HasHanzi(string text)
        {
            foreach (char ch in text) if (ch >= '一' && ch <= '鿿') return true;
            return false;
        }

        internal static bool IsBopomofo(string text)
        {
            foreach (char ch in text) if ((ch >= '㄀' && ch <= 'ㄯ') || ch == 'ˊ' || ch == 'ˇ' || ch == 'ˋ' || ch == '˙') return true;
            return false;
        }

        void ApplyProposal()
        {
            Snapshot s = current;
            if (s == null || busy) return;
            List<Change> included = overlay.IncludedChanges();
            if (included.Count == 0) { Cancel("nothing included"); return; }
            if (!TargetUnchanged(s)) { Cancel("target changed at apply"); return; }
            int gen = s.Generation; busy = true;
            overlay.ShowStatus("套用中…（請勿操作鍵盤）");
            if (s.Exact && ApplyByPaste(s, included, gen)) return;
            Log.Write("gen " + gen + " apply changes=" + included.Count + " via candidate keys");
            Worker(delegate
            {
                string err = Candidates.Apply(s.Popup, included, delegate { return gen != generation; });
                overlay.BeginInvoke(new Action(delegate
                {
                    busy = false;
                    if (gen != generation) return;
                    if (err == null) { Log.Write("gen " + gen + " applied"); Cancel(null); }
                    else { Log.Write("gen " + gen + " apply stopped"); Fail(err); }
                }));
            });
        }

        internal enum KeyDecision { Apply, Toggle, CancelAndSwallow, CancelAndPass }

        // Tab applies a shown proposal, a digit toggles that row; Esc closes only the overlay; any other key cancels
        // and goes through to the IME.
        internal static KeyDecision DecideKey(int vk, bool proposalShowing, bool busy)
        {
            if (proposalShowing && !busy)
            {
                if (vk == Native.VK_TAB) return KeyDecision.Apply;
                if (RowIndex(vk) >= 0) return KeyDecision.Toggle;
            }
            return vk == Native.VK_ESCAPE ? KeyDecision.CancelAndSwallow : KeyDecision.CancelAndPass;
        }

        // 1–9 on the main keyboard or the numeric keypad → row 0–8.
        internal static int RowIndex(int vk)
        {
            if (vk >= 0x31 && vk <= 0x39) return vk - 0x31;
            if (vk >= Native.VK_NUMPAD1 && vk <= Native.VK_NUMPAD1 + 8) return vk - Native.VK_NUMPAD1;
            return -1;
        }

        // Fast path when the composition text is known exactly: cancel the composition with Esc (the list is closed,
        // so Esc drops the whole uncommitted text) and paste the corrected sentence, which commits it. The clipboard's
        // text is put back afterwards. Returns false (nothing done) when the clipboard cannot be used.
        bool ApplyByPaste(Snapshot s, List<Change> included, int gen)
        {
            string text = ApplyChanges(s.Current, included);
            string savedText = null;
            try
            {
                if (Clipboard.ContainsText()) savedText = Clipboard.GetText();
                Clipboard.SetText(text);
            }
            catch (Exception ex) { Log.Write("gen " + gen + " clipboard unavailable: " + ex.GetType().Name); return false; }
            Log.Write("gen " + gen + " apply changes=" + included.Count + " via paste");
            Worker(delegate
            {
                bool ok = Candidates.CloseList(s.Popup) && gen == generation;
                if (ok)
                {
                    Native.Tap(Native.VK_ESCAPE);          // cancel the composition
                    Thread.Sleep(60);
                    Native.Chord(Native.VK_CONTROL, (ushort)'V');
                    Thread.Sleep(120);
                }
                overlay.BeginInvoke(new Action(delegate
                {
                    try { if (savedText != null) Clipboard.SetText(savedText); } catch (Exception) { }
                    busy = false;
                    if (gen != generation) return;
                    if (ok) { Log.Write("gen " + gen + " applied"); Cancel(null); }
                    else { Log.Write("gen " + gen + " apply stopped"); Fail("候選清單沒有關閉，未套用"); }
                }));
            });
            return true;
        }

        bool KeyWhileOverlay(int vk)
        {
            switch (DecideKey(vk, overlay.HasProposal, busy))
            {
                case KeyDecision.Apply: ApplyProposal(); return true;
                case KeyDecision.Toggle: overlay.ToggleRow(RowIndex(vk)); return true;
                case KeyDecision.CancelAndSwallow: Cancel("key " + vk + " pressed"); return true;
                default: Cancel("key " + vk + " pressed"); return false;
            }
        }

        void Fail(string message)
        {
            overlay.ShowStatus(message); overlay.AutoHide();
            watch.Stop(); current = null; generation++;
        }

        void Cancel(string reason)
        {
            if (reason != null && current != null) Log.Write("gen " + current.Generation + " cancelled: " + reason);
            watch.Stop(); current = null; generation++;
            overlay.HideOverlay();
        }

        static void Worker(ThreadStart work)
        {
            Thread t = new Thread(delegate() { try { work(); } catch (Exception ex) { Log.Write("worker error " + ex.GetType().Name + ": " + ex.Message); } });
            t.IsBackground = true; t.SetApartmentState(ApartmentState.MTA); t.Start();
        }

        static Native.RECT ToScreen(IntPtr caretWindow, Native.RECT caret)
        {
            if (caretWindow == IntPtr.Zero) return new Native.RECT();
            Native.POINT tl = new Native.POINT { x = caret.left, y = caret.top }, br = new Native.POINT { x = caret.right, y = caret.bottom };
            Native.ClientToScreen(caretWindow, ref tl); Native.ClientToScreen(caretWindow, ref br);
            return new Native.RECT { left = tl.x, top = tl.y, right = br.x, bottom = br.y };
        }

        protected override void ExitThreadCore() { hotkey.Dispose(); tray.Visible = false; base.ExitThreadCore(); }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using (new Mutex(true, "Jevboard.SingleInstance", out created))
            {
                if (!created) return;
                Application.Run(new App());
            }
        }
    }
}
