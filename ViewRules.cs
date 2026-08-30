// Presentation decisions that are pure functions, kept out of the controls so
// they can be tested without a window.
//
// Most of what is here earned its place by being a bug someone hit rather than
// a rule someone predicted, which is why the comments are longer than the code.
//
// C# 5 only (in-box csc).

using System;
using System.Globalization;

namespace RSFind
{
    // What the results pane orders files by. Every one of these is read off
    // the FileHits the scan already produced, so none of them costs a second
    // pass over the folder.
    public enum ResultSort { Name, Modified, Created, Size, Hits }

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

        // Extensions the shell RUNS rather than opens, so RSFind will not hand
        // them to it.
        //
        // Opening a hit with no editor command configured calls Process.Start
        // on the path, which uses the file's association - normally a text
        // editor, because normally the file is a log. But the file mask can be
        // blank and Exclude binary files can be unchecked, which the README
        // describes as the way to find a string inside a firmware image. An
        // .exe, .bat, .ps1, or .lnk holding the search string as ASCII appears
        // in the results like anything else, and double-clicking it used to run
        // it.
        //
        // The replace path already refuses binaries "regardless of what the
        // search options said". The open path had not been given the same
        // reasoning, which is the whole of this rule.
        //
        // Two lists, deliberately. The built-in one covers what is dangerous on
        // any Windows machine; PATHEXT covers what this particular machine has
        // decided is executable, which is the only way to catch a box where
        // .py or .rb has been added to it.
        static readonly string[] AlwaysExecutable = {
            ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".cpl", ".msc",
            ".msi", ".msp", ".hta", ".jar", ".gadget", ".application",
            ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ps1", ".psm1",
            ".lnk", ".url", ".scf", ".reg"
        };

