// The preview. Nothing is written that has not been through this window.
//
// One row per change, showing the line with the old text struck through in the
// down color and the new text beside it in the up color. The inline diff shape
// reads faster than a pair of before-and-after lines and takes half the
// height:
//
// charcheck:spelling-off
//   Every theme sets a [colour] color for each surface.
// charcheck:spelling-on
//
// Every row carries a checkbox, and a file header carries one that toggles its
// whole group. Files that cannot be written appear too, greyed, with the
// sentence explaining why. Hiding a refusal would be the worst outcome here:
// the person would come away believing a file had been changed.
//
// C# 5 only (in-box csc).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace RSFind
{
    public class ReplaceDialog : Form
    {
        // Above this, the preview stops being a preview. Rather than show a
        // sample and imply the rest was reviewed, the run is refused and the
        // person is asked to narrow it - which is also the honest advice for
        // a replace across forty thousand lines.
        public const int MaxPreviewChanges = 5000;

        struct Row
        {
            public bool IsFile;
            public int Plan;
            public int Change;
        }

        readonly List<ReplacePlan> _plans;
        readonly List<Row> _rows = new List<Row>();
        readonly string _query;
        readonly string _replacement;

        ListView _list;
        Label _header;
        ThemedButton _apply, _cancel, _all, _none;
        Font _mono, _monoBold, _monoStrike;
        double _cell = 8;

        public ReplaceDialog(List<ReplacePlan> plans, string query, string replacement)
        {
            _plans = plans;
            _query = query;
            _replacement = replacement;

            Text = "Preview Replace";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(Dpi.S(940), Dpi.S(560));
            MinimumSize = new Size(Dpi.S(620), Dpi.S(360));
            BackColor = Th.T.Panel;
            ForeColor = Th.T.Txt;

            BuildFonts();
            BuildControls();
            Rebuild();
            UpdateHeader();

            Resize += delegate { LayoutControls(); };
            Shown += delegate { OsChrome.ApplyTitleBar(this); LayoutControls(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_mono != null) _mono.Dispose();
                if (_monoBold != null) _monoBold.Dispose();
                if (_monoStrike != null) _monoStrike.Dispose();
            }
            base.Dispose(disposing);
        }

        void BuildFonts()
        {
            _mono = new Font("Consolas", 9f, FontStyle.Regular);
            if (!_mono.Name.Equals("Consolas", StringComparison.OrdinalIgnoreCase))
            {
                _mono.Dispose();
                _mono = new Font(FontFamily.GenericMonospace, 9f, FontStyle.Regular);
            }
            _monoBold = new Font(_mono, FontStyle.Bold);
            _monoStrike = new Font(_mono, FontStyle.Strikeout);
            const int Sample = 200;
            _cell = TextRenderer.MeasureText(new string('0', Sample), _mono,
                new Size(int.MaxValue, int.MaxValue), Flags).Width / (double)Sample;
            if (_cell < 1) _cell = 1;
        }

        void BuildControls()
        {
            _header = new Label();
            _header.AutoSize = false;
            _header.BackColor = Th.T.Panel;
            _header.ForeColor = Th.T.Txt;
            Controls.Add(_header);

            _list = new ListView();
            _list.View = View.Details;
            _list.HeaderStyle = ColumnHeaderStyle.None;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.VirtualMode = true;
            _list.OwnerDraw = true;
            _list.BorderStyle = BorderStyle.None;
            _list.HideSelection = false;
            _list.BackColor = Th.T.Input;
            _list.ForeColor = Th.T.Txt;
            _list.Font = _mono;
            _list.Columns.Add("Change", 100);
            ImageList spacer = new ImageList();
            spacer.ImageSize = new Size(1, Math.Max(1, _mono.Height + Dpi.S(4)));
            _list.SmallImageList = spacer;
            _list.RetrieveVirtualItem += delegate(object s, RetrieveVirtualItemEventArgs e)
            {
                e.Item = new ListViewItem(string.Empty);
            };
            _list.DrawItem += OnDrawItem;
            _list.DrawSubItem += delegate { };
            _list.MouseDown += OnListMouseDown;
            _list.KeyDown += OnListKeyDown;
            Controls.Add(_list);

            _all = NewButton("Select All", false);
            _all.Click += delegate { SetAll(true); };
            _none = NewButton("Select None", false);
            _none.Click += delegate { SetAll(false); };

            _apply = NewButton("Apply", true);
            _apply.DialogResult = DialogResult.OK;
            _cancel = NewButton("Cancel", false);
            _cancel.DialogResult = DialogResult.Cancel;

            AcceptButton = null;   // Enter must not write files by accident
            CancelButton = _cancel;
        }

        ThemedButton NewButton(string text, bool primary)
        {
            ThemedButton b = new ThemedButton();
            b.Text = text;
            b.Primary = primary;
            Controls.Add(b);
            return b;
        }

        void LayoutControls()
        {
            if (_list == null) return;
            int pad = Dpi.S(12);
            int rowH = Dpi.S(28);
            int buttonW = Dpi.S(104);

            _header.SetBounds(pad, pad, ClientSize.Width - pad * 2, Dpi.S(38));

            int listTop = _header.Bottom + Dpi.S(6);
            int listBottom = ClientSize.Height - pad - rowH - Dpi.S(8);
            _list.SetBounds(pad, listTop, ClientSize.Width - pad * 2,
                            Math.Max(Dpi.S(80), listBottom - listTop));
            FitColumn();

            int y = ClientSize.Height - pad - rowH;
            _all.SetBounds(pad, y, buttonW, rowH);
            _none.SetBounds(_all.Right + Dpi.S(8), y, buttonW, rowH);
            _cancel.SetBounds(ClientSize.Width - pad - buttonW, y, buttonW, rowH);
            _apply.SetBounds(_cancel.Left - Dpi.S(8) - Dpi.S(160), y, Dpi.S(160), rowH);
        }

        void FitColumn()
        {
            int w = _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
            if (w < Dpi.S(60)) w = Dpi.S(60);
            if (_list.Columns[0].Width != w) _list.Columns[0].Width = w;
        }

        // ---- rows ------------------------------------------------------------

        void Rebuild()
        {
            _rows.Clear();
            for (int p = 0; p < _plans.Count; p++)
            {
                Row header = new Row();
                header.IsFile = true;
                header.Plan = p;
                header.Change = -1;
                _rows.Add(header);

                ReplacePlan plan = _plans[p];
                if (plan.Refusal != null) continue;
                for (int c = 0; c < plan.Changes.Count; c++)
                {
                    Row r = new Row();
                    r.Plan = p;
                    r.Change = c;
                    _rows.Add(r);
                }
            }
            _list.VirtualListSize = _rows.Count;
            FitColumn();
            _list.Invalidate();
        }

        void UpdateHeader()
        {
            int changes = 0, files = 0, refused = 0;
            foreach (ReplacePlan p in _plans)
            {
                if (p.Refusal != null) { refused++; continue; }
                int n = p.SelectedCount;
                changes += n;
                if (n > 0) files++;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("Replacing \"").Append(_query).Append("\" with \"")
              .Append(_replacement).Append("\".");
            sb.Append("\r\n");
            sb.Append(changes.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append(changes == 1 ? " change selected in " : " changes selected in ");
            sb.Append(files.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append(files == 1 ? " file" : " files");
            if (refused > 0)
            {
                sb.Append(". ").Append(refused.ToString("N0", CultureInfo.InvariantCulture));
                sb.Append(refused == 1 ? " file cannot be written" : " files cannot be written");
                sb.Append(" and is listed below with the reason");
            }
            _header.Text = sb.ToString();

            _apply.Text = changes == 0
                ? "Apply"
                : "Apply to " + files.ToString("N0", CultureInfo.InvariantCulture)
                  + (files == 1 ? " File" : " Files");
            _apply.Enabled = changes > 0;
        }

        void SetAll(bool selected)
        {
            foreach (ReplacePlan p in _plans)
            {
                if (p.Refusal != null) continue;
                foreach (ReplaceChange c in p.Changes) c.Selected = selected;
            }
            UpdateHeader();
            _list.Invalidate();
        }

        // ---- painting ----------------------------------------------------------

        static readonly TextFormatFlags Flags =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
            TextFormatFlags.SingleLine;

        int Cells(int n) { return (int)Math.Round(n * _cell); }

        int CheckSize { get { return Dpi.S(14); } }
        int CheckLeft(bool isFile) { return Dpi.S(6) + (isFile ? 0 : Dpi.S(16)); }

        void OnDrawItem(object sender, DrawListViewItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _rows.Count) return;
            Theme t = Th.T;
            Row row = _rows[e.ItemIndex];
            ReplacePlan plan = _plans[row.Plan];
            Graphics g = e.Graphics;
            Rectangle b = e.Bounds;

            bool selected = (e.State & ListViewItemStates.Selected) != 0;
            Color back = row.IsFile ? t.Panel2 : t.Input;
            if (selected) back = Th.Mix(back, t.Accent, 0.25);
            using (SolidBrush br = new SolidBrush(back)) g.FillRectangle(br, b);

            if (row.IsFile)
            {
                DrawFileRow(g, b, plan, t);
                return;
            }
            DrawChangeRow(g, b, plan, plan.Changes[row.Change], t);
        }

        void DrawFileRow(Graphics g, Rectangle b, ReplacePlan plan, Theme t)
        {
            int x = CheckLeft(true);
            if (plan.Refusal == null)
            {
                bool all = plan.SelectedCount == plan.Changes.Count && plan.Changes.Count > 0;
                bool some = plan.SelectedCount > 0;
                DrawCheck(g, b, x, all, some && !all, t);
            }
            x += CheckSize + Dpi.S(8);

            Color name = plan.Refusal == null ? t.Txt : t.TxtDim;
            TextRenderer.DrawText(g, plan.File.RelativePath, _monoBold,
                new Rectangle(x, b.Y, b.Right - x, b.Height), name, Flags);
            x += Cells(plan.File.RelativePath.Length) + Dpi.S(10);

            string tail;
            Color tailColor;
            if (plan.Refusal != null)
            {
                // The reason travels with the file, in the warning color, so
                // a refusal cannot be mistaken for a file with no matches.
                tail = "not replaced: " + plan.Refusal;
                tailColor = t.Warn;
            }
            else
            {
                tail = "(" + plan.SelectedCount.ToString(CultureInfo.InvariantCulture)
                     + " of " + plan.Changes.Count.ToString(CultureInfo.InvariantCulture)
                     + (plan.Changes.Count == 1 ? " change)" : " changes)");
                tailColor = t.TxtDim;
            }
            TextRenderer.DrawText(g, tail, _mono,
                new Rectangle(x, b.Y, b.Right - x - Dpi.S(6), b.Height), tailColor, Flags);
        }

        void DrawChangeRow(Graphics g, Rectangle b, ReplacePlan plan, ReplaceChange change, Theme t)
        {
            int x = CheckLeft(false);
            DrawCheck(g, b, x, change.Selected, false, t);
            x += CheckSize + Dpi.S(8);

            string gutter = "Line " + change.LineNumber.ToString(CultureInfo.InvariantCulture) + ": ";
            TextRenderer.DrawText(g, gutter, _mono,
                new Rectangle(x, b.Y, b.Right - x, b.Height), t.TxtDim, Flags);
            x += Cells(gutter.Length);

            // The inline diff: the line up to the match, the old text struck
            // through, the new text, then the rest of the line. The span comes
            // from the change itself rather than from diffing the two lines,
            // so the reader sees one whole word becoming another.
            string head = change.Before.Substring(0, change.MatchStart);
            string tail = change.Before.Substring(change.MatchStart + change.OldText.Length);

            Color body = change.Selected ? t.Txt : t.TxtDim;
            x += DrawAt(g, head, _mono, x, b, body);
            x += DrawAt(g, change.OldText, _monoStrike, x, b, t.Down);
            x += DrawAt(g, " ", _mono, x, b, body);
            x += DrawAt(g, change.NewText, _monoBold, x, b, t.Up);
            DrawAt(g, tail, _mono, x, b, body);
        }

        int DrawAt(Graphics g, string s, Font f, int x, Rectangle b, Color color)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int width = b.Right - Dpi.S(6) - x;
            if (width > 0)
                TextRenderer.DrawText(g, s, f, new Rectangle(x, b.Y, width, b.Height), color, Flags);
            return Cells(s.Length);
        }

        void DrawCheck(Graphics g, Rectangle b, int x, bool on, bool partial, Theme t)
        {
            int size = CheckSize;
            Rectangle box = new Rectangle(x, b.Y + (b.Height - size) / 2, size, size);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Draw.FillBorderRound(g, box, 3, on ? t.Accent : t.Input,
                                 on || partial ? t.Accent : t.Border);
            if (on)
            {
                float u = box.Width / 15f;
                using (Pen pen = new Pen(Th.OnColor(t.Accent), Math.Max(2f, 2f * u)))
                {
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    g.DrawLines(pen, new PointF[] {
                        new PointF(box.Left + 3 * u, box.Top + 7 * u),
                        new PointF(box.Left + 6 * u, box.Top + 10 * u),
                        new PointF(box.Left + 11 * u, box.Top + 4 * u)
                    });
                }
            }
            else if (partial)
            {
                // Some but not all of this file's changes are selected.
                using (SolidBrush br = new SolidBrush(t.Accent))
                    g.FillRectangle(br, box.Left + Dpi.S(3), box.Top + box.Height / 2 - Dpi.S(1),
                                    box.Width - Dpi.S(6), Dpi.S(2));
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        }

        // ---- interaction ---------------------------------------------------------

        void OnListMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ListViewItem item = _list.GetItemAt(e.X, e.Y);
            if (item == null || item.Index < 0 || item.Index >= _rows.Count) return;
            Toggle(item.Index);
        }

        void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Space) return;
            if (_list.SelectedIndices.Count == 0) return;
            Toggle(_list.SelectedIndices[0]);
            e.Handled = true;
        }

        // A click anywhere on the row toggles it. The checkbox is a hint, not
        // a hit target: this window is read carefully and clicked a lot, and
        // asking someone to hit a 14px box repeatedly is how mistakes happen.
        void Toggle(int index)
        {
            Row row = _rows[index];
            ReplacePlan plan = _plans[row.Plan];
            if (plan.Refusal != null) return;

            if (row.IsFile)
            {
                bool turnOn = plan.SelectedCount < plan.Changes.Count;
                foreach (ReplaceChange c in plan.Changes) c.Selected = turnOn;
            }
            else
            {
                ReplaceChange c = plan.Changes[row.Change];
                c.Selected = !c.Selected;
            }
            UpdateHeader();
            _list.Invalidate();
        }
    }
}
