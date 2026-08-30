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
    }
}
