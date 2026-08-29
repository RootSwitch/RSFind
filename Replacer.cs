// Replace in files: the one button here that can damage a directory.
//
// The whole design is arranged so that nothing is written that the person did
// not look at first, and so that anything written can be put back.
//
//   - It never rescans. It works from the result set already on screen, so
//     "eyeball the Find results before replacing" is not a habit anyone has to
//     remember; it is the only path through the feature.
//   - It verifies twice. Once to build the preview, and again immediately
//     before the write, because minutes can pass while someone reads it.
//     Verification is not a timestamp check alone: every line being edited
//     must still be character-for-character what the search reported.
//   - It preserves the case of what it replaced, which is what makes a
//     British-to-American pass over a documentation folder produce a clean
//     diff instead of a hundred new sentence-case mistakes.
//   - It writes through a temporary file and keeps the original, so a whole
//     run reverts as a unit.
//
// C# 5 only (in-box csc).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RSFind
{
    public class ReplaceOptions
    {
        public string Replacement = "";
        public bool PreserveCase = true;
    }

    public class ReplaceChange
    {
        public int LineNumber;
        public string Before;
        public string After;
        public bool Selected = true;

        // The exact span being replaced, carried rather than re-derived by
        // diffing Before against After. A minimal diff of the two spellings
        // below is the single letter u, which is accurate and unreadable: the
        // preview would show a struck-out "u" where the reader needs to see
        // one word becoming another.
        //
        // charcheck:spelling-off
        //   Colour -> Color  diffs to  u -> (nothing)
        // charcheck:spelling-on
        public int MatchStart;
        public string OldText;
        public string NewText;

        // Index into the file's Hits. The offsets it implies are resolved at
        // plan time and resolved again at write time, never trusted across
        // the gap between them.
        public int HitIndex;
    }

    public class ReplacePlan
    {
        public FileHits File;
        public List<ReplaceChange> Changes = new List<ReplaceChange>();

        // Non-null means nothing will be written to this file, and this is the
        // sentence explaining why. Refusals are shown, never hidden: a file
        // that silently declines to change is the worst outcome here.
        public string Refusal;

        public bool CanApply { get { return Refusal == null && SelectedCount > 0; } }

        public int SelectedCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Changes.Count; i++) if (Changes[i].Selected) n++;
                return n;
            }
        }
    }

    public class ReplaceResult
    {
        public int FilesWritten;
        public int ChangesWritten;
        public List<string> Failures = new List<string>();
        public string UndoDirectory;
    }

    public static class Replacer
    {
        // ---- case preservation -------------------------------------------

        // Gives the replacement the shape of what it replaced. The examples
        // are the reason the feature exists, so they are spelled out with the
        // spelling scan suspended for four lines.
        //
        // charcheck:spelling-off
        //   colour -> color,  Colour -> Color,  COLOUR -> COLOR
        // charcheck:spelling-on
        //
        // Only when the replacement was typed in lower case. A replacement
        // carrying its own capitals - RSFind, GmbH, macOS - was written that
        // way deliberately, and reshaping it would be the tool overruling the
        // person. A mixed-case match is left alone too: there is no shape
        // there to copy.
        public static string PreserveCase(string matched, string replacement)
        {
            if (string.IsNullOrEmpty(matched) || string.IsNullOrEmpty(replacement))
                return replacement;
            if (HasUpper(replacement)) return replacement;

            if (IsAllUpper(matched)) return replacement.ToUpperInvariant();
            if (IsTitleCase(matched))
                return char.ToUpperInvariant(replacement[0]) + replacement.Substring(1);
            return replacement;
        }

        static bool HasUpper(string s)
        {
            for (int i = 0; i < s.Length; i++) if (char.IsUpper(s[i])) return true;
            return false;
        }

        static bool IsAllUpper(string s)
        {
            bool sawLetter = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (!char.IsLetter(s[i])) continue;
                sawLetter = true;
                if (!char.IsUpper(s[i])) return false;
            }
            return sawLetter;
        }

        static bool IsTitleCase(string s)
        {
            if (!char.IsUpper(s[0])) return false;
            for (int i = 1; i < s.Length; i++)
                if (char.IsLetter(s[i]) && char.IsUpper(s[i])) return false;
            return true;
        }

        // ---- planning -------------------------------------------------------

        public static ReplacePlan Plan(FileHits file, Matcher matcher, ReplaceOptions options)
        {
            ReplacePlan plan = new ReplacePlan();
            plan.File = file;

            // Refusals appear on one row in the preview, so they are written
            // to be read at a glance: what is wrong, then what to do about it.
            if (file.Extracted)
            {
                plan.Refusal = "text extracted from an Office file is not what is on disk";
                return plan;
            }
            if (file.Transformed)
            {
                plan.Refusal = "escapes were stripped for the search; "
                             + "turn off Strip ANSI escapes and search again";
                return plan;
            }

            string text;
            Encoding encoding;
            bool hasBom;
            string refusal = ReadVerified(file, out text, out encoding, out hasBom);
            if (refusal != null)
            {
                plan.Refusal = refusal;
                return plan;
            }

            int[] starts = LineStarts(text);
            for (int i = 0; i < file.Hits.Count; i++)
            {
                Hit hit = file.Hits[i];
                string reason = VerifyHit(text, starts, hit);
                if (reason != null)
                {
                    plan.Refusal = reason;
                    plan.Changes.Clear();
                    return plan;
                }

                string matched = hit.Line.Substring(hit.MatchStart, hit.MatchLength);
                string replacement;
                try
                {
                    replacement = matcher.Expand(hit.Line, hit.MatchStart, options.Replacement);
                }
                catch (PatternError ex)
                {
                    plan.Refusal = ex.Message;
                    plan.Changes.Clear();
                    return plan;
                }
                if (options.PreserveCase) replacement = PreserveCase(matched, replacement);

                ReplaceChange change = new ReplaceChange();
                change.LineNumber = hit.LineNumber;
                change.HitIndex = i;
                change.MatchStart = hit.MatchStart;
                change.OldText = matched;
                change.NewText = replacement;
                change.Before = hit.Line;
                change.After = hit.Line.Substring(0, hit.MatchStart)
                             + replacement
                             + hit.Line.Substring(hit.MatchStart + hit.MatchLength);
                plan.Changes.Add(change);
            }

            if (plan.Changes.Count == 0) plan.Refusal = "nothing to replace in this file";
            return plan;
        }

        // ---- verification ----------------------------------------------------

        // Re-reads the file and checks it is the one the search saw. Size and
        // timestamp are the cheap half; the caller then checks every line it
        // intends to touch, which is the half that catches an edit that
        // happened to land on the same size.
        static string ReadVerified(FileHits file, out string text,
                                   out Encoding encoding, out bool hasBom)
        {
            text = null;
            encoding = null;
            hasBom = false;

            FileInfo info;
            try
            {
                info = new FileInfo(file.Path);
                if (!info.Exists) return "the file no longer exists";
                if (info.Length != file.Length)
                    return "the file has changed size since the search";
                if (info.LastWriteTimeUtc != file.LastWriteUtc)
                    return "the file has been modified since the search";
            }
            catch (IOException ex) { return ex.Message; }
            catch (UnauthorizedAccessException ex) { return ex.Message; }

            byte[] bytes;
            try
            {
                bytes = TextFiles.ReadAllBytesShared(file.Path);
            }
            catch (IOException ex) { return ex.Message; }
            catch (UnauthorizedAccessException ex) { return ex.Message; }

            // A binary is refused whatever the search options said. Someone
            // who unchecked exclude-binary to find a string inside a firmware
            // image did not thereby ask to have it rewritten.
            if (TextFiles.LooksBinary(bytes))
                return "this file looks binary, and RSFind will not rewrite binaries";

            text = TextFiles.Decode(bytes, out encoding, out hasBom);
            return null;
        }

        static string VerifyHit(string text, int[] starts, Hit hit)
        {
            int index = hit.LineNumber - 1;
            if (index < 0 || index >= starts.Length)
                return "the file has fewer lines than it did during the search";

            // The line as stored, minus its terminator, which is what the
            // search reported.
            int from = starts[index];
            int end = index + 1 < starts.Length ? starts[index + 1] : text.Length;
            if (end > from && text[end - 1] == '\n') end--;
            if (end > from && text[end - 1] == '\r') end--;

            string current = text.Substring(from, end - from);
            if (!string.Equals(current, hit.Line, StringComparison.Ordinal))
                return "the text at line " + hit.LineNumber.ToString(CultureInfo.InvariantCulture)
                     + " has changed since the search";
            return null;
        }

        static int[] LineStarts(string text)
        {
            List<int> starts = new List<int>();
            starts.Add(0);
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n' && i + 1 <= text.Length) starts.Add(i + 1);
            // A file ending in a newline has no line after it, matching how
            // SplitLines refuses to invent a trailing empty line.
            if (starts.Count > 1 && starts[starts.Count - 1] == text.Length)
                starts.RemoveAt(starts.Count - 1);
            return starts.ToArray();
        }

        // ---- writing ----------------------------------------------------------

        // Applies the selected changes. Everything is verified again first,
        // because the preview the person approved may be minutes old.
        public static ReplaceResult Apply(IList<ReplacePlan> plans, string undoRoot)
        {
            ReplaceResult result = new ReplaceResult();
            List<string> manifest = new List<string>();
            string undoDir = null;

            for (int p = 0; p < plans.Count; p++)
            {
                ReplacePlan plan = plans[p];
                if (!plan.CanApply) continue;

                string text;
                Encoding encoding;
                bool hasBom;
                string refusal = ReadVerified(plan.File, out text, out encoding, out hasBom);
                if (refusal != null)
                {
                    result.Failures.Add(plan.File.RelativePath + ": " + refusal);
                    continue;
                }

                int[] starts = LineStarts(text);
                string rebuilt;
                refusal = Rebuild(plan, text, starts, out rebuilt);
                if (refusal != null)
                {
                    result.Failures.Add(plan.File.RelativePath + ": " + refusal);
                    continue;
                }

                byte[] outBytes;
                refusal = Encode(rebuilt, encoding, hasBom, out outBytes);
                if (refusal != null)
                {
                    result.Failures.Add(plan.File.RelativePath + ": " + refusal);
                    continue;
                }

                if (undoDir == null)
                {
                    undoDir = NewUndoDirectory(undoRoot);
                    if (undoDir == null)
                    {
                        result.Failures.Add("could not create the undo folder, so nothing was written");
                        return result;
                    }
                }

                string backup = Path.Combine(undoDir,
                    result.FilesWritten.ToString("D4", CultureInfo.InvariantCulture) + ".bak");
                try
                {
                    File.Copy(plan.File.Path, backup, true);
                }
                catch (IOException ex)
                {
                    result.Failures.Add(plan.File.RelativePath + ": could not back up (" + ex.Message + ")");
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    result.Failures.Add(plan.File.RelativePath + ": could not back up (" + ex.Message + ")");
                    continue;
                }

                refusal = WriteThroughTemp(plan.File.Path, outBytes);
                if (refusal != null)
                {
                    result.Failures.Add(plan.File.RelativePath + ": " + refusal);
                    try { File.Delete(backup); } catch (IOException) { }
                    continue;
                }

                // What was written, so Undo can refuse to clobber a later edit.
                FileInfo after = new FileInfo(plan.File.Path);
                manifest.Add(string.Join("|", new string[] {
                    Path.GetFileName(backup),
                    after.Length.ToString(CultureInfo.InvariantCulture),
                    after.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                    plan.File.Path
                }));

                result.FilesWritten++;
                result.ChangesWritten += plan.SelectedCount;
            }

            if (undoDir != null && manifest.Count > 0)
            {
                try
                {
                    File.WriteAllLines(Path.Combine(undoDir, "manifest.txt"),
                                       manifest.ToArray(), new UTF8Encoding(false));
                    result.UndoDirectory = undoDir;
                }
                catch (IOException ex)
                {
                    result.Failures.Add("the undo record could not be written (" + ex.Message + ")");
                }
            }
            return result;
        }

        // Rebuilds the whole file from the original text plus the selected
        // edits, working on character offsets rather than by splitting and
        // rejoining lines. Rejoining would normalize line endings, drop or add
        // a final newline, and quietly rewrite every line in a file where only
        // one was meant to change.
        static string Rebuild(ReplacePlan plan, string text, int[] starts, out string rebuilt)
        {
            rebuilt = null;
            List<int> offsets = new List<int>();
            List<int> lengths = new List<int>();
            List<string> inserts = new List<string>();

            for (int i = 0; i < plan.Changes.Count; i++)
            {
                ReplaceChange change = plan.Changes[i];
                if (!change.Selected) continue;

                Hit hit = plan.File.Hits[change.HitIndex];
                string reason = VerifyHit(text, starts, hit);
                if (reason != null) return reason;

                int abs = starts[hit.LineNumber - 1] + hit.MatchStart;
                if (abs < 0 || abs + hit.MatchLength > text.Length)
                    return "a match now falls outside the file";

                string onDisk = text.Substring(abs, hit.MatchLength);
                string expected = hit.Line.Substring(hit.MatchStart, hit.MatchLength);
                if (!string.Equals(onDisk, expected, StringComparison.Ordinal))
                    return "the matched text has changed since the search";

                // The replacement is recovered from the previewed line, so
                // what lands on disk is exactly what was on screen.
                int tail = hit.Line.Length - (hit.MatchStart + hit.MatchLength);
                string replacement = change.After.Substring(
                    hit.MatchStart, change.After.Length - tail - hit.MatchStart);

                offsets.Add(abs);
                lengths.Add(hit.MatchLength);
                inserts.Add(replacement);
            }

            if (offsets.Count == 0) return "nothing was selected";

            // Ascending order, skipping any that overlap one already taken.
            int[] order = new int[offsets.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, delegate(int a, int b) { return offsets[a].CompareTo(offsets[b]); });

            StringBuilder sb = new StringBuilder(text.Length);
            int cursor = 0;
            for (int k = 0; k < order.Length; k++)
            {
                int i = order[k];
                if (offsets[i] < cursor) continue;
                sb.Append(text, cursor, offsets[i] - cursor);
                sb.Append(inserts[i]);
                cursor = offsets[i] + lengths[i];
            }
            sb.Append(text, cursor, text.Length - cursor);
            rebuilt = sb.ToString();
            return null;
        }

        // Encodes and then proves the encoding round-trips.
        //
        // This is the guard that stops an ANSI file quietly losing characters.
        // Encoding.Default maps anything outside its codepage to a question
        // mark and reports success, so replacing a word in a Windows-1252 file
        // with one containing an em dash or a Greek letter would silently
        // destroy it. Better to refuse the file and say so.
        static string Encode(string text, Encoding encoding, bool hasBom, out byte[] bytes)
        {
            bytes = null;
            try
            {
                byte[] body = encoding.GetBytes(text);
                string roundTrip = encoding.GetString(body);
                if (!string.Equals(roundTrip, text, StringComparison.Ordinal))
                    return "the replacement uses characters this file's encoding ("
                         + encoding.WebName + ") cannot store";

                if (!hasBom) { bytes = body; return null; }

                byte[] preamble = encoding.GetPreamble();
                byte[] all = new byte[preamble.Length + body.Length];
                Buffer.BlockCopy(preamble, 0, all, 0, preamble.Length);
                Buffer.BlockCopy(body, 0, all, preamble.Length, body.Length);
                bytes = all;
                return null;
            }
            catch (EncoderFallbackException)
            {
                return "the replacement cannot be encoded in this file's encoding";
            }
        }

        // Writes beside the target and swaps, so a failure part-way through
        // leaves the original intact rather than a half-written file.
        static string WriteThroughTemp(string path, byte[] bytes)
        {
            string temp = path + ".rsfind-tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
            }
            catch (IOException ex) { return ex.Message; }
            catch (UnauthorizedAccessException ex) { return ex.Message; }

            try
            {
                // Replace keeps the destination's attributes and access
                // control, which a delete-then-move would throw away.
                File.Replace(temp, path, null);
                return null;
            }
            catch (IOException)
            {
                // Replace refuses across some filesystems and on files with
                // certain attributes. Falling back is worth it, but only after
                // the temp file is known to be complete.
                try
                {
                    File.Delete(path);
                    File.Move(temp, path);
                    return null;
                }
                catch (IOException ex)
                {
                    Cleanup(temp);
                    return ex.Message;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Cleanup(temp);
                    return ex.Message;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                Cleanup(temp);
                return ex.Message;
            }
        }

        static void Cleanup(string temp)
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---- undo -------------------------------------------------------------

        static string NewUndoDirectory(string undoRoot)
        {
            try
            {
                // The caller supplies the stamp so this stays testable and so
                // two runs in the same second cannot collide.
                string dir = Path.Combine(undoRoot,
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                int suffix = 1;
                string candidate = dir;
                while (Directory.Exists(candidate))
                    candidate = dir + "-" + (++suffix).ToString(CultureInfo.InvariantCulture);
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // Puts back every file in a run, refusing any that has been edited
        // since. A file someone has worked on since the replace is not one to
        // silently overwrite in the name of undoing.
        public static ReplaceResult Undo(string undoDir)
        {
            ReplaceResult result = new ReplaceResult();
            result.UndoDirectory = undoDir;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(Path.Combine(undoDir, "manifest.txt"));
            }
            catch (IOException ex)
            {
                result.Failures.Add("the undo record could not be read (" + ex.Message + ")");
                return result;
            }

            foreach (string line in lines)
            {
                string[] parts = line.Split(new char[] { '|' }, 4);
                if (parts.Length != 4) continue;

                string backup = Path.Combine(undoDir, parts[0]);
                long expectedLength;
                long expectedTicks;
                if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out expectedLength)) continue;
                if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out expectedTicks)) continue;
                string target = parts[3];

                try
                {
                    FileInfo now = new FileInfo(target);
                    if (!now.Exists)
                    {
                        result.Failures.Add(target + ": no longer exists");
                        continue;
                    }
                    if (now.Length != expectedLength || now.LastWriteTimeUtc.Ticks != expectedTicks)
                    {
                        result.Failures.Add(target + ": edited since the replace, left alone");
                        continue;
                    }
                    File.Copy(backup, target, true);
                    result.FilesWritten++;
                }
                catch (IOException ex) { result.Failures.Add(target + ": " + ex.Message); }
                catch (UnauthorizedAccessException ex) { result.Failures.Add(target + ": " + ex.Message); }
            }
            return result;
        }

        // The most recent run, or null. Used to offer Undo without making the
        // person find a folder.
        public static string LatestUndoDirectory(string undoRoot)
        {
            try
            {
                if (!Directory.Exists(undoRoot)) return null;
                string[] dirs = Directory.GetDirectories(undoRoot);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                for (int i = dirs.Length - 1; i >= 0; i--)
                    if (File.Exists(Path.Combine(dirs[i], "manifest.txt"))) return dirs[i];
                return null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
