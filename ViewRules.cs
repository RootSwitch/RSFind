// Presentation decisions that are pure functions, kept out of the controls so
// they can be tested without a window.
//
// There is exactly one rule here so far, and it earned its place by being a
// bug someone hit rather than a rule someone predicted.
//
// C# 5 only (in-box csc).

using System;

namespace RSFind
{
    public static class ViewRules
    {
        static bool Contains(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return true;
            if (haystack == null) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // A file whose PATH matches the filter keeps every one of its hits.
        //
        // This is the case the filter exists for. Narrowing a thousand hits to
        // "the ones from LAB4" is a question about which FILE, and the host is
        // in the filename rather than in any of the matched lines - so keeping
        // only the lines that happen to contain "LAB4" would answer with
        // almost nothing and look like the filter was broken.
        public static bool FileKeepsEverything(string filter, string relativePath)
        {
            return string.IsNullOrEmpty(filter) || Contains(relativePath, filter);
        }

        // Otherwise a hit is kept on its own merits, matched against the line
        // and against the location label a workbook cell carries instead of a
        // line number.
        public static bool HitIsShown(string filter, string relativePath,
                                      string lineText, string location)
        {
            if (FileKeepsEverything(filter, relativePath)) return true;
            return Contains(lineText, filter) || Contains(location, filter);
        }

        // True when a list's scroll position is about to be left pointing past
        // the end of its data.
        //
        // A native ListView does not clamp its scroll offset when the item
        // count shrinks under it. Run a search that fills the pane, scroll down
        // to read it, then run a second search matching a handful of lines: the
        // offset stays where it was, the viewport sits beyond the last row, and
        // the pane renders blank. A short result set has no scrollbar either,
        // so there is no way to scroll back - the results are simply invisible
        // while the status line reports how many there are, which reads as the
        // tool lying about having found something.
        //
        // Two cases fall out of the one comparison rather than needing their
        // own branches, which is worth stating because the obvious version of
        // this function has both and neither does anything.
        //
        // Growing never triggers it: results stream in during a scan, and a
        // valid top index is always below the count, so a list that is filling
        // up is left where the reader put it. An explicit "if it grew, do
        // nothing" test was written here first and the planted-defect run
        // showed it was dead code.
        //
        // Shrinking without passing the reader's place never triggers it
        // either, so collapsing one file's group keeps their position.
        public static bool NeedsScrollHome(int topIndex, int newCount)
        {
            return topIndex >= newCount;
        }
    }
}
