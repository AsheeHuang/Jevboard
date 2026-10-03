using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace Jevboard
{
    // Floating card that never takes focus: WS_EX_NOACTIVATE + MA_NOACTIVATE on click, and only custom-painted
    // controls inside (nothing focusable), so clicks never move keyboard focus away from the editor.
    class Overlay : Form
    {
        public Action ApplyClicked, CancelClicked;

        static readonly Color Accent = Color.FromArgb(31, 91, 184), AccentSoft = Color.FromArgb(232, 240, 253);
        static readonly Color Ink = Color.FromArgb(33, 33, 33), Muted = Color.FromArgb(128, 128, 128), Border = Color.FromArgb(214, 214, 214);
        static readonly Font TitleFont = new Font("Microsoft JhengHei UI", 10f, FontStyle.Bold);
        static readonly Font BodyFont = new Font("Microsoft JhengHei UI", 13.5f);
        static readonly Font RowFont = new Font("Microsoft JhengHei UI", 10.5f);
        static readonly Font SmallFont = new Font("Microsoft JhengHei UI", 8.5f);

        readonly Label title = new Label();
        readonly SentenceLine current = new SentenceLine("目前"), proposed = new SentenceLine("建議");
        readonly FlowLayoutPanel rows = new FlowLayoutPanel();
        readonly FlowLayoutPanel footer = new FlowLayoutPanel();
        readonly Timer hintTimer = new Timer();
        string currentText = "";
        List<Change> changes = new List<Change>();

        public Overlay()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            BackColor = Color.White; Padding = new Padding(16, 12, 16, 12); AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = RowFont;
            title.AutoSize = true; title.Font = TitleFont; title.ForeColor = Accent; title.Margin = new Padding(0, 0, 0, 6);
            current.Margin = new Padding(0, 2, 0, 0); proposed.Margin = new Padding(0, 0, 0, 4);
            rows.FlowDirection = FlowDirection.TopDown; rows.AutoSize = true; rows.WrapContents = false; rows.Margin = new Padding(0, 2, 0, 4);
            footer.FlowDirection = FlowDirection.LeftToRight; footer.AutoSize = true; footer.WrapContents = false; footer.Margin = new Padding(0, 6, 0, 0);
            footer.Controls.Add(Chip("套用", true, delegate { if (ApplyClicked != null) ApplyClicked(); }));
            footer.Controls.Add(Chip("取消", false, delegate { if (CancelClicked != null) CancelClicked(); }));
            Label hints = new Label { AutoSize = true, Font = SmallFont, ForeColor = Muted, Text = "Tab 套用 ・ Esc 取消 ・ 數字鍵 切換該處", Margin = new Padding(10, 7, 0, 0) };
            footer.Controls.Add(hints);
            FlowLayoutPanel root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Location = new Point(Padding.Left, Padding.Top) };
            root.Controls.Add(title); root.Controls.Add(current); root.Controls.Add(proposed); root.Controls.Add(rows); root.Controls.Add(footer);
            Controls.Add(root);   // the form's Padding only applies to docked children, hence the explicit Location
            hintTimer.Interval = 2500; hintTimer.Tick += delegate { hintTimer.Stop(); HideOverlay(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                p.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                p.ClassStyle |= 0x20000;   // CS_DROPSHADOW
                return p;
            }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = new IntPtr(Native.MA_NOACTIVATE); return; }
            base.WndProc(ref m);
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width, Height), 12)) Region = new Region(path);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 12))
            using (Pen pen = new Pen(Border)) e.Graphics.DrawPath(pen, path);
            using (SolidBrush brush = new SolidBrush(Accent)) e.Graphics.FillRectangle(brush, 0, 0, 4, Height);   // accent edge
        }

        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        static Control Chip(string text, bool primary, EventHandler onClick)
        {
            ChipLabel chip = new ChipLabel(text, primary);
            chip.Click += onClick;
            return chip;
        }

        public bool HasProposal { get { return Visible && footer.Visible; } }

        // Transient message that hides itself.
        public void ShowHint(string text, Point anchor) { ShowStatus(text, anchor); hintTimer.Stop(); hintTimer.Start(); }

        // Status line only (while reading candidates / waiting for Jev).
        public void ShowStatus(string text, Point anchor)
        {
            hintTimer.Stop();
            title.Text = text; title.ForeColor = Muted;
            current.Visible = false; proposed.Visible = false; rows.Visible = false; footer.Visible = false; rows.Controls.Clear();
            Place(anchor);
        }
        public void ShowStatus(string text) { ShowStatus(text, Visible ? Location : Cursor.Position); }
        public void MoveTo(Point anchor) { Place(anchor); }
        public void SetStatus(string text) { title.Text = text; }
        public void AutoHide() { hintTimer.Stop(); hintTimer.Start(); }

        public void ShowSentence(string text, string statusText)
        {
            currentText = text;
            title.Text = statusText; title.ForeColor = Muted;
            current.Set(Plain(text)); current.Visible = true;
            proposed.Visible = false; rows.Visible = false; footer.Visible = false;
            PerformLayout();
        }

        // Proposal rows; a click or the row's digit toggles that change. The proposed line reflects included changes only.
        public void ShowProposal(string statusText, List<Change> proposal)
        {
            changes = proposal;
            rows.SuspendLayout(); rows.Controls.Clear();
            for (int i = 0; i < changes.Count; i++)
            {
                ChangeRow row = new ChangeRow(i + 1, changes[i]);
                int index = i;
                row.Click += delegate { ToggleRow(index); };
                rows.Controls.Add(row);
            }
            rows.ResumeLayout();
            title.Text = statusText; title.ForeColor = Accent;
            rows.Visible = true; footer.Visible = true; proposed.Visible = true;
            RefreshProposed();
            PerformLayout();
        }

        public void ToggleRow(int index)
        {
            if (!HasProposal || index < 0 || index >= changes.Count) return;
            App.Toggle(changes, index);
            foreach (Control row in rows.Controls) row.Invalidate();
            RefreshProposed();
        }

        public List<Change> IncludedChanges()
        {
            List<Change> list = new List<Change>();
            foreach (Change c in changes) if (c.Included) list.Add(c);
            return list;
        }

        void RefreshProposed() { proposed.Set(Segments(currentText, changes)); }

        // The sentence split into (text, isChange) runs with every included change applied.
        internal static List<KeyValuePair<string, bool>> Segments(string current, List<Change> changes)
        {
            List<KeyValuePair<string, bool>> segments = new List<KeyValuePair<string, bool>>();
            StringBuilder plain = new StringBuilder();
            int i = 0;
            while (i < current.Length)
            {
                Change hit = null;
                foreach (Change c in changes) if (c.Included && c.Offset == i) { hit = c; break; }
                if (hit != null)
                {
                    if (plain.Length > 0) { segments.Add(new KeyValuePair<string, bool>(plain.ToString(), false)); plain.Length = 0; }
                    segments.Add(new KeyValuePair<string, bool>(hit.Text, true));
                    i += hit.Replaced.Length;
                }
                else { plain.Append(current[i]); i++; }
            }
            if (plain.Length > 0) segments.Add(new KeyValuePair<string, bool>(plain.ToString(), false));
            return segments;
        }

        // Same content as Segments, with changes wrapped in 【】 (used by tests and logs-free diagnostics).
        internal static string ProposedText(string current, List<Change> changes)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, bool> seg in Segments(current, changes)) sb.Append(seg.Value ? "【" + seg.Key + "】" : seg.Key);
            return sb.ToString();
        }

        static List<KeyValuePair<string, bool>> Plain(string text)
        {
            List<KeyValuePair<string, bool>> one = new List<KeyValuePair<string, bool>>();
            one.Add(new KeyValuePair<string, bool>(text, false));
            return one;
        }

        void Place(Point anchor)
        {
            PerformLayout();
            Rectangle area = Screen.FromPoint(anchor).WorkingArea;
            int x = Math.Max(area.Left, Math.Min(anchor.X, area.Right - Width));
            int y = Math.Max(area.Top, Math.Min(anchor.Y, area.Bottom - Height));
            if (!Visible) { Location = new Point(x, y); Show(); }
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        public void HideOverlay() { hintTimer.Stop(); if (Visible) Hide(); rows.Controls.Clear(); changes = new List<Change>(); }

        // A sentence with a small gray prefix; changed runs are drawn bold on a soft accent background.
        class SentenceLine : Control
        {
            readonly string prefix;
            List<KeyValuePair<string, bool>> segments = new List<KeyValuePair<string, bool>>();
            static readonly Font Bold = new Font(BodyFont, FontStyle.Bold);
            public SentenceLine(string prefix)
            {
                this.prefix = prefix;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                TabStop = false; Font = BodyFont; Height = BodyFont.Height + 10;
            }
            public void Set(List<KeyValuePair<string, bool>> runs)
            {
                segments = runs;
                int width = Measure(prefix, SmallFont) + 10;
                foreach (KeyValuePair<string, bool> seg in segments) width += Measure(seg.Key, seg.Value ? Bold : BodyFont) + (seg.Value ? 8 : 0);
                Size = new Size(width + 4, BodyFont.Height + 10);
                Invalidate();
            }
            static int Measure(string text, Font font) { return TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding).Width; }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                int x = 0, mid = Height / 2;
                TextRenderer.DrawText(e.Graphics, prefix, SmallFont, new Point(x, mid - SmallFont.Height / 2), Muted, TextFormatFlags.NoPadding);
                x += Measure(prefix, SmallFont) + 10;
                foreach (KeyValuePair<string, bool> seg in segments)
                {
                    Font font = seg.Value ? Bold : BodyFont;
                    int w = Measure(seg.Key, font);
                    if (seg.Value)
                    {
                        using (SolidBrush brush = new SolidBrush(AccentSoft)) e.Graphics.FillRectangle(brush, x, 2, w + 8, Height - 4);
                        TextRenderer.DrawText(e.Graphics, seg.Key, font, new Point(x + 4, mid - font.Height / 2), Accent, TextFormatFlags.NoPadding);
                        x += w + 8;
                    }
                    else { TextRenderer.DrawText(e.Graphics, seg.Key, font, new Point(x, mid - font.Height / 2), Ink, TextFormatFlags.NoPadding); x += w; }
                }
            }
        }

        // One proposed change: a numbered badge, "第 N 字 原 → 新" and the probability; dimmed when excluded.
        class ChangeRow : Control
        {
            readonly int number; readonly Change change;
            public ChangeRow(int number, Change change)
            {
                this.number = number; this.change = change;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                TabStop = false; Cursor = Cursors.Hand; Font = RowFont; Margin = new Padding(0, 1, 0, 1);
                Size = new Size(TextRenderer.MeasureText(Caption(), RowFont).Width + 70, RowFont.Height + 12);
            }
            string Caption() { return "第 " + (change.Offset + 1) + " 字　" + change.Replaced + " → " + change.Text + "　" + (change.Probability * 100).ToString("0") + "%"; }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                bool on = change.Included;
                Rectangle badge = new Rectangle(2, (Height - 22) / 2, 22, 22);
                using (SolidBrush fill = new SolidBrush(on ? Accent : Color.White)) e.Graphics.FillEllipse(fill, badge);
                using (Pen ring = new Pen(on ? Accent : Muted)) e.Graphics.DrawEllipse(ring, badge);
                TextRenderer.DrawText(e.Graphics, number.ToString(), SmallFont, badge, on ? Color.White : Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(e.Graphics, Caption(), RowFont, new Point(32, (Height - RowFont.Height) / 2), on ? Ink : Muted, TextFormatFlags.NoPadding);
            }
        }

        // Clickable chip for the mouse; never focusable.
        class ChipLabel : Control
        {
            readonly bool primary;
            public ChipLabel(string text, bool primary)
            {
                this.primary = primary;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                TabStop = false; Cursor = Cursors.Hand; Text = text; Font = RowFont; Margin = new Padding(0, 0, 8, 0);
                Size = new Size(TextRenderer.MeasureText(text, RowFont).Width + 28, RowFont.Height + 12);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
                {
                    using (SolidBrush fill = new SolidBrush(primary ? Accent : Color.FromArgb(240, 240, 240))) e.Graphics.FillPath(fill, path);
                    if (!primary) using (Pen pen = new Pen(Border)) e.Graphics.DrawPath(pen, path);
                }
                TextRenderer.DrawText(e.Graphics, Text, RowFont, new Rectangle(0, 0, Width, Height), primary ? Color.White : Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }
}
