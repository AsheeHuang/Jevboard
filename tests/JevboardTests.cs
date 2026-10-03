using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Jevboard.Tests
{
    // Static tests: no IME, no keyboard, no windows. Fixtures are candidate pages captured from Microsoft Bopomofo
    // and a response in the shape the Jev API returns. `--live` additionally runs the 16-sentence prompt evaluation
    // against the real API with the saved key.
    static class Runner
    {
        static int passed, failed;
        static void Check(bool ok, string what) { if (ok) passed++; else { failed++; Console.WriteLine("FAIL: " + what); } }
        static void Eq(string expected, string actual, string what) { Check(expected == actual, what + ": expected [" + expected + "] got [" + actual + "]"); }
        static void Eq(int expected, int actual, string what) { Check(expected == actual, what + ": expected " + expected + " got " + actual); }

        // Page-1 candidates captured on 2026-10-03 for ㄧㄡˊ ㄩˊ ㄏㄠˇ ㄏㄠˇ ㄔ; the IME had converted the sentence to 由於好好吃.
        static Snapshot Youyu()
        {
            Snapshot s = new Snapshot();
            string[][] pages = {
                new[] { "由於", "魷魚", "由", "游", "遊", "油", "尤", "猶", "郵" },
                new[] { "於", "餘", "魚", "余", "漁", "瑜", "虞", "逾", "于" },
                new[] { "好好吃", "好", "郝", "恏", "ㄏㄠˇ" },
                new[] { "好吃", "好", "郝", "恏", "ㄏㄠˇ" },
                new[] { "吃", "癡", "痴", "嗤", "喫", "笞", "ㄔ", "鴟", "摛" } };
            for (int i = 0; i < pages.Length; i++) s.Positions.Add(new Position { Offset = i, Items = new List<string>(pages[i]) });
            s.Current = Candidates.CurrentText(s.Positions);
            return s;
        }

        // Mirrored questions (id + "m") are answered like their originals by the fakes below.
        static string Base(string id) { return id.EndsWith("m") ? id.Substring(0, id.Length - 1) : id; }

        static JevClient.Answer Answer(string choice, params object[] idProbPairs)
        {
            JevClient.Answer a = new JevClient.Answer { Ok = true, Choice = choice };
            for (int i = 0; i < idProbPairs.Length; i += 2) a.Probabilities[(string)idProbPairs[i]] = Convert.ToDouble(idProbPairs[i + 1]);
            return a;
        }

        // --preview: show the overlay with sample data for 1.5 s (never activated, no input) and save a screenshot
        // of it to bin\overlay-preview.png, so the look can be checked without driving the IME.
        [STAThread]
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (Array.IndexOf(args, "--preview") >= 0) return Preview();
            Action[] tests = { TestCurrentText, TestResolveCurrent, TestBopomofo, TestCommonHanzi, TestQuestions, TestRareAlternatives, TestUnknownPositions, TestParse, TestPropose, TestProposedText, TestRefine, TestPairStage, TestBothOrders, TestKeys };
            foreach (Action test in tests)
            {
                try { test(); }
                catch (Exception ex) { failed++; Console.WriteLine("FAIL: " + test.Method.Name + " threw " + ex.GetType().Name + ": " + ex.Message); }
            }
            Console.WriteLine(passed + " passed, " + failed + " failed");
            if (failed == 0 && Array.IndexOf(args, "--live") >= 0) JevEval.Run();
            if (failed == 0 && Array.IndexOf(args, "--whole") >= 0) JevEval.RunMulti();
            return failed == 0 ? 0 : 1;
        }

        static int Preview()
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            Overlay overlay = new Overlay();
            overlay.CreateControl();
            List<Change> changes = new List<Change> {
                new Change { Offset = 1, Text = "再", Replaced = "在", Probability = 0.86 },
                new Change { Offset = 3, Text = "依次", Replaced = "一次", Probability = 0.32, Included = false },
                new Change { Offset = 5, Text = "瑞士", Replaced = "芮氏", Probability = 0.98 } };
            overlay.ShowStatus("…", new System.Drawing.Point(200, 200));
            overlay.ShowSentence("想在去一次芮氏", "Jev 分析整句中…");
            overlay.ShowProposal("Jev 建議修改 2 處，另有 1 個可選", changes);
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += delegate
            {
                timer.Stop();
                System.Drawing.Rectangle r = overlay.Bounds;
                using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(r.Width + 24, r.Height + 24))
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.X - 12, r.Y - 12, 0, 0, bmp.Size);
                    bmp.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "overlay-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                overlay.HideOverlay();
                System.Windows.Forms.Application.Exit();
            };
            timer.Start();
            System.Windows.Forms.Application.Run();
            Console.WriteLine("preview saved to bin\\overlay-preview.png");
            return 0;
        }

        static void TestCurrentText()
        {
            Eq("由於好好吃", Youyu().Current, "current sentence = first character of each position's first candidate");
        }

        // 做愛要帶保險套: the ㄧㄠˋ page lists 藥 first although the sentence reads 要; the editor's text settles it.
        static void TestResolveCurrent()
        {
            Snapshot s = new Snapshot();
            s.Positions.Add(new Position { Offset = 0, Items = new List<string> { "做", "作", "坐" } });
            s.Positions.Add(new Position { Offset = 1, Items = new List<string> { "愛", "艾", "礙" } });
            s.Positions.Add(new Position { Offset = 2, Items = new List<string> { "藥", "要", "耀", "鑰" } });
            s.Positions.Add(new Position { Offset = 3, Items = new List<string> { "帶", "戴", "待" } });
            s.Positions.Add(new Position { Offset = 4, Items = new List<string> { "保險套", "保險", "保", "寶" } });
            s.Positions.Add(new Position { Offset = 5, Items = new List<string> { "險", "顯", "鮮" } });
            s.Positions.Add(new Position { Offset = 6, Items = new List<string> { "套", "討", "套套" } });
            Eq("做愛藥帶保險套", Candidates.CurrentText(s.Positions), "first candidates alone get 藥");
            string context;
            Eq("做愛要帶保險套", Candidates.ResolveCurrent(s.Positions, "昨天說過，做愛要帶保險套", out context), "text before the caret settles 要");
            Eq("昨天說過，", context, "committed text in front becomes the context");
            Check(Candidates.ResolveCurrent(s.Positions, "這是別的文字一二三四五六七", out context) == null, "text that is not the composition is rejected");
            Check(Candidates.ResolveCurrent(s.Positions, "太短", out context) == null, "too short is rejected");
            Check(Candidates.ResolveCurrent(s.Positions, null, out context) == null, "no text pattern is rejected");

            // Unknown positions take their real characters from the text.
            Snapshot mixed = new Snapshot();
            mixed.Positions.Add(new Position { Offset = 0, Items = new List<string> { "我", "婐" } });
            mixed.Positions.Add(Candidates.Unknown(1)); mixed.Positions.Add(Candidates.Unknown(2)); mixed.Positions.Add(Candidates.Unknown(3));
            mixed.Positions.Add(new Position { Offset = 4, Items = new List<string> { "好", "郝" } });
            Eq("我 OK好", Candidates.ResolveCurrent(mixed.Positions, "我 OK好", out context), "spaces and letters come through");

            // With the real current text the keep option is the matching candidate, not c1.
            s.Current = "做愛要帶保險套";
            string state; List<JevClient.Question> qs = App.BuildQuestions(s, out state);
            JevClient.Question q2 = null; foreach (JevClient.Question q in qs) if (q.Id == "p2") q2 = q;
            Eq("c2", q2.KeepId, "keep is the candidate that reproduces the sentence (要)");
            Eq("做愛藥帶保險套", q2.Options[0].Value, "c1 (藥) is an alternative now");

            // Converted character absent from the page: a synthetic keep option stands in and is never a change.
            s.Current = "做愛麼帶保險套";
            qs = App.BuildQuestions(s, out state);
            q2 = null; foreach (JevClient.Question q in qs) if (q.Id == "p2") q2 = q;
            Eq("keep", q2.KeepId, "synthetic keep id"); Eq("做愛麼帶保險套", q2.Options[0].Value, "keep option carries the current sentence");
            Dictionary<string, JevClient.Answer> answers = new Dictionary<string, JevClient.Answer>();
            answers["p2"] = Answer("keep", "keep", 0.9, "c2", 0.1);
            Eq(0, App.Propose(s, qs, answers).Count, "choosing keep with the alternative at 0.1 shows nothing");
            answers["p2"] = Answer("c2", "keep", 0.1, "c2", 0.8);
            List<Change> changes = App.Propose(s, qs, answers);
            Eq(1, changes.Count, "a candidate beating keep is a change"); Eq("要", changes[0].Text, "change text"); Eq("麼", changes[0].Replaced, "replaced text");
        }

        static void TestBopomofo()
        {
            Check(App.IsBopomofo("ㄏㄠˇ"), "bopomofo syllable detected");
            Check(App.IsBopomofo("ㄔ"), "bare bopomofo detected");
            Check(!App.IsBopomofo("好"), "hanzi is not bopomofo");
        }

        static void TestCommonHanzi()
        {
            foreach (string ok in new[] { "歡", "辦", "于", "妳", "魷魚", "檯", "她", "牠", "後", "發", "，", "A1" }) Check(App.IsCommonHanzi(ok), "common: " + ok);
            foreach (string bad in new[] { "懽", "办", "伱", "儗", "祂", "裏", "发", "公事公办" }) Check(!App.IsCommonHanzi(bad), "rare or simplified: " + bad);
        }

        // ㄋㄧˇ page captured on 2026-10-02: rare variants must not become alternatives, but a rare current choice stays as c1.
        static void TestRareAlternatives()
        {
            Snapshot s = new Snapshot();
            s.Positions.Add(new Position { Offset = 0, Items = new List<string> { "你", "妳", "擬", "隬", "旎", "昵", "伱", "伲", "儗" } });
            s.Current = Candidates.CurrentText(s.Positions);
            string state;
            List<JevClient.Question> qs = App.BuildQuestions(s, out state);
            Eq(1, qs.Count, "one question");
            List<string> values = new List<string>();
            foreach (KeyValuePair<string, string> o in qs[0].Options) values.Add(o.Value);
            Eq("你 妳 擬 旎", string.Join(" ", values.ToArray()), "隬/昵/儗 (Big5 rare block) and 伱/伲 (not Big5) dropped, 旎 (common block) kept");
            Eq("c3", qs[0].Options[2].Key, "ids still follow the IME page index");
            Eq("c5", qs[0].Options[3].Key, "旎 keeps its page index after the gap");

            Snapshot rareCurrent = new Snapshot();
            rareCurrent.Positions.Add(new Position { Offset = 0, Items = new List<string> { "懽", "歡", "讙" } });
            rareCurrent.Current = Candidates.CurrentText(rareCurrent.Positions);
            qs = App.BuildQuestions(rareCurrent, out state);
            Eq(2, qs[0].Options.Count, "rare current (c1) kept, rare alternative dropped");
            Eq("懽", qs[0].Options[0].Value, "c1 is the current text even when rare");
            Eq("歡", qs[0].Options[1].Value, "common alternative offered");

            Snapshot idiom = new Snapshot();
            idiom.Positions.Add(new Position { Offset = 0, Items = new List<string> { "留言", "流言" } });
            idiom.Positions.Add(new Position { Offset = 1, Items = new List<string> { "言" } });
            idiom.Positions.Add(new Position { Offset = 2, Items = new List<string> { "妃與", "妃", "非", "蜚語", "蜚", "办公" } });
            idiom.Positions.Add(new Position { Offset = 3, Items = new List<string> { "與", "語" } });
            idiom.Current = Candidates.CurrentText(idiom.Positions);
            qs = App.BuildQuestions(idiom, out state);
            JevClient.Question q2 = null; foreach (JevClient.Question q in qs) if (q.Id == "p2") q2 = q;
            values.Clear(); foreach (KeyValuePair<string, string> o in q2.Options) values.Add(o.Value);
            Eq("留言妃與 留言非與 留言蜚語", string.Join(" ", values.ToArray()), "妃 merged, the word 蜚語 allowed, single 蜚 and 办公 dropped");
            Check(App.AllowedAlternative("蜚語") && !App.AllowedAlternative("蜚") && !App.AllowedAlternative("办公") && !App.AllowedAlternative("懽"), "alternative rule");
        }

        // A space or punctuation inside the composition opens no candidate list: it is kept as one unknown character so
        // later offsets stay right, gets no question, and trailing probes past the end are dropped.
        static void TestUnknownPositions()
        {
            Snapshot s = new Snapshot();
            s.Positions.Add(new Position { Offset = 0, Items = new List<string> { "你", "妳" } });
            s.Positions.Add(Candidates.Unknown(1));
            s.Positions.Add(new Position { Offset = 2, Items = new List<string> { "好", "郝" } });
            s.Positions.Add(new Position { Offset = 3, Items = new List<string> { "嗎", "媽", "馬" } });
            s.Current = Candidates.CurrentText(s.Positions);
            Eq("你　好嗎", s.Current, "unknown position shows as an ideographic space");
            string state;
            List<JevClient.Question> qs = App.BuildQuestions(s, out state);
            Eq(3, qs.Count, "no question for the unknown position");
            Eq("p2", qs[1].Id, "offsets after the space are unchanged");
            Eq("你　郝嗎", qs[1].Options[1].Value, "options keep the space in place");

            Dictionary<string, JevClient.Answer> answers = new Dictionary<string, JevClient.Answer>();
            answers["p3"] = Answer("c2", "c1", 0.2, "c2", 0.7);
            List<Change> changes = App.Propose(s, qs, answers);
            Eq(1, changes.Count, "change after the space"); Eq(3, changes[0].Offset, "offset counts the space as one character");

            Check(!App.HasHanzi(Candidates.UnknownChar) && !App.HasHanzi("，") && App.HasHanzi("好"), "hanzi detection");

            // 我用Jev測試: forward reads 我, 用 and stops at J; from End, Left meets 試, 測, three unreadable letters, then 用 again.
            List<Position> forward = new List<Position> { new Position { Offset = 0, Items = new List<string> { "我" } }, new Position { Offset = 1, Items = new List<string> { "用" } } };
            List<KeyValuePair<int, Position>> tail = new List<KeyValuePair<int, Position>>();
            tail.Add(new KeyValuePair<int, Position>(1, new Position { Items = new List<string> { "試" } }));
            tail.Add(new KeyValuePair<int, Position>(2, new Position { Items = new List<string> { "測" } }));
            tail.Add(new KeyValuePair<int, Position>(3, null)); tail.Add(new KeyValuePair<int, Position>(4, null)); tail.Add(new KeyValuePair<int, Position>(5, null));
            List<Position> all = Candidates.MergeTail(forward, forward[1], tail);
            Eq("我用　　　測試", Candidates.CurrentText(all), "length from Left presses; letters become unknown characters");
            Eq(7, all.Count, "one position per character");
            Eq(5, all[5].Offset, "測 sits right after the letters");

            // 我喜歡你。: the full stop is the only unreadable character, met once forward and once on the way back.
            forward = new List<Position> { new Position { Offset = 0, Items = new List<string> { "我" } }, new Position { Offset = 1, Items = new List<string> { "喜歡", "喜" } }, new Position { Offset = 2, Items = new List<string> { "歡" } }, new Position { Offset = 3, Items = new List<string> { "你" } } };
            tail = new List<KeyValuePair<int, Position>>(); tail.Add(new KeyValuePair<int, Position>(1, null));
            all = Candidates.MergeTail(forward, forward[3], tail);
            Eq("我喜歡你　", Candidates.CurrentText(all), "trailing punctuation kept as one unknown character");
        }

        static void TestQuestions()
        {
            Snapshot s = Youyu();
            string state;
            List<JevClient.Question> qs = App.BuildQuestions(s, out state);
            Check(!state.Contains("由於好好吃"), "state must not contain the current sentence (anchoring)");
            Eq(5, qs.Count, "one question per offset");
            JevClient.Question q0 = qs[0];
            Eq("p0", q0.Id, "question id");
            Eq(0, q0.Offset, "question offset"); Eq("c1", q0.KeepId, "the IME's choice keeps the sentence");
            Check(q0.Instructions.Contains("第 1 個字"), "instructions name the 1-based position");
            Eq(8, q0.Options.Count, "由 merged into 由於 (same sentence): 9 candidates -> 8 options");
            Eq("c1", q0.Options[0].Key, "first option id is the IME's current choice");
            Eq("由於好好吃", q0.Options[0].Value, "c1 is the current sentence");
            Eq("c2", q0.Options[1].Key, "ids keep the IME page index even after merging");
            Eq("魷魚好好吃", q0.Options[1].Value, "two-character word replaces two characters");
            Eq("c4", q0.Options[2].Key, "c3 (由) was merged away");
            Eq("游於好好吃", q0.Options[2].Value, "single character replaces one character");
            JevClient.Question q2 = qs[2];
            Eq(2, q2.Options.Count, "好好吃 and 好 merge; 恏 (not Big5) and ㄏㄠˇ excluded, 郝 stays");
            foreach (JevClient.Question q in qs) foreach (KeyValuePair<string, string> o in q.Options) Check(!App.IsBopomofo(o.Value), "no bopomofo in option " + q.Id + "/" + o.Key);
            s.Context = "昨天";
            App.BuildQuestions(s, out state);
            Check(state.StartsWith("這句話前面已輸入的文字：「昨天」"), "committed context goes first in the state");
        }

        static void TestParse()
        {
            string json = "{\"model\":\"jev-1.13.0\",\"answers\":{"
                + "\"p0\":{\"type\":\"choice\",\"choice\":\"c2\",\"confidence\":0.5,\"probabilities\":{\"c1\":0.19,\"c2\":0.33,\"c4\":0.09,\"c5\":0.33,\"c6\":0.01,\"c7\":0.01,\"c8\":0.04,\"c9\":0.0}},"
                + "\"p1\":{\"type\":\"choice\",\"choice\":\"c9\",\"confidence\":0.4,\"probabilities\":{\"c1\":0.42,\"c9\":0.55}},"
                + "\"p2\":{\"type\":\"choice\",\"choice\":\"c7\",\"probabilities\":{\"c1\":1.0}},"
                + "\"p3\":{\"type\":\"choice\",\"choice\":\"c1\",\"probabilities\":{\"c1\":1.5}}},\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";
            Dictionary<string, HashSet<string>> ids = new Dictionary<string, HashSet<string>>();
            ids["p0"] = new HashSet<string>(new[] { "c1", "c2", "c4", "c5", "c6", "c7", "c8", "c9" });
            ids["p1"] = new HashSet<string>(new[] { "c1", "c2", "c3", "c4", "c5", "c6", "c7", "c8", "c9" });
            ids["p2"] = new HashSet<string>(new[] { "c1", "c3", "c4" });
            ids["p3"] = new HashSet<string>(new[] { "c1", "c3", "c4" });
            ids["p4"] = new HashSet<string>(new[] { "c1", "c2" });
            Dictionary<string, JevClient.Answer> answers = new Dictionary<string, JevClient.Answer>();
            JevClient.Parse(json, ids, answers);
            Eq(5, answers.Count, "an answer object per asked question");
            Check(answers["p0"].Ok && answers["p0"].Choice == "c2" && Math.Abs(answers["p0"].Probabilities["c2"] - 0.33) < 1e-9 && Math.Abs(answers["p0"].Confidence - 0.5) < 1e-9, "valid answer parsed");
            Check(answers["p1"].Ok && answers["p1"].Choice == "c9", "second answer parsed");
            Check(!answers["p2"].Ok, "choice outside the snapshot ids is rejected");
            Check(!answers["p3"].Ok, "probability above 1 is rejected");
            Check(!answers["p4"].Ok, "question missing from the response is not Ok");
            Dictionary<string, JevClient.Answer> none = new Dictionary<string, JevClient.Answer>();
            JevClient.Parse("{\"error\":\"nope\"}", ids, none);
            Eq(0, none.Count, "response without answers yields nothing");
        }

        static void TestPropose()
        {
            Snapshot s = Youyu();
            string st; List<JevClient.Question> qs = App.BuildQuestions(s, out st);
            Dictionary<string, JevClient.Answer> answers = new Dictionary<string, JevClient.Answer>();
            answers["p0"] = Answer("c2", "c1", 0.19, "c2", 0.6);
            answers["p1"] = Answer("c9", "c1", 0.2, "c9", 0.6);
            List<Change> changes = App.Propose(s, qs, answers);
            Eq(1, App.IncludedCount(changes), "word change at offset 0 covers offset 1, so the 于 change is not applied");
            Eq("魷魚", changes[0].Text, "proposed text"); Eq("由於", changes[0].Replaced, "replaced text"); Eq(0, changes[0].Offset, "offset");

            answers["p0"] = Answer("c2", "c1", 0.5, "c2", 0.35);
            changes = App.Propose(s, qs, answers);
            Eq(1, App.IncludedCount(changes), "below threshold at offset 0, so offset 1 is applied");
            Check(changes[0].Offset == 0 && !changes[0].Included, "offset 0 stays an optional row"); Check(changes[1].Included && changes[1].Text == "于", "offset 1 change");

            answers["p0"] = Answer("c2", "c1", 0.40, "c2", 0.55);
            answers["p1"] = Answer("c1", "c1", 0.9);
            changes = App.Propose(s, qs, answers);
            Eq(0, App.IncludedCount(changes), "0.55 against 0.40 is within the noise margin: not applied by default");
            Eq(1, changes.Count, "but it is shown as an optional row"); Check(!changes[0].Included && changes[0].Text == "魷魚", "optional row content");

            answers["p0"] = Answer("c2", "c1", 0.26, "c2", 0.52);
            Eq(1, App.IncludedCount(App.Propose(s, qs, answers)), "0.52 against 0.26 is twice as likely and accepted");

            // 60% A / 40% B: A on, B shown off; switching B on switches A off.
            answers["p0"] = Answer("c2", "c1", 0.05, "c2", 0.55, "c5", 0.35);
            answers["p1"] = Answer("c1", "c1", 0.9);
            changes = App.Propose(s, qs, answers);
            Eq(2, changes.Count, "two rows at offset 0"); Check(changes[0].Included && changes[0].Text == "魷魚" && !changes[1].Included && changes[1].Text == "遊", "best on, runner-up off");
            App.Toggle(changes, 1);
            Check(changes[1].Included && !changes[0].Included, "exclusive at the same position");
            App.Toggle(changes, 1);
            Check(!changes[1].Included && !changes[0].Included, "switching off touches nothing else");

            // keep 60% / alternative 40%: nothing applied, the alternative is offered.
            answers["p0"] = Answer("c1", "c1", 0.6, "c5", 0.4);
            changes = App.Propose(s, qs, answers);
            Eq(1, changes.Count, "alternative offered"); Eq(0, App.IncludedCount(changes), "nothing applied by default");

            // an included word covering offset 1 keeps a row at offset 1 optional
            answers["p0"] = Answer("c2", "c1", 0.1, "c2", 0.9);
            answers["p1"] = Answer("c9", "c1", 0.1, "c9", 0.8);
            changes = App.Propose(s, qs, answers);
            Eq(1, App.IncludedCount(changes), "the word 魷魚 covers offset 1"); Eq(2, changes.Count, "于 stays as an optional row");
            App.Toggle(changes, 1);
            Check(changes[1].Included && !changes[0].Included, "choosing 于 drops the covering word");
            answers["p1"] = Answer("c1", "c1", 0.9);

            Snapshot gender = new Snapshot();
            gender.Positions.Add(new Position { Offset = 0, Items = new List<string> { "你", "妳", "擬" } });
            gender.Positions.Add(new Position { Offset = 1, Items = new List<string> { "它", "他", "她", "牠" } });
            gender.Current = Candidates.CurrentText(gender.Positions);
            string gst; List<JevClient.Question> gqs = App.BuildQuestions(gender, out gst);
            Dictionary<string, JevClient.Answer> ganswers = new Dictionary<string, JevClient.Answer>();
            ganswers["p0"] = Answer("c2", "c1", 0.1, "c2", 0.9);
            ganswers["p1"] = Answer("c3", "c1", 0.1, "c3", 0.9);
            List<Change> gchanges = App.Propose(gender, gqs, ganswers);
            Eq(1, gchanges.Count, "你→妳 is a gender guess and never proposed; 它→她 is");
            Eq("她", gchanges[0].Text, "它→她 kept");
            Check(App.IsGenderSwap("妳", "你") && App.IsGenderSwap("他", "她") && !App.IsGenderSwap("她", "它") && !App.IsGenderSwap("妳好", "你好"), "gender swap rule");

            answers["p0"] = Answer("c3", "c1", 0.3, "c3", 0.6);
            Eq(0, App.Propose(s, qs, answers).Count, "same text as current (由) is never a change");

            answers["p0"] = new JevClient.Answer();   // not Ok
            Eq(0, App.Propose(s, qs, answers).Count, "invalid answers are ignored");
        }

        static void TestKeys()
        {
            Check(App.DecideKey(Native.VK_TAB, true, false) == App.KeyDecision.Apply, "Tab applies a shown proposal");
            Check(App.DecideKey(Native.VK_TAB, false, false) == App.KeyDecision.CancelAndPass, "Tab without a proposal cancels and passes through");
            Check(App.DecideKey(Native.VK_TAB, true, true) == App.KeyDecision.CancelAndPass, "Tab while busy cancels and passes through");
            Check(App.DecideKey(Native.VK_ESCAPE, true, false) == App.KeyDecision.CancelAndSwallow, "Esc cancels and is swallowed");
            Check(App.DecideKey(0x41, true, false) == App.KeyDecision.CancelAndPass, "other keys cancel and go through to the IME");
            Check(App.DecideKey(0x31, true, false) == App.KeyDecision.Toggle && App.RowIndex(0x31) == 0, "digit 1 toggles row 0");
            Check(App.DecideKey(0x39, true, false) == App.KeyDecision.Toggle && App.RowIndex(0x39) == 8, "digit 9 toggles row 8");
            Check(App.DecideKey(Native.VK_NUMPAD1 + 2, true, false) == App.KeyDecision.Toggle && App.RowIndex(Native.VK_NUMPAD1 + 2) == 2, "numpad 3 toggles row 2");
            Check(App.DecideKey(0x30, true, false) == App.KeyDecision.CancelAndPass && App.RowIndex(0x30) == -1, "digit 0 is not a row");
            Check(App.DecideKey(0x31, false, false) == App.KeyDecision.CancelAndPass, "digits without a proposal go through to the IME");
            Check(App.DecideKey(0x31, true, true) == App.KeyDecision.CancelAndPass, "digits while busy go through");

            List<Change> two = new List<Change> { new Change { Offset = 0, Text = "魷魚", Replaced = "由於" }, new Change { Offset = 4, Text = "喫", Replaced = "吃" } };
            List<KeyValuePair<string, bool>> segs = Overlay.Segments("由於好好吃", two);
            Eq(3, segs.Count, "segments: change, plain, change"); Check(segs[0].Value && segs[0].Key == "魷魚" && !segs[1].Value && segs[1].Key == "好好" && segs[2].Value, "segment runs");
            two[1].Included = false;
            Eq("【魷魚】好好吃‹2›", Overlay.ProposedText("由於好好吃", two), "a switched-off alternative marks the original with its row number");
            List<Overlay.Run> runs = Overlay.Runs("由於好好吃", two);
            Eq(3, runs.Count, "applied, plain, optional"); Check(runs[2].Kind == Overlay.Run.Optional && runs[2].Text == "吃" && runs[2].Label == "2", "optional run");
            two.Add(new Change { Offset = 4, Text = "痴", Replaced = "吃", Included = false });
            Eq("【魷魚】好好吃‹2,3›", Overlay.ProposedText("由於好好吃", two), "several alternatives at one position list every row");
            two[0].Included = false;
            Eq("由於‹1›好好吃‹2,3›", Overlay.ProposedText("由於好好吃", two), "nothing applied: both spans are marked, sentence unchanged");
        }

        // 留言妃與 → 流言蜚語 needs two rounds: 蜚語 only beats 妃與 once 與 is gone, and 流言 only once the tail reads 蜚語.
        static void TestRefine()
        {
            Snapshot s = new Snapshot();
            s.Positions.Add(new Position { Offset = 0, Items = new List<string> { "留言", "流言", "留", "流" } });
            s.Positions.Add(new Position { Offset = 1, Items = new List<string> { "言", "嚴" } });
            s.Positions.Add(new Position { Offset = 2, Items = new List<string> { "妃與", "妃", "非", "蜚語", "蜚", "飛" } });
            s.Positions.Add(new Position { Offset = 3, Items = new List<string> { "與", "語", "雨" } });
            s.Current = Candidates.CurrentText(s.Positions);
            Eq("留言妃與", s.Current, "fixture");
            int round = 0;
            List<string> keepIds = new List<string>();
            Func<string, List<JevClient.Question>, Dictionary<string, JevClient.Answer>> ask = delegate(string state, List<JevClient.Question> qs)
            {
                round++;
                Dictionary<string, JevClient.Answer> answers = new Dictionary<string, JevClient.Answer>();
                foreach (JevClient.Question q in qs)
                {
                    keepIds.Add(round + ":" + q.Id + "=" + q.KeepId);
                    if (round == 1 && Base(q.Id) == "p2") answers[q.Id] = Answer("c4", q.KeepId, 0.1, "c4", 0.8);        // 蜚語
                    else if (round == 2 && Base(q.Id) == "p0") answers[q.Id] = Answer("c2", q.KeepId, 0.1, "c2", 0.9);   // 流言
                    else answers[q.Id] = Answer(q.KeepId, q.KeepId, 0.9);
                }
                return answers;
            };
            List<Change> changes = App.Refine(s, ask, 3);
            Eq(3, round, "two productive rounds plus one that finds nothing");
            Eq(2, App.IncludedCount(changes), "two changes applied"); Eq(2, changes.Count, "no optional rows from these answers");
            Eq("流言蜚語", App.ApplyChanges(s.Current, changes), "final sentence");
            Eq("妃與", changes[1].Replaced, "replaced text is relative to the original composition");
            Check(keepIds.Contains("2:p2=c4"), "in round 2 the keep option at offset 2 is the accepted 蜚語, not the IME's c1");

            round = 0;
            ask = delegate(string state, List<JevClient.Question> qs)
            {
                round++;
                Dictionary<string, JevClient.Answer> answers = new Dictionary<string, JevClient.Answer>();
                foreach (JevClient.Question q in qs)
                {
                    if (round == 1 && Base(q.Id) == "p2") answers[q.Id] = Answer("c4", q.KeepId, 0.1, "c4", 0.8);
                    else if (round == 2 && Base(q.Id) == "p2") answers[q.Id] = Answer("c1", q.KeepId, 0.1, "c1", 0.9);   // back to 妃與
                    else answers[q.Id] = Answer(q.KeepId, q.KeepId, 0.9);
                }
                return answers;
            };
            Eq(0, App.IncludedCount(App.Refine(s, ask, 3)), "a reverted change leaves nothing applied");
            Check(App.Refine(s, delegate(string st, List<JevClient.Question> qs) { return null; }, 3) == null, "first request failing yields null");
        }

        // 在是一次 → 再試一次: per-position answers accept nothing (再是一次 / 在試一次 both look odd), the pair question
        // offers 再試一次 and wins.
        static void TestPairStage()
        {
            Snapshot s = new Snapshot();
            s.Positions.Add(new Position { Offset = 0, Items = new List<string> { "在", "再", "載", "栽" } });
            s.Positions.Add(new Position { Offset = 1, Items = new List<string> { "是", "試", "事", "市" } });
            s.Positions.Add(new Position { Offset = 2, Items = new List<string> { "一次", "一", "衣" } });
            s.Positions.Add(new Position { Offset = 3, Items = new List<string> { "次", "刺", "賜" } });
            s.Current = Candidates.CurrentText(s.Positions);
            List<string> asked = new List<string>();
            Func<string, List<JevClient.Question>, Dictionary<string, JevClient.Answer>> ask = delegate(string state, List<JevClient.Question> qs)
            {
                Dictionary<string, JevClient.Answer> answers = new Dictionary<string, JevClient.Answer>();
                foreach (JevClient.Question q in qs)
                {
                    if (Base(q.Id) == "pair")
                    {
                        List<string> ids = new List<string>(); foreach (KeyValuePair<string, string> o in q.Options) ids.Add(o.Key + "=" + o.Value);
                        if (q.Id == "pair") asked.Add(string.Join(" ", ids.ToArray()));
                        answers[q.Id] = Answer("d0", "keep", 0.1, "s0", 0.05, "s1", 0.05, "d0", 0.8);   // 再試一次
                    }
                    else if (Base(q.Id) == "p0") answers[q.Id] = Answer(q.KeepId, q.KeepId, 0.6, "c2", 0.3);   // 再 plausible but not convincing alone
                    else if (Base(q.Id) == "p1") answers[q.Id] = Answer(q.KeepId, q.KeepId, 0.55, "c2", 0.35); // 試 likewise
                    else answers[q.Id] = Answer(q.KeepId, q.KeepId, 0.95);
                }
                return answers;
            };
            List<Change> changes = App.Refine(s, ask, 3);
            Eq(1, asked.Count, "pair question asked once");
            Check(asked[0].Contains("keep=在是一次") && asked[0].Contains("s0=再是一次") && asked[0].Contains("s1=在試一次") && asked[0].Contains("d0=再試一次"), "options: keep, each alone, both together: " + asked[0]);
            Eq(2, App.IncludedCount(changes), "both characters applied");
            Eq("再試一次", App.ApplyChanges(s.Current, changes.FindAll(delegate(Change c) { return c.Included; })), "final sentence");

            // The pair question only runs when nothing was accepted, and a weak pair answer changes nothing.
            asked.Clear();
            ask = delegate(string state, List<JevClient.Question> qs)
            {
                Dictionary<string, JevClient.Answer> answers = new Dictionary<string, JevClient.Answer>();
                foreach (JevClient.Question q in qs)
                {
                    if (Base(q.Id) == "pair") { if (q.Id == "pair") asked.Add(q.Id); answers[q.Id] = Answer("d0", "keep", 0.45, "d0", 0.5); }
                    else if (Base(q.Id) == "p0") answers[q.Id] = Answer(q.KeepId, q.KeepId, 0.6, "c2", 0.3);
                    else if (Base(q.Id) == "p1") answers[q.Id] = Answer(q.KeepId, q.KeepId, 0.55, "c2", 0.35);
                    else answers[q.Id] = Answer(q.KeepId, q.KeepId, 0.95);
                }
                return answers;
            };
            changes = App.Refine(s, ask, 3);
            Eq(1, asked.Count, "pair question asked"); Eq(0, App.IncludedCount(changes), "0.5 against 0.45 is not convincing");
            Eq(2, changes.Count, "the two alternatives stay as optional rows");

            // Characters the IME converted as one long word (羅密歐) are left out of the pair question.
            Snapshot name = new Snapshot();
            name.Positions.Add(new Position { Offset = 0, Items = new List<string> { "我", "婐" } });
            name.Positions.Add(new Position { Offset = 1, Items = new List<string> { "的", "得" } });
            name.Positions.Add(new Position { Offset = 2, Items = new List<string> { "羅密歐", "羅", "蘿" } });
            name.Positions.Add(new Position { Offset = 3, Items = new List<string> { "密", "蜜" } });
            name.Positions.Add(new Position { Offset = 4, Items = new List<string> { "歐", "鷗" } });
            name.Current = Candidates.CurrentText(name.Positions);
            Check(!App.InsideImeWord(name, 1) && App.InsideImeWord(name, 2) && App.InsideImeWord(name, 3) && App.InsideImeWord(name, 4), "offsets 2–4 belong to the IME word");
            asked.Clear();
            ask = delegate(string state, List<JevClient.Question> qs)
            {
                Dictionary<string, JevClient.Answer> answers = new Dictionary<string, JevClient.Answer>();
                foreach (JevClient.Question q in qs)
                {
                    if (Base(q.Id) == "pair") { if (q.Id == "pair") asked.Add(q.Id); answers[q.Id] = Answer("keep", "keep", 1.0); }
                    else if (Base(q.Id) == "p2") answers[q.Id] = Answer(q.KeepId, q.KeepId, 0.6, "c3", 0.4);   // 蘿
                    else if (Base(q.Id) == "p3") answers[q.Id] = Answer(q.KeepId, q.KeepId, 0.55, "c2", 0.45); // 蜜
                    else answers[q.Id] = Answer(q.KeepId, q.KeepId, 0.95);
                }
                return answers;
            };
            changes = App.Refine(name, ask, 3);
            Eq(0, asked.Count, "no pair question: the only candidates sit inside the IME's word");
            Eq(0, App.IncludedCount(changes), "name untouched");
        }

        // 約會要戴保險套: the second slot wins 0.9 in either order; averaging both orders leaves a balanced pair.
        static void TestBothOrders()
        {
            JevClient.Answer forward = Answer("c2", "c1", 0.07, "c2", 0.93);
            JevClient.Answer reversed = Answer("c1", "c1", 0.88, "c2", 0.12);
            JevClient.Answer avg = App.Average(forward, reversed);
            Check(avg.Ok && Math.Abs(avg.Probabilities["c1"] - 0.475) < 1e-9 && Math.Abs(avg.Probabilities["c2"] - 0.525) < 1e-9 && avg.Choice == "c2", "averaged distribution");
            Check(App.Average(forward, new JevClient.Answer()) == forward, "a failed mirror falls back to the original");
            Check(!App.Average(null, null).Ok, "nothing usable is not Ok");

            List<string> seen = new List<string>();
            Dictionary<string, JevClient.Answer> merged = App.AskBothOrders(delegate(string state, List<JevClient.Question> qs)
            {
                Dictionary<string, JevClient.Answer> answers = new Dictionary<string, JevClient.Answer>();
                foreach (JevClient.Question q in qs) { seen.Add(q.Id + ":" + q.Options[0].Key); answers[q.Id] = q.Id.EndsWith("m") ? reversed : forward; }
                return answers;
            }, "", new List<JevClient.Question> { new JevClient.Question { Id = "p3", Offset = 3, Options = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("c1", "約會要戴保險套"), new KeyValuePair<string, string>("c2", "約會要帶保險套") } } });
            Eq("p3:c1 p3m:c2", string.Join(" ", seen.ToArray()), "original and mirrored order sent together");
            Eq(1, merged.Count, "merged back to the original ids");
            Check(Math.Abs(merged["p3"].Probabilities["c2"] - 0.525) < 1e-9, "merged probabilities");
        }

        static void TestProposedText()
        {
            List<Change> changes = new List<Change> { new Change { Offset = 0, Text = "魷魚", Replaced = "由於" } };
            Eq("【魷魚】好好吃", Overlay.ProposedText("由於好好吃", changes), "included change is marked");
            changes[0].Included = false;
            Eq("由於‹1›好好吃", Overlay.ProposedText("由於好好吃", changes), "excluded change leaves the sentence but marks the span with its row number");
        }
    }

    // Live prompt evaluation: for each sentence, pretend the IME chose the wrong homophone (should be fixed) and the
    // right one (should be left alone). Uses the app's own BuildQuestions/Propose so the prompt under test is the real one.
    static class JevEval
    {
        class Case { public string Sentence; public int Offset; public string Correct, Wrong; public string[] Extras; }
        static Case C(string sentence, string correct, string wrong, params string[] extras)
        {
            int offset = sentence.IndexOf(correct);
            if (offset < 0) throw new ArgumentException(correct + " not in " + sentence);
            return new Case { Sentence = sentence, Offset = offset, Correct = correct, Wrong = wrong, Extras = extras };
        }

        // Extras approximate the rest of the IME's first page for that reading. Correct and wrong words share the
        // exact Bopomofo reading including tone, otherwise the IME would never offer the wrong one.
        static readonly Case[] Cases = {
            // 8 homophone pairs in two contexts each (the original set)
            C("夜市的烤魷魚好好吃。", "魷魚", "由於", "由", "游", "遊", "油", "尤", "猶", "郵"),
            C("由於下雨，比賽延期了。", "由於", "魷魚", "由", "游", "遊", "油", "尤", "猶", "郵"),
            C("每個公民都有投票的權利。", "權利", "權力", "全力", "權", "全", "泉", "拳", "詮"),
            C("主管有決定預算的權力。", "權力", "權利", "全力", "權", "全", "泉", "拳", "詮"),
            C("上班時間先處理公事。", "公事", "公式", "公示", "攻勢", "工事", "共事", "公", "工"),
            C("這道數學題要套用公式。", "公式", "公事", "公示", "攻勢", "工事", "共事", "公", "工"),
            C("這份禮物對我很有意義。", "意義", "異議", "一意", "意", "異", "義", "易", "一"),
            C("如果對提案有異議，請提出來。", "異議", "意義", "一意", "意", "異", "義", "易", "一"),
            C("等了三個小時，車子終於來了。", "終於", "忠於", "中於", "終", "忠", "中", "鐘", "鍾"),
            C("他始終忠於自己的信念。", "忠於", "終於", "中於", "終", "忠", "中", "鐘", "鍾"),
            C("下週就是期中考了。", "期中", "其中", "其", "期", "齊", "旗", "棋", "奇"),
            C("這十本書，其中三本我讀過。", "其中", "期中", "其", "期", "齊", "旗", "棋", "奇"),
            C("這項手術需要熟練的技術。", "技術", "計數", "記述", "技", "計", "記", "繼", "既"),
            C("這個程式負責計數，統計進場人數。", "計數", "技術", "記述", "技", "計", "記", "繼", "既"),
            C("視力檢查後，醫生說我有近視。", "近視", "進士", "近", "進", "盡", "晉", "禁", "勁"),
            C("他考中進士後，進入朝廷任職。", "進士", "近視", "近", "進", "盡", "晉", "禁", "勁"),
            // Reported in use
            C("我喜歡你。", "歡", "懽", "讙", "獾", "驩", "還", "環"),
            C("公事公辦。", "辦", "办", "半", "伴", "扮", "拌", "辨", "瓣"),
            C("在再不分。", "再", "在", "載", "栽", "災", "宰", "仔", "債"),
            C("在再不分。", "不分", "部份", "部分", "不", "部", "布", "步", "佈"),
            // 的／得、在／再、作／做
            C("他跑得很快。", "得", "的", "德", "地", "底"),
            C("這是我的書。", "的", "得", "德", "地", "底"),
            C("我明天再去一次。", "再", "在", "載", "栽", "災", "宰"),
            C("請再說一遍。", "再", "在", "載", "栽", "災", "宰"),
            C("他現在在家裡休息。", "在家", "再家", "在", "再", "載", "栽"),
            C("這份工作很辛苦。", "工作", "工做", "作", "做", "坐", "座", "昨"),
            C("我今天要做蛋糕。", "做", "作", "坐", "座", "昨", "酢"),
            // 已／以、須／需、帶／戴、製／制、複／復、練／鍊
            C("我已經吃飽了。", "已經", "以經", "已", "以", "一", "依", "衣", "椅"),
            C("你可以先走。", "可以", "可已", "以", "已", "一", "依", "衣", "椅"),
            C("我們需要更多時間。", "需要", "須要", "需", "須", "虛", "徐", "噓"),
            C("使用前必須詳閱說明書。", "必須", "必需", "須", "需", "虛", "徐", "噓"),
            C("出門請記得帶雨傘。", "帶", "戴", "待", "代", "袋", "貸", "怠"),
            C("他今天戴了一頂帽子。", "戴", "帶", "待", "代", "袋", "貸", "怠"),
            C("這台手機是台灣製造的。", "製造", "制造", "製", "制", "智", "至", "治", "志"),
            C("公司推出新的管理制度。", "制度", "製度", "制", "製", "智", "至", "治", "志"),
            C("考試前要好好複習。", "複習", "復習", "複", "復", "父", "付", "富", "負"),
            C("他終於恢復健康了。", "恢復", "恢複", "復", "複", "父", "付", "富", "負"),
            C("每天都要練習彈鋼琴。", "練習", "鍊習", "練", "鍊", "煉", "戀", "鏈"),
            // 歷／曆、訂／定、隻／支、麵／面、進／近
            C("他有豐富的工作經歷。", "經歷", "經曆", "歷", "曆", "力", "立", "利", "例"),
            C("農曆新年快到了。", "農曆", "農歷", "曆", "歷", "力", "立", "利", "例"),
            C("我已經訂好餐廳了。", "訂", "定", "釘", "錠", "碇"),
            C("會議時間還沒確定。", "確定", "確訂", "定", "訂", "釘", "錠", "碇"),
            C("我養了一隻貓。", "隻", "支", "枝", "之", "知", "脂", "芝"),
            C("請借我一支筆。", "支", "隻", "枝", "之", "知", "脂", "芝"),
            C("我晚餐想吃麵。", "麵", "面", "緬", "勉", "免", "眠"),
            C("請進來坐。", "進", "近", "盡", "晉", "禁", "勁", "浸"),
            // 整詞同音選錯
            C("其實我不太懂。", "其實", "期實", "其", "期", "齊", "旗", "棋", "奇"),
            C("因為下雨，我們取消了行程。", "因為", "音為", "因", "音", "姻", "殷", "陰"),
            C("我不知道他去了哪裡。", "知道", "之道", "知", "之", "支", "隻", "枝", "脂"),
            C("他長得很像他爸爸。", "像", "象", "向", "相", "項", "巷", "橡"),
            C("動物園裡有一頭大象。", "象", "像", "向", "相", "項", "巷", "橡"),
            C("他是我的同學。", "同學", "童學", "同", "童", "銅", "桐", "瞳"),
            C("我希望明天是晴天。", "希望", "西望", "希", "西", "稀", "吸", "犧"),
            C("這件事情非常重要。", "重要", "中要", "重", "中", "眾", "種", "仲"),
            C("我記得那天下大雨。", "記得", "紀得", "記", "紀", "計", "技", "既"),
            C("謝謝你的幫忙。", "幫忙", "邦忙", "幫", "邦", "梆", "綁"),
            C("我們的關係很好。", "關係", "觀係", "關", "觀", "官", "冠", "棺"),
            C("這是一個誤會。", "誤會", "物會", "誤", "物", "務", "霧", "悟"),
            C("我喜歡聽音樂。", "音樂", "音岳", "樂", "岳", "月", "越", "悅", "躍"),
            C("請輸入你的密碼。", "密碼", "蜜碼", "密", "蜜", "祕", "秘", "泌", "覓"),
            C("檔案已經下載完成。", "下載", "下在", "載", "在", "再", "栽", "災"),
            C("我不太會玩電腦遊戲。", "遊戲", "游戲", "遊", "游", "由", "油", "尤"),
            C("今天天氣很好。", "天氣", "天器", "氣", "器", "汽", "棄", "泣"),
            C("他每天搭公車上班。", "公車", "公扯", "車", "扯", "徹", "撤"),
            C("記得按時吃藥。", "藥", "要", "耀", "鑰", "曜", "躍"),
            C("這家店賣新鮮的水果。", "賣", "麥", "脈", "邁"),
            C("學生們都在認真聽課。", "認真", "認針", "真", "針", "珍", "偵", "斟"),
            // 語境決定：同一組同音字，兩句各自正確，只有上下文能分辨
            C("做愛要戴保險套。", "戴", "帶", "待", "代", "袋", "貸"),
            C("出門要帶保險套。", "帶", "戴", "待", "代", "袋", "貸"),
            C("太陽很大，記得戴帽子。", "戴", "帶", "待", "代", "袋", "貸"),
            C("山上會冷，記得帶外套。", "帶", "戴", "待", "代", "袋", "貸"),
            C("這台電腦太舊了，它該換了。", "它", "他", "她", "牠", "塔", "踏"),
            C("弟弟回來了，他餓了。", "他", "它", "她", "牠", "塔", "踏"),
            C("我下午有三堂課。", "課", "顆", "克", "刻", "客", "恪"),
            C("我吃了一顆蘋果。", "顆", "課", "克", "刻", "客", "恪"),
            C("這件事很麻煩。", "事", "是", "試", "市", "視", "式"),
            C("他是我的老闆。", "是", "事", "試", "市", "視", "式"),
            C("讓我試試看。", "試", "是", "事", "市", "視", "式"),
            C("他長得像媽媽。", "像", "向", "象", "相", "項", "巷"),
            C("請向右轉。", "向", "像", "象", "相", "項", "巷"),
            C("車站離這裡很近。", "近", "進", "盡", "晉", "禁", "勁"),
            C("送你一枝玫瑰。", "枝", "支", "隻", "之", "知", "脂"),
            C("請回覆這封信。", "回覆", "回復", "複", "付", "富", "負"),
            C("這是紀念品。", "紀念", "記念", "計", "技", "既", "季"),
            C("這個計畫很重要。", "計畫", "記畫", "紀", "技", "既", "季"),
            C("我經過公園。", "經過", "精過", "驚", "晶", "京", "鯨"),
            C("他今天精神很好。", "精神", "經神", "驚", "晶", "京", "鯨"),
            C("氣球爆了。", "爆", "報", "抱", "豹", "暴", "曝"),
            C("下週一要交報告。", "報告", "爆告", "抱", "豹", "暴", "曝"),
            C("下午三點要開會。", "開會", "開繪", "惠", "慧", "匯", "彙"),
            C("她很擅長繪畫。", "繪畫", "會畫", "惠", "慧", "匯", "彙"),
            C("這個問題很難。", "問題", "問提", "堤", "啼", "蹄", "緹"),
            C("他提出一個好建議。", "提出", "題出", "堤", "啼", "蹄", "緹"),
            C("我要買兩張票。", "票", "漂", "瞟", "驃"),
            C("她今天很漂亮。", "漂亮", "票亮", "瞟", "驃"),
            C("下班後放鬆一下。", "放鬆", "放松", "嵩", "淞", "凇"),
            C("山上有很多松樹。", "松樹", "鬆樹", "嵩", "淞", "凇"),
            C("我喜歡吃餅乾。", "餅乾", "餅干", "甘", "杆", "肝", "竿"),
            C("請不要干擾我。", "干擾", "乾擾", "甘", "杆", "肝", "竿"),
            C("她是古代的皇后。", "皇后", "皇後", "候", "厚", "吼", "逅"),
            C("吃完飯之後去散步。", "之後", "之后", "候", "厚", "吼", "逅"),
            C("我們明天見面。", "見面", "見麵", "緬", "勉", "免", "眠"),
            C("遠方有一座山。", "座", "坐", "做", "作", "昨", "佐"),
            C("請坐，不要客氣。", "坐", "座", "做", "作", "昨", "佐"),
            C("公司有一百位員工。", "員工", "原工", "元", "園", "圓", "源"),
            C("這是失敗的原因。", "原因", "員因", "元", "園", "圓", "源"),
            C("這本書一百元。", "元", "員", "原", "園", "圓", "源"),
            C("我們去公園散步。", "公園", "公圓", "員", "原", "元", "源"),
            C("桌子是圓形的。", "圓形", "員形", "原", "元", "園", "源"),
            C("她戴了一條項鍊。", "項鍊", "項練", "戀", "煉", "鏈", "鍊"),
            C("他捐了一億元。", "億", "意", "憶", "易", "異", "義") };

        public static void Run()
        {
            string key = Settings.LoadKey();
            if (key == null) { Console.WriteLine("live evaluation skipped: no Jev API key saved"); return; }
            int fixedCount = 0, keptCount = 0;
            foreach (Case c in Cases)
            {
                string wrongSentence = c.Sentence.Substring(0, c.Offset) + c.Wrong + c.Sentence.Substring(c.Offset + c.Wrong.Length);
                List<string> wrongFirst = new List<string> { c.Wrong, c.Correct }; wrongFirst.AddRange(c.Extras);
                List<string> correctFirst = new List<string> { c.Correct, c.Wrong }; correctFirst.AddRange(c.Extras);
                List<Change> fix = Ask(wrongSentence, c.Offset, wrongFirst);
                List<Change> keep = Ask(c.Sentence, c.Offset, correctFirst);
                bool fixedOk = fix != null && App.IncludedCount(fix) == 1 && fix[0].Included && fix[0].Text == c.Correct;
                bool keptOk = keep != null && App.IncludedCount(keep) == 0;
                if (fixedOk) fixedCount++;
                if (keptOk) keptCount++;
                Console.WriteLine((fixedOk ? "✓" : "✗") + (keptOk ? "✓" : "✗") + "  " + c.Sentence + "  錯字起跑→" + Describe(fix) + "  正確起跑→" + Describe(keep));
            }
            Console.WriteLine("錯字起跑會修正 " + fixedCount + "/" + Cases.Length + "，正確起跑不改壞 " + keptCount + "/" + Cases.Length);
            RunMulti();
        }

        static string Describe(List<Change> changes)
        {
            if (changes == null) return "請求失敗";
            List<Change> on = new List<Change>(); List<Change> off = new List<Change>();
            foreach (Change c in changes) (c.Included ? on : off).Add(c);
            string extra = off.Count == 0 ? "" : "（可選：" + string.Join("、", off.ConvertAll(delegate(Change c) { return c.Text + " " + (c.Probability * 100).ToString("0") + "%"; }).ToArray()) + "）";
            if (on.Count == 0) return "不改" + extra;
            return "改成「" + on[0].Text + "」" + (on[0].Probability * 100).ToString("0") + "%" + extra;
        }

        // Builds the snapshot exactly as a harvest would (one position carrying the IME page) and runs the real pipeline.
        static List<Change> Ask(string current, int offset, List<string> page)
        {
            Snapshot s = new Snapshot { Current = current };
            s.Positions.Add(new Position { Offset = offset, Items = page });
            return Refine(s);
        }

        static List<Change> Refine(Snapshot s) { return Refine(s, false); }

        static List<Change> Refine(Snapshot s, bool verbose)
        {
            string key = Settings.LoadKey(); string error = null; int round = 0;
            List<Change> changes = App.Refine(s, delegate(string state, List<JevClient.Question> questions)
            {
                round++;
                if (verbose)
                {
                    Console.WriteLine("      ---- round " + round + " request ----");
                    Console.WriteLine("      state: " + state.Replace("\n", " / "));
                    foreach (JevClient.Question q in questions)
                    {
                        if (q.Id.EndsWith("m")) continue;
                        List<string> opts = new List<string>(); foreach (KeyValuePair<string, string> o in q.Options) opts.Add(o.Key + "=" + o.Value);
                        Console.WriteLine("      " + q.Id + " (keep=" + q.KeepId + "): " + q.Instructions);
                        Console.WriteLine("          " + string.Join(" | ", opts.ToArray()));
                    }
                }
                Dictionary<string, JevClient.Answer> answers = JevClient.AskSync(key, state, questions, 15000, out error);
                if (verbose && answers != null)
                {
                    StringBuilder sb = new StringBuilder("      round " + round + ":");
                    foreach (JevClient.Question q in questions)
                    {
                        if (q.Id.EndsWith("m")) continue;
                        JevClient.Answer a; if (!answers.TryGetValue(q.Id, out a) || !a.Ok) continue;
                        double p, keep; a.Probabilities.TryGetValue(a.Choice, out p); a.Probabilities.TryGetValue(q.KeepId, out keep);
                        if (a.Choice == q.KeepId) continue;
                        string text = null; foreach (KeyValuePair<string, string> o in q.Options) if (o.Key == a.Choice) text = o.Value;
                        sb.Append(" " + q.Id + "→" + text + " " + p.ToString("0.00") + "(原 " + keep.ToString("0.00") + ")");
                    }
                    Console.WriteLine(sb.ToString());
                }
                return answers;
            }, App.MaxRounds);
            if (changes == null) Console.WriteLine("  request failed: " + error);
            return changes;
        }

        // Whole compositions through the real pipeline (iteration included).
        public static void RunMulti()
        {
            // Two interacting errors: the IME gave 留言妃與 for 流言蜚語 (reported in use).
            Snapshot s = new Snapshot();
            s.Positions.Add(new Position { Offset = 0, Items = new List<string> { "留言", "流言", "留", "流", "劉", "柳" } });
            s.Positions.Add(new Position { Offset = 1, Items = new List<string> { "言", "嚴", "顏", "研", "鹽" } });
            s.Positions.Add(new Position { Offset = 2, Items = new List<string> { "妃與", "妃", "非", "飛", "蜚語", "菲", "啡", "蜚" } });
            s.Positions.Add(new Position { Offset = 3, Items = new List<string> { "與", "語", "雨", "宇", "羽", "予" } });
            Whole(s, "流言蜚語", "整句多錯");

            // Correct already, with a transliterated name: the IME gave 我愛你你是我的羅密歐 and Jev turned it into 我噯妳你是我的蘿蜜歐 (reported in use).
            s = new Snapshot();
            s.Positions.Add(new Position { Offset = 0, Items = new List<string> { "我", "婐" } });
            s.Positions.Add(new Position { Offset = 1, Items = new List<string> { "愛", "噯", "艾", "礙", "隘", "曖", "唉", "璦", "嗌" } });
            s.Positions.Add(new Position { Offset = 2, Items = new List<string> { "你", "妳", "擬", "旎", "昵", "伲" } });
            s.Positions.Add(new Position { Offset = 3, Items = new List<string> { "你", "妳", "擬", "旎", "昵", "伲" } });
            s.Positions.Add(new Position { Offset = 4, Items = new List<string> { "是", "事", "市", "試", "視", "式", "世", "適", "釋" } });
            s.Positions.Add(new Position { Offset = 5, Items = new List<string> { "我", "婐" } });
            s.Positions.Add(new Position { Offset = 6, Items = new List<string> { "的", "得", "地", "德", "底" } });
            s.Positions.Add(new Position { Offset = 7, Items = new List<string> { "羅密歐", "羅", "蘿", "螺", "邏", "囉", "鑼", "騾", "籮" } });
            s.Positions.Add(new Position { Offset = 8, Items = new List<string> { "密", "蜜", "祕", "秘", "泌", "覓", "謐", "冪", "蜜蜂" } });
            s.Positions.Add(new Position { Offset = 9, Items = new List<string> { "歐", "鷗", "甌", "毆", "謳", "區", "偶", "藕" } });
            Whole(s, "我愛你你是我的羅密歐", "音譯名＋性別");

            // Two adjacent errors with no word candidate to rescue them: the IME gave 在是一次 for 再試一次 (reported in use).
            s = new Snapshot();
            s.Positions.Add(new Position { Offset = 0, Items = new List<string> { "在", "再", "載", "栽", "災", "宰" } });
            s.Positions.Add(new Position { Offset = 1, Items = new List<string> { "是", "試", "事", "市", "視", "式", "世", "適" } });
            s.Positions.Add(new Position { Offset = 2, Items = new List<string> { "一次", "一", "衣", "依", "醫", "伊" } });
            s.Positions.Add(new Position { Offset = 3, Items = new List<string> { "次", "刺", "賜", "伺", "廁" } });
            Whole(s, "再試一次", "相鄰兩錯");
            RunMulti2();
        }

        public static void RunMulti2()
        {
            // 想在去一次芮氏: two independent errors; 芮氏 is a dictionary word (芮氏規模) the IME preferred over 瑞士.
            Snapshot s = new Snapshot();
            s.Positions.Add(new Position { Offset = 0, Items = new List<string> { "想", "響", "享", "饗", "餉", "响" } });
            s.Positions.Add(new Position { Offset = 1, Items = new List<string> { "在", "再", "載", "栽", "災", "宰" } });
            s.Positions.Add(new Position { Offset = 2, Items = new List<string> { "去", "趣", "覷", "娶", "闃" } });
            s.Positions.Add(new Position { Offset = 3, Items = new List<string> { "一次", "一", "衣", "依", "醫", "伊" } });
            s.Positions.Add(new Position { Offset = 4, Items = new List<string> { "次", "刺", "賜", "伺", "廁" } });
            s.Positions.Add(new Position { Offset = 5, Items = new List<string> { "芮氏", "瑞士", "瑞", "芮", "銳", "睿", "叡", "枘" } });
            s.Positions.Add(new Position { Offset = 6, Items = new List<string> { "氏", "士", "是", "事", "試", "市", "視", "式", "世" } });
            Whole(s, "想再去一次瑞士", "兩處獨立錯字");
        }

        static void Whole(Snapshot s, string expected, string label)
        {
            s.Current = Candidates.CurrentText(s.Positions);
            List<Change> changes = Refine(s, true);
            string result = changes == null ? "請求失敗" : App.ApplyChanges(s.Current, changes.FindAll(delegate(Change c) { return c.Included; }));
            Console.WriteLine((result == expected ? "✓" : "✗") + "   " + label + "：" + s.Current + " → " + result);
        }
    }
}
