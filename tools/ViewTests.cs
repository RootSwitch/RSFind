// Tests that need a real window.
//
// EngineTests covers everything in RSFind that is not a window, on purpose:
// pure functions are cheap to test and fast to run. But the results pane has
// now produced two bugs that no pure test could have caught, both found by
// someone using the tool rather than by the suite - a scroll offset left
// pointing past the end of the data, and a selection state the framework
// reports incorrectly. Both live in the space between this code and the
// native control, and only a real control can answer for that.
//
// So this is a second, smaller suite that opens a window. It needs an
// interactive desktop and will not run in a headless session.
//
// Run: tools\Run-Tests.cmd
//
// C# 5 only (in-box csc).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RSFind
{
    public static class ViewTests
    {
        static int checks;

        [STAThread]
        public static int Main()
        {
            try
            {
                SelectionPaintTests();
                SortKeepsCollapseWithItsFile();
                Console.WriteLine("PASS  " + checks + " view checks");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL  " + ex.Message);
                return 1;
            }
        }

        static void Ok(bool condition, string what)
        {
            checks++;
            if (!condition) throw new Exception(what);
        }

        static void Eq(int actual, int expected, string what)
        {
            checks++;
            if (actual != expected)
                throw new Exception(what + ": expected " + expected + " got " + actual);
        }

        // The bug: click one line, scroll, and every line scrolling into view
        // renders as though it were selected too. The real selection was never
        // wrong - Copy Selected always copied the one line - so this was purely
        // what got painted.
        //
        // The cause is that DrawListViewItemEventArgs.State is not usable in
        // virtual mode. Measured on a bare ListView configured the same way:
        // 216 of 216 painted rows claimed Selected against a real selection of
        // exactly one, with and without the double-buffering styles.
        //
        // This test asks the only question that matters end to end - how many
        // rows are painted with a selection background - by rendering the
        // control and counting them, rather than by trusting the same event
        // argument that was wrong in the first place.
        static void SelectionPaintTests()
        {
            Dpi.Init();
            Th.Set("classic");

            Form f = new Form();
            f.ClientSize = new Size(700, 320);
            ResultsView view = new ResultsView();
            view.Dock = DockStyle.Fill;
            f.Controls.Add(view);
            f.Show();
            Pump();

            view.AddFiles(MakeFiles(14, 40));   // 14 headers + 560 line rows
            Pump();

            int rowHeight = view.GetItemRect(0).Height;
            Ok(rowHeight > 0, "the control reports a row height");

            // Nothing selected yet, so nothing should be painted as selected.
            Eq(PaintedAsSelected(view, rowHeight), 0, "an untouched list paints no selection");

            // One row selected, on screen.
            view.SelectedIndices.Clear();
            view.SelectedIndices.Add(2);
            Pump();
            Eq(view.SelectedIndices.Count, 1, "exactly one row is really selected");
            Eq(PaintedAsSelected(view, rowHeight), 1, "one selected row paints as one selected row");

            // Now scroll far past it. The selection has not changed, and the
            // only selected row is no longer on screen, so nothing visible
            // should carry the selection background. Before the fix every
            // single visible row did.
            view.EnsureVisible(400);
            Pump();
            Eq(view.SelectedIndices.Count, 1, "scrolling does not change the selection");
            Eq(PaintedAsSelected(view, rowHeight), 0,
               "scrolling away from the selected row leaves nothing painted as selected");

            // And scrolling back finds it again, so the fix did not simply
            // stop drawing selections.
            view.EnsureVisible(0);
            Pump();
            Eq(PaintedAsSelected(view, rowHeight), 1, "scrolling back repaints the one real selection");

            f.Close();
            f.Dispose();
        }

        // The trap a sort over this view has to avoid: the files and their
        // collapse flags are parallel lists indexed by position, so permuting
        // one without the other lands every group's expanded state on a
        // different file. That failure does not look like a sort bug - it
        // looks like the disclosure triangles randomly forgetting themselves.
        //
        // Made observable through the row count. The two files here have
        // different hit counts, so collapsing one removes a known number of
        // rows; if the flag ends up on the other file after a sort, a
        // different number comes back.
        static void SortKeepsCollapseWithItsFile()
        {
            Dpi.Init();
            Th.Set("classic");

            Form f = new Form();
            f.ClientSize = new Size(700, 320);
            ResultsView view = new ResultsView();
            view.Dock = DockStyle.Fill;
            f.Controls.Add(view);
            f.Show();
            Pump();

            // Sorting by size reverses the name order, so a permutation bug
            // cannot hide behind the two orders happening to agree.
            List<FileHits> files = new List<FileHits>();
            files.Add(One("alpha.log", 8, 900));
            files.Add(One("bravo.log", 40, 100));
            view.AddFiles(files);
            view.ApplySort();               // by name: alpha, bravo
            Pump();

            Eq(view.VirtualListSize, 2 + 48, "two headers and every hit");

            // Collapse bravo, whose header sits below alpha's eight hits.
            ClickDisclosure(view, 9);
            Pump();
            Eq(view.VirtualListSize, 2 + 8, "collapsing the 40-hit file hides forty rows");

            // Smallest first puts bravo above alpha. Its collapse flag has to
            // travel with it. If the flags stay put instead, alpha collapses
            // and the count comes back as 2 + 40.
            view.SetSort(ResultSort.Size, false, false);
            Pump();
            Eq(view.VirtualListSize, 2 + 8,
               "the collapsed file is still the collapsed file after a sort");

            view.SetAllCollapsed(false);
            Pump();
            Eq(view.VirtualListSize, 2 + 48, "expanding everything brings every row back");

            f.Close();
            f.Dispose();
        }

        // The handler is invoked directly rather than by posting a real
        // WM_LBUTTONDOWN.
        //
        // A native ListView answers a button-down by entering its own modal
        // loop for marquee selection, and that loop waits for a physical
        // button release which a synthesized message never delivers. The first
        // version of this test posted the message and hung the suite instead
        // of failing it, which is the one outcome a test must never have.
        static void ClickDisclosure(ResultsView view, int rowIndex)
        {
            Rectangle r = view.GetItemRect(rowIndex);
            MouseEventArgs click = new MouseEventArgs(
                MouseButtons.Left, 1, 2, r.Y + r.Height / 2, 0);   // x=2 is inside the indent
            typeof(ResultsView)
                .GetMethod("OnMouseDown", System.Reflection.BindingFlags.Instance |
                                          System.Reflection.BindingFlags.NonPublic)
                .Invoke(view, new object[] { click });
        }

        static FileHits One(string name, int hits, long size)
        {
            FileHits fh = new FileHits();
            fh.Path = @"C:\logs\" + name;
            fh.RelativePath = name;
            fh.Length = size;
            fh.LastWriteUtc = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
            fh.CreationUtc = fh.LastWriteUtc;
            for (int i = 0; i < hits; i++)
            {
                Hit hit = new Hit();
                hit.LineNumber = i + 1;
                hit.Line = "a line with smartctl in it";
                hit.MatchStart = 12;
                hit.MatchLength = 8;
                fh.Hits.Add(hit);
            }
            return fh;
        }

        // Renders the control and counts rows whose background is neither of
        // the two unselected backgrounds. A selected row is the base color
        // mixed toward the accent, so it cannot collide with either.
        //
        // Sampled two pixels in from the left edge, which is inside the row
        // background and outside the disclosure triangle and every glyph.
        static int PaintedAsSelected(ResultsView view, int rowHeight)
        {
            using (Bitmap bmp = new Bitmap(view.Width, view.Height))
            {
                view.DrawToBitmap(bmp, new Rectangle(0, 0, view.Width, view.Height));

                Color fileRow = Th.T.Panel2;
                Color lineRow = Th.T.Input;
                int found = 0;
                for (int y = rowHeight / 2; y < bmp.Height; y += rowHeight)
                {
                    Color c = bmp.GetPixel(2, y);
                    if (Same(c, fileRow) || Same(c, lineRow)) continue;
                    found++;
                }
                return found;
            }
        }

        // GDI round-trips through a 32bpp surface, so an exact match is not
        // guaranteed. One unit of slack per channel is far tighter than the
        // gap between a base color and the same color mixed toward the accent.
        static bool Same(Color a, Color b)
        {
            return Math.Abs(a.R - b.R) <= 1
                && Math.Abs(a.G - b.G) <= 1
                && Math.Abs(a.B - b.B) <= 1;
        }

        static List<FileHits> MakeFiles(int files, int hitsEach)
        {
            List<FileHits> all = new List<FileHits>();
            DateTime when = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < files; i++)
            {
                FileHits fh = new FileHits();
                fh.Path = @"C:\logs\file" + i.ToString("00") + ".log";
                fh.RelativePath = "file" + i.ToString("00") + ".log";
                fh.Length = 1024 * (i + 1);
                fh.LastWriteUtc = when.AddHours(i);
                fh.CreationUtc = when.AddHours(-i);
                for (int h = 0; h < hitsEach; h++)
                {
                    Hit hit = new Hit();
                    hit.LineNumber = h + 1;
                    hit.Line = "line " + h + " with smartctl in it";
                    hit.MatchStart = 12;
                    hit.MatchLength = 8;
                    fh.Hits.Add(hit);
                }
                all.Add(fh);
            }
            return all;
        }

        static void Pump()
        {
            for (int i = 0; i < 10; i++)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(8);
            }
        }
    }
}
