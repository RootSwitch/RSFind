// The results pane: a collapsible, per-file grouped list of hits.
//
// A header row per file carrying its hit count, indented "Line N: text" rows
// underneath, the match highlighted in place, and optional context lines
// around it. Grouping by file rather than listing hits flat is the whole
// reason this is a custom view: the question being asked is almost always
// "which file was it in", and a flat list makes the reader count.
//
// A ListView in VirtualMode, because the alternative is materializing an
// object per hit and a folder of session logs answers a common word with six
// figures of them. Virtual mode means the control asks for the rows it is
// about to paint and nothing else, so appending a file that matched 5,000
// times costs a size update rather than 5,000 allocations.
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
    public class ResultsView : ListView
    {
        enum RowKind { File, Line }

        // A flattened index into the tree. Rel is 0 for the matched line,
        // negative for a context line before it, positive for one after.
        struct Row
        {
            public RowKind Kind;
            public int File;
            public int Hit;
            public int Rel;
        }

        // Longer than this and only a window around the match is drawn. A
        // minified file or a base64 blob is one line of two million characters,
        // and asking TextRenderer to measure it once per repaint freezes the
        // window. The file still counts as a hit; the row just shows the part
        // that matters, with ellipses marking what was cut.
        const int MaxDrawnLine = 400;

        readonly List<FileHits> _files = new List<FileHits>();
        readonly List<bool> _collapsed = new List<bool>();
        readonly List<Row> _rows = new List<Row>();

        // Narrows what the pane shows without touching what was found. The
        // case it exists for: a search across a hundred session logs returns a
        // thousand hits, and the next question is "which of these was on
        // LAB4" - a question about the results, not a reason to search the
        // disk again.
        string _filter = "";

        Font _mono;
        Font _monoBold;
        double _cell = 8;   // width of one character cell, see ApplyFonts
        ContextMenuStrip _menu;

        public event EventHandler<OpenHitEventArgs> OpenRequested;

        // Raised for Ctrl+F and for the context menu item. The bar itself
        // belongs to the window, so the list asks rather than owning one.
        public event EventHandler FindRequested;

        public int FileCount { get { return _files.Count; } }

        public ResultsView()
        {
            View = View.Details;
            HeaderStyle = ColumnHeaderStyle.None;
            FullRowSelect = true;
            MultiSelect = true;
            VirtualMode = true;
            OwnerDraw = true;
            BorderStyle = BorderStyle.None;
            HideSelection = false;
            // One column, kept exactly as wide as the client area. A wider one
            // would give the control a horizontal scrollbar that is permanently
            // present and never useful: long lines are windowed around the
            // match rather than scrolled to, so there is nothing off to the
            // right to reach.
            Columns.Add("Result", 100);

            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

            RetrieveVirtualItem += OnRetrieveVirtualItem;
            DrawItem += OnDrawItem;
            DrawSubItem += OnDrawSubItem;
            Th.Changed += OnThemeChanged;

            BuildMenu();
            ApplyFonts();
            OnThemeChanged(null, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Th.Changed -= OnThemeChanged;
                if (_mono != null) _mono.Dispose();
                if (_monoBold != null) _monoBold.Dispose();
            }
            base.Dispose(disposing);
        }

        void ApplyFonts()
        {
            // Fixed pitch, because these are console logs and because a
            // proportional font makes the highlight arithmetic guesswork.
            // Consolas has shipped with Windows since Vista; the fallback is
            // there so a stripped image degrades to something usable.
            float size = 9f;
            _mono = new Font("Consolas", size, FontStyle.Regular);
            if (!_mono.Name.Equals("Consolas", StringComparison.OrdinalIgnoreCase))
            {
                _mono.Dispose();
                _mono = new Font(FontFamily.GenericMonospace, size, FontStyle.Regular);
            }
            _monoBold = new Font(_mono, FontStyle.Bold);
            Font = _mono;

            // One character cell, measured over a long run so the per-string
            // rounding in MeasureText averages out instead of being multiplied
            // by the length of the line.
            const int Sample = 200;
            _cell = Measure(new string('0', Sample), _mono).Width / (double)Sample;
            if (_cell < 1) _cell = 1;

            // ListView takes its row height from the small image list, not
            // from the font. An image list with no images and the height we
            // want is the standard way to ask for breathing room.
            ImageList spacer = new ImageList();
            spacer.ImageSize = new Size(1, Math.Max(1, _mono.Height + Dpi.S(4)));
            SmallImageList = spacer;
        }

        void OnThemeChanged(object sender, EventArgs e)
        {
            BackColor = Th.T.Input;
            ForeColor = Th.T.Txt;
            if (IsHandleCreated) OsChrome.ApplyScrollBars(this);
            if (_menu != null) MenuTheme.Apply(_menu);
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            OsChrome.ApplyScrollBars(this);
            FitColumn();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            FitColumn();
        }

        void FitColumn()
        {
            if (Columns.Count == 0) return;
            // Less the vertical scrollbar, which the column would otherwise
            // sit under and push a horizontal bar into existence.
            int w = ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
            if (w < Dpi.S(60)) w = Dpi.S(60);
            if (Columns[0].Width != w) Columns[0].Width = w;
        }

        // ---- content ------------------------------------------------------

        public void ClearResults()
        {
            SelectedIndices.Clear();
            _files.Clear();
            _collapsed.Clear();
            _rows.Clear();
            SetRowCount(0);
            FitColumn();
            Invalidate();
        }

        void SetRowCount(int count)
        {
            int previous = VirtualListSize;
            // Read the offset while the old rows still exist. After the count
            // drops, the control reports a nonsense value - a negative top
            // index - and can no longer act on a scroll request.
            int top = TopIndex();
            bool home = ViewRules.NeedsScrollHome(top, count);
            if (home && previous > 0) EnsureVisible(0);

            VirtualListSize = count;

            // Collapsing a group shrinks the list without invalidating the
            // reader's place, so the view is only pulled home when the place
            // no longer exists.
            if (home && count > 0) EnsureVisible(0);
        }

        int TopIndex()
        {
            if (!IsHandleCreated) return 0;
            return (int)Native.SendMessage(Handle, Native.LVM_GETTOPINDEX,
                                           IntPtr.Zero, IntPtr.Zero);
        }

        // Appends a batch. Batching is the caller's job because the engine
        // reports one file at a time from several threads at once, and a
        // VirtualListSize update per file makes the control redraw more often
        // than the disk delivers.
        public void AddFiles(IList<FileHits> batch)
        {
            if (batch == null || batch.Count == 0) return;
            for (int i = 0; i < batch.Count; i++)
            {
                _files.Add(batch[i]);
                _collapsed.Add(false);
            }
            Rebuild();
        }

        public void SetAllCollapsed(bool collapsed)
        {
            for (int i = 0; i < _collapsed.Count; i++) _collapsed[i] = collapsed;
            Rebuild();
        }

        // What the filter matches, and why each half is there.
        //
        // A file whose PATH matches keeps all of its hits, because "show me
        // LAB4" means the whole file, and the host is in the filename rather
        // than in any of the lines. A hit whose LINE matches is kept on its
        // own, with its file header for company, because a header with no rows
        // under it looks like a file that matched nothing.
        bool FileNameMatches(FileHits fh)
        {
            return ViewRules.FileKeepsEverything(_filter, fh.RelativePath);
        }

        bool HitMatches(Hit hit)
        {
            return ViewRules.HitIsShown(_filter, null, hit.Line, hit.Location);
        }

        bool FileHasAnything(FileHits fh)
        {
            if (FileNameMatches(fh)) return true;
            for (int h = 0; h < fh.Hits.Count; h++)
                if (HitMatches(fh.Hits[h])) return true;
            return false;
        }

        // Rows currently listed, and rows the search found, so the filter can
        // report what it is hiding rather than leaving a short list to be read
        // as a short answer.
        public int VisibleHits { get; private set; }

        public int TotalHits
        {
            get
            {
                int n = 0;
                for (int f = 0; f < _files.Count; f++) n += _files[f].Hits.Count;
                return n;
            }
        }

        public string Filter
        {
            get { return _filter; }
        }

        public void SetFilter(string filter)
        {
            string next = filter == null ? "" : filter;
            if (string.Equals(next, _filter, StringComparison.Ordinal)) return;
            _filter = next;
            Rebuild();
        }

        void Rebuild()
        {
            _rows.Clear();
            VisibleHits = 0;
            for (int f = 0; f < _files.Count; f++)
            {
                bool wholeFile = FileNameMatches(_files[f]);
                if (!wholeFile && !FileHasAnything(_files[f])) continue;

                Row header = new Row();
                header.Kind = RowKind.File;
                header.File = f;
                header.Hit = -1;
                _rows.Add(header);

                List<Hit> hits = _files[f].Hits;
                for (int h = 0; h < hits.Count; h++)
                    if (wholeFile || HitMatches(hits[h])) VisibleHits++;

                if (_collapsed[f]) continue;

                for (int h = 0; h < hits.Count; h++)
                {
                    Hit hit = hits[h];
                    if (!wholeFile && !HitMatches(hit)) continue;
                    if (hit.Before != null)
                    {
                        for (int b = hit.Before.Length; b > 0; b--)
                            _rows.Add(MakeLineRow(f, h, -b));
                    }
                    _rows.Add(MakeLineRow(f, h, 0));
                    if (hit.After != null)
                    {
                        for (int a = 1; a <= hit.After.Length; a++)
                            _rows.Add(MakeLineRow(f, h, a));
                    }
                }
            }

            // Selection indices are meaningless once the row list changes
            // under them, and leaving them behind makes the control ask for
            // rows that no longer exist.
            SelectedIndices.Clear();
            SetRowCount(_rows.Count);
            // The row count is what makes the vertical scrollbar appear, and
            // its arrival takes 17px out of the client area without raising a
            // resize. Without re-fitting here the column stays at the old
            // width and the control grows a horizontal scrollbar that scrolls
            // by exactly the width of the vertical one.
            FitColumn();
            Invalidate();
        }

        static Row MakeLineRow(int file, int hit, int rel)
        {
            Row r = new Row();
            r.Kind = RowKind.Line;
            r.File = file;
            r.Hit = hit;
            r.Rel = rel;
            return r;
        }

        void OnRetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            // Virtual mode asks for indices that were valid a moment ago while
            // a resize is in flight. Handing back a blank item is the documented
            // way through it; throwing takes the window with it.
            if (e.ItemIndex < 0 || e.ItemIndex >= _rows.Count)
            {
                e.Item = new ListViewItem(string.Empty);
                return;
            }
            e.Item = new ListViewItem(string.Empty);
        }

        // ---- painting -----------------------------------------------------

        void OnDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            // Everything is painted in OnDrawItem, which owns the whole row.
        }

        void OnDrawItem(object sender, DrawListViewItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _rows.Count) return;
            Theme t = Th.T;
            Row row = _rows[e.ItemIndex];
            Graphics g = e.Graphics;
            Rectangle bounds = e.Bounds;

            bool selected = (e.State & ListViewItemStates.Selected) != 0;
            Color back = row.Kind == RowKind.File ? t.Panel2 : t.Input;
            if (selected) back = Th.Mix(back, t.Accent, Focused ? 0.35 : 0.18);
            using (SolidBrush b = new SolidBrush(back))
                g.FillRectangle(b, bounds);

            int pad = Dpi.S(6);
            if (row.Kind == RowKind.File)
            {
                DrawFileRow(g, bounds, pad, row.File, t);
                return;
            }
            DrawLineRow(g, bounds, pad, row, t);
        }

        static int ArrowSize { get { return Dpi.S(7); } }

        // Where the filename starts, and therefore where the disclosure target
        // ends. One number so the drawing and the hit test cannot drift apart:
        // a gap between them would be a strip that looks like the arrow and
        // does not toggle, or one that looks like the name and does.
        static int DisclosureWidth
        {
            get { return Dpi.S(6) + ArrowSize * 2 + Dpi.S(6); }
        }

        void DrawFileRow(Graphics g, Rectangle bounds, int pad, int fileIndex, Theme t)
        {
            FileHits fh = _files[fileIndex];
            int x = bounds.X + pad;

            // The disclosure triangle, drawn rather than glyphed so it follows
            // the palette like everything else.
            int arrow = ArrowSize;
            int cy = bounds.Y + bounds.Height / 2;
            using (SolidBrush b = new SolidBrush(t.TxtDim))
            {
                Point[] tri = _collapsed[fileIndex]
                    ? new Point[] { new Point(x, cy - arrow), new Point(x, cy + arrow), new Point(x + arrow, cy) }
                    : new Point[] { new Point(x - 1, cy - arrow / 2), new Point(x + arrow * 2 - 1, cy - arrow / 2), new Point(x + arrow - 1, cy + arrow) };
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillPolygon(b, tri);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            }
            x = bounds.X + DisclosureWidth;

            Rectangle rest = new Rectangle(x, bounds.Y, bounds.Right - x - pad, bounds.Height);
            x += DrawRun(g, fh.RelativePath, _monoBold, rest, t.Txt);

            int shown = VisibleIn(fh);
            StringBuilder tail = new StringBuilder();
            tail.Append("  (").Append(shown.ToString(CultureInfo.InvariantCulture));
            // While filtered, the count says what it is out of. A header
            // reading "1 hit" over a file that actually matched seven would be
            // the pane quietly disagreeing with the search.
            if (shown != fh.Hits.Count)
                tail.Append(" of ").Append(fh.Hits.Count.ToString(CultureInfo.InvariantCulture));
            tail.Append(shown == 1 && shown == fh.Hits.Count ? " hit" : " hits");
            if (fh.Truncated) tail.Append(", capped");
            tail.Append(')');
            rest = new Rectangle(x, bounds.Y, bounds.Right - x - pad, bounds.Height);
            DrawRun(g, tail.ToString(), _mono, rest, t.TxtDim);
        }

        void DrawLineRow(Graphics g, Rectangle bounds, int pad, Row row, Theme t)
        {
            Hit hit = _files[row.File].Hits[row.Hit];
            bool isMatch = row.Rel == 0;

            string text;
            int lineNumber = hit.LineNumber + row.Rel;
            if (isMatch) text = hit.Line;
            else if (row.Rel < 0) text = hit.Before[hit.Before.Length + row.Rel];
            else text = hit.After[row.Rel - 1];

            int start = isMatch ? hit.MatchStart : -1;
            int length = isMatch ? hit.MatchLength : 0;
            text = PrepareForDisplay(text, ref start, ref length);

            int x = bounds.X + Dpi.S(22);

            // The gutter, always dim, so the eye goes to the text. Not
            // right-aligned into a column: that column would have to be as wide
            // as the longest entry in the whole result set, which is not known
            // until the scan ends.
            //
            // Extracted formats carry a location instead of a line number,
            // because "line 47 of a workbook" is not a place anyone can go.
            string gutter = hit.Location != null
                ? hit.Location + ": "
                : "Line " + lineNumber.ToString(CultureInfo.InvariantCulture) + ": ";
            Rectangle rest = new Rectangle(x, bounds.Y, bounds.Right - x, bounds.Height);
            DrawRun(g, gutter, _mono, rest, t.TxtDim);

            // Everything after the gutter is positioned by character cell
            // rather than by summing measured runs. MeasureText rounds each
            // string it is handed, so accumulating three of them drifts by a
            // pixel or two - which is invisible on the text and glaring on the
            // highlight, because a chip that is a pixel wide of its glyphs is
            // the one thing on the row the eye is drawn to. Cell arithmetic is
            // exact for a fixed-pitch font, and this list is only ever drawn
            // in one.
            int x0 = x + Cells(gutter.Length);
            Color body = isMatch ? t.Txt : t.TxtDim;

            if (start < 0 || length <= 0)
            {
                DrawAt(g, text, _mono, x0, bounds, pad, body);
                return;
            }

            // The match: accent fill with whichever of white or near black
            // reads on it, the same rule the buttons use.
            int chipX = x0 + Cells(start);
            Rectangle chip = new Rectangle(chipX, bounds.Y + Dpi.S(1),
                                           Cells(length), bounds.Height - Dpi.S(2));
            if (chip.Right > bounds.Right) chip.Width = Math.Max(0, bounds.Right - chip.X);
            if (chip.Width > 0)
            {
                using (SolidBrush b = new SolidBrush(t.Accent))
                    g.FillRectangle(b, chip);
            }

            DrawAt(g, text.Substring(0, start), _mono, x0, bounds, pad, body);
            DrawAt(g, text.Substring(start, length), _monoBold, chipX, bounds, pad,
                   Th.OnColor(t.Accent));
            DrawAt(g, text.Substring(start + length), _mono,
                   x0 + Cells(start + length), bounds, pad, body);
        }

        int Cells(int count)
        {
            return (int)Math.Round(count * _cell);
        }

        void DrawAt(Graphics g, string s, Font f, int x, Rectangle bounds, int pad, Color color)
        {
            if (string.IsNullOrEmpty(s)) return;
            int width = bounds.Right - pad - x;
            if (width <= 0) return;
            TextRenderer.DrawText(g, s, f, new Rectangle(x, bounds.Y, width, bounds.Height),
                                  color, Flags);
        }

        static readonly TextFormatFlags Flags =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
            TextFormatFlags.SingleLine;

        static Size Measure(string s, Font f)
        {
            return TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), Flags);
        }

        static int DrawRun(Graphics g, string s, Font f, Rectangle r, Color color)
        {
            if (string.IsNullOrEmpty(s) || r.Width <= 0) return 0;
            TextRenderer.DrawText(g, s, f, r, color, Flags);
            return Measure(s, f).Width;
        }

        // Expands tabs and, for a very long line, keeps a window around the
        // match. Both operations move the match, so both fix up the offsets
        // rather than leaving the highlight pointing at the wrong characters.
        static string PrepareForDisplay(string text, ref int start, ref int length)
        {
            const int TabWidth = 4;
            if (text == null) { start = -1; length = 0; return string.Empty; }

            if (text.IndexOf('\t') >= 0)
            {
                StringBuilder sb = new StringBuilder(text.Length + 16);
                int newStart = start, newLength = length;
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] != '\t') { sb.Append(text[i]); continue; }
                    int extra = TabWidth - 1;
                    sb.Append(' ', TabWidth);
                    if (start >= 0 && i < start) newStart += extra;
                    else if (start >= 0 && i < start + length) newLength += extra;
                }
                text = sb.ToString();
                start = newStart;
                length = newLength;
            }

            if (text.Length <= MaxDrawnLine) return text;

            int from = 0;
            if (start > MaxDrawnLine / 2) from = start - MaxDrawnLine / 2;
            int take = Math.Min(MaxDrawnLine, text.Length - from);
            string window = text.Substring(from, take);
            if (start >= 0) start -= from;

            if (from > 0)
            {
                window = "... " + window;
                if (start >= 0) start += 4;
            }
            if (from + take < text.Length) window += " ...";

            // If the match ran off the end of the window, the highlight would
            // index past it. Better an unhighlighted row than a wrong one.
            if (start < 0 || start + length > window.Length) { start = -1; length = 0; }
            return window;
        }

        // ---- interaction ---------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int index = IndexAt(e.Location);
            if (index < 0) return;
            Row row = _rows[index];

            // Only the indent toggles. Clicking the filename used to collapse
            // the group, which made double-clicking a header to open the file
            // impossible - the two clicks arrived first and cancelled each
            // other out.
            if (row.Kind == RowKind.File && e.X < DisclosureWidth) Toggle(row.File);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            OpenSelected();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int index = FocusedIndex();

            if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.C) { CopySelection(); e.Handled = true; return; }
            if (index < 0) return;

            Row row = _rows[index];
            if (e.KeyCode == Keys.Left && row.Kind == RowKind.File && !_collapsed[row.File])
            {
                Toggle(row.File);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Right && row.Kind == RowKind.File && _collapsed[row.File])
            {
                Toggle(row.File);
                e.Handled = true;
            }
        }

        int IndexAt(Point p)
        {
            ListViewItem item = GetItemAt(p.X, p.Y);
            if (item == null) return -1;
            return item.Index >= 0 && item.Index < _rows.Count ? item.Index : -1;
        }

        int FocusedIndex()
        {
            if (SelectedIndices.Count == 0) return -1;
            int i = SelectedIndices[0];
            return i >= 0 && i < _rows.Count ? i : -1;
        }

        // Used when the filter bar hands focus back: arriving in an empty
        // selection means the arrow keys do nothing, which reads as the list
        // being dead.
        public void SelectFirst()
        {
            if (_rows.Count == 0) return;
            SelectedIndices.Clear();
            SelectedIndices.Add(0);
            EnsureVisible(0);
        }

        void Toggle(int fileIndex)
        {
            _collapsed[fileIndex] = !_collapsed[fileIndex];
            Rebuild();
        }

        void OpenSelected()
        {
            int index = FocusedIndex();
            if (index < 0) return;
            if (OpenRequested == null) return;
            Row row = _rows[index];
            FileHits fh = _files[row.File];

            // A file header opens the file, at its first hit rather than at
            // line 1: someone who double-clicks the header of a log that
            // matched on line 1402 wants to be at line 1402, not at the top of
            // a session transcript.
            Hit hit;
            int line;
            if (row.Kind == RowKind.File)
            {
                if (fh.Hits.Count == 0) return;
                hit = fh.Hits[0];
                line = hit.LineNumber;
            }
            else
            {
                hit = fh.Hits[row.Hit];
                line = hit.LineNumber + row.Rel;
            }
            OpenRequested(this, new OpenHitEventArgs(fh.Path, line, hit.Location != null));
        }

        // ---- copy and export ------------------------------------------------

        public string SelectionAsText()
        {
            StringBuilder sb = new StringBuilder();
            foreach (int i in SelectedIndices)
            {
                if (i < 0 || i >= _rows.Count) continue;
                sb.AppendLine(RowAsText(_rows[i]));
            }
            return sb.ToString();
        }

        int VisibleIn(FileHits fh)
        {
            if (_filter.Length == 0 || FileNameMatches(fh)) return fh.Hits.Count;
            int n = 0;
            for (int h = 0; h < fh.Hits.Count; h++) if (HitMatches(fh.Hits[h])) n++;
            return n;
        }

        // Everything the filter admits, whether or not its group is collapsed.
        //
        // The two states are not the same kind of thing. A filter is a
        // statement about which results are wanted, so it belongs in what gets
        // copied. Collapsing is a way to get a long list out of the way while
        // reading, and someone who collapses a group before copying has not
        // asked to leave its contents behind.
        public string AllAsText()
        {
            StringBuilder sb = new StringBuilder();
            for (int f = 0; f < _files.Count; f++)
            {
                FileHits fh = _files[f];
                bool wholeFile = FileNameMatches(fh);
                if (!wholeFile && !FileHasAnything(fh)) continue;

                Row header = new Row();
                header.Kind = RowKind.File;
                header.File = f;
                header.Hit = -1;
                sb.AppendLine(RowAsText(header));

                for (int h = 0; h < fh.Hits.Count; h++)
                {
                    if (!wholeFile && !HitMatches(fh.Hits[h])) continue;
                    Hit hit = fh.Hits[h];
                    if (hit.Before != null)
                        for (int bIndex = hit.Before.Length; bIndex > 0; bIndex--)
                            sb.AppendLine(RowAsText(MakeLineRow(f, h, -bIndex)));
                    sb.AppendLine(RowAsText(MakeLineRow(f, h, 0)));
                    if (hit.After != null)
                        for (int a = 1; a <= hit.After.Length; a++)
                            sb.AppendLine(RowAsText(MakeLineRow(f, h, a)));
                }
            }
            return sb.ToString();
        }

        public void CopyAll()
        {
            string text = AllAsText();
            if (text.Length == 0) return;
            try { Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        }

        string RowAsText(Row row)
        {
            FileHits fh = _files[row.File];
            if (row.Kind == RowKind.File)
                return fh.Path + "  (" + fh.Hits.Count.ToString(CultureInfo.InvariantCulture) + " hits)";

            Hit hit = fh.Hits[row.Hit];
            string text = row.Rel == 0 ? hit.Line
                        : row.Rel < 0 ? hit.Before[hit.Before.Length + row.Rel]
                        : hit.After[row.Rel - 1];
            string where = hit.Location != null
                ? hit.Location
                : (hit.LineNumber + row.Rel).ToString(CultureInfo.InvariantCulture);
            // Tab-separated so a paste into a spreadsheet or a diff lands in
            // columns, and prefixed with the path so a copied line still says
            // where it came from once it leaves the window.
            return fh.RelativePath + "\t" + where + "\t" + text;
        }

        public void CopySelection()
        {
            string text = SelectionAsText();
            if (text.Length == 0) return;
            try { Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        }

        public string SelectedPath()
        {
            int index = FocusedIndex();
            if (index < 0) return null;
            return _files[_rows[index].File].Path;
        }

        // ---- context menu ---------------------------------------------------

        void BuildMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.Renderer = new ToolStripProfessionalRenderer(new ThemeColorTable());
            _menu.ShowImageMargin = false;

            Add(_menu, "Open", delegate { OpenSelected(); });
            Add(_menu, "Copy Selected", delegate { CopySelection(); });
            // The reason this exists: pasting a whole result set into a second
            // file and reading it there is a normal way to work through one.
            Add(_menu, "Copy All Results", delegate { CopyAll(); });
            Add(_menu, "Copy Path", delegate
            {
                string p = SelectedPath();
                if (p == null) return;
                try { Clipboard.SetText(p); }
                catch (System.Runtime.InteropServices.ExternalException) { }
            });
            Add(_menu, "Open Containing Folder", delegate
            {
                string p = SelectedPath();
                if (p == null) return;
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + p + "\"");
                }
                catch (System.ComponentModel.Win32Exception) { }
            });
            _menu.Items.Add(new ToolStripSeparator());
            Add(_menu, "Find in Results", delegate
            {
                if (FindRequested != null) FindRequested(this, EventArgs.Empty);
            });
            _menu.Items.Add(new ToolStripSeparator());
            Add(_menu, "Expand All", delegate { SetAllCollapsed(false); });
            Add(_menu, "Collapse All", delegate { SetAllCollapsed(true); });

            _menu.Opening += delegate { MenuTheme.Apply(_menu); };
            ContextMenuStrip = _menu;
        }

        static void Add(ContextMenuStrip menu, string text, EventHandler onClick)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += onClick;
            menu.Items.Add(item);
        }
    }

    public class OpenHitEventArgs : EventArgs
    {
        public readonly string Path;
        public readonly int Line;

        // True for a hit in an extracted format. Handing a workbook to a text
        // editor with a "-n14" argument opens a zip as text at line 14, which
        // is worse than useless; these go to the shell association instead.
        public readonly bool ShellOnly;

        public OpenHitEventArgs(string path, int line, bool shellOnly)
        {
            Path = path;
            Line = line;
            ShellOnly = shellOnly;
        }
    }
}