        public static bool IsExecutable(string path, string pathExt)
        {
            if (string.IsNullOrEmpty(path)) return false;

            string ext;
            try { ext = System.IO.Path.GetExtension(path); }
            catch (ArgumentException) { return true; }   // unreadable name, do not launch it
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.ToLowerInvariant();

            for (int i = 0; i < AlwaysExecutable.Length; i++)
                if (ext == AlwaysExecutable[i]) return true;

            if (string.IsNullOrEmpty(pathExt)) return false;
            string[] parts = pathExt.Split(new char[] { ';' },
                                           StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim().ToLowerInvariant();
                if (p.Length == 0) continue;
                if (p[0] != '.') p = "." + p;
                if (ext == p) return true;
            }
            return false;
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

        // How big a window is allowed to open, along one axis.
        //
        // A default size is only a good default on a display that can hold it.
        // The window's own default is wide enough for the sort controls to sit
        // on the options row, and at 150% on a 1366-wide laptop that figure is
        // wider than the whole desktop - a window opening with its own controls
        // off the edge of the screen is worse than one whose sort controls
        // wrap. It catches the other direction too: a size saved on a large
        // monitor and reopened on a small one.
        //
        // The minimum wins over the clamp. On a display small enough that even
        // the minimum will not fit, the minimum is the honest answer and the
        // window is allowed to be bigger than the desktop - shrinking below it
        // would only produce a layout that cannot draw itself.
        public static int ClampToWorkArea(int wanted, int available, int margin, int minimum)
        {
            int max = available - margin;
            if (max <= minimum) return wanted;
            return wanted > max ? max : wanted;
        }

        // ---- ordering -------------------------------------------------------

        // Before this existed the pane had no order at all. Files were appended
        // in whatever sequence Parallel.ForEach finished them, which looks
        // alphabetical - the walk hands paths out in directory order and most
        // come back in roughly the order they went out - right up until it
        // isn't, because one large file in the middle reorders everything
        // behind it. Two runs over the same folder could disagree.
        //
        // So the first job of a sort here is to give the pane a defined order,
        // and the second is to let someone pick which one.
        public static int CompareFiles(ResultSort key, bool descending,
                                       FileHits a, FileHits b)
        {
            if (a == null || b == null) return 0;

            int c = CompareKey(key, a, b);
            if (descending) c = -c;
            if (c != 0) return c;

            // The tie-break is NOT reversed along with the key. "Newest first"
            // means newest first and then A to Z; files sharing a timestamp
            // have expressed no opinion about each other, and flipping them
            // too would make the second half of the order surprising.
            //
            // It has to be here at all because ties are the normal case, not
            // the edge one. A folder pulled off a device in one copy carries
            // one creation time across every file in it, and Array.Sort is not
            // stable - without a total order those files would land in an
            // arbitrary arrangement that changes between runs, which is the
            // exact defect this feature exists to remove.
            return ComparePath(a.RelativePath, b.RelativePath);
        }

        static int CompareKey(ResultSort key, FileHits a, FileHits b)
        {
            switch (key)
            {
                case ResultSort.Modified:
                    return DateTime.Compare(a.LastWriteUtc, b.LastWriteUtc);
                case ResultSort.Created:
                    return DateTime.Compare(a.CreationUtc, b.CreationUtc);
                case ResultSort.Size:
                    return a.Length.CompareTo(b.Length);
                case ResultSort.Hits:
                    return HitCount(a).CompareTo(HitCount(b));
                default:
                    return ComparePath(a.RelativePath, b.RelativePath);
            }
        }

        static int HitCount(FileHits f)
        {
            return f.Hits == null ? 0 : f.Hits.Count;
        }

        // Case-insensitive so the order reads the way a person would write it,
        // then ordinal so that two names differing only in case still have an
        // order. Windows will not hand you both in one folder, but a search
        // spans subfolders and can.
        public static int ComparePath(string a, string b)
        {
            int c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            return string.CompareOrdinal(a, b);
        }

        // Stored in settings.ini as a word rather than a number, so the file
        // stays something a person can read and edit.
        public static string SortName(ResultSort key)
        {
            switch (key)
            {
                case ResultSort.Modified: return "modified";
                case ResultSort.Created: return "created";
                case ResultSort.Size: return "size";
                case ResultSort.Hits: return "hits";
                default: return "name";
            }
        }

        public static ResultSort ParseSort(string text, ResultSort fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            switch (text.Trim().ToLowerInvariant())
            {
                case "modified": return ResultSort.Modified;
                case "created": return ResultSort.Created;
                case "size": return ResultSort.Size;
                case "hits": return ResultSort.Hits;
                case "name": return ResultSort.Name;
                default: return fallback;
            }
        }

        // ---- the detail shown on a file header ------------------------------

        // Local time, not UTC. The timestamp is carried around in UTC so that
        // comparing two of them is unambiguous, and a reader comparing one
        // against their own memory of when they were on that box is not going
        // to do the offset arithmetic. Getting this wrong would be quiet: the
        // dates would look plausible and simply be a few hours off.
        public static string FormatWhen(DateTime utc)
        {
            if (utc == default(DateTime)) return "";
            DateTime local;
            try { local = utc.ToLocalTime(); }
            catch (ArgumentException) { return ""; }
            return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        // Bytes below a kilobyte, one decimal above it. Invariant throughout,
        // like every other number this program prints.
        public static string FormatSize(long bytes)
        {
            if (bytes < 0) return "";
            if (bytes < 1024)
                return bytes.ToString(CultureInfo.InvariantCulture) + " B";

            double v = bytes;
            string[] units = { "KB", "MB", "GB", "TB" };
            int u = -1;

            // The promotion test is against 1023.95, not 1024, because the
            // decision has to be made about the number that will be PRINTED
            // rather than the one being carried. One byte under a megabyte is
            // 1023.999 KB, which stays in KB against a 1024 test and then
            // rounds to one decimal place for display - so a file reports as
            // "1024.0 KB", a unit it was just decided not to be in.
            do { v /= 1024.0; u++; }
            while (v >= 1023.95 && u < units.Length - 1);

            return v.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[u];
        }
    }
}
