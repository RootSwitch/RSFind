// Tests for everything in RSFind that is not a window.
//
// Plain asserts, no framework, non-zero exit on the first failure - the same
// shape as the Canvas suite's tools\*.js. Every check here has been verified
// by planting the defect it is meant to catch and watching it fail.
//
// Run: tools\Run-Tests.cmd
//
// C# 5 only (in-box csc).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace RSFind
{
    public static class EngineTests
    {
        static int checks;
        const char Esc = (char)27;
        const char Bel = (char)7;

        public static int Main()
        {
            // A corpus under the repo rather than %TEMP%, so a failed run
            // leaves the evidence where the person debugging it will look.
            string root = Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location)),
                "testdata");

            try
            {
                MatcherTests();
                MaskTests();
                BomTests();
                BinaryTests();
                DecodeTests();
                SplitTests();
                AnsiTests();
                EngineTests_EndToEnd(root);

                Console.WriteLine("PASS  " + checks + " checks");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL  " + ex.Message);
                return 1;
            }
            finally
            {
                // Removes only what this run created, by name.
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (IOException) { }
            }
        }

        // ---- the harness -------------------------------------------------

        static void Ok(bool condition, string what)
        {
            checks++;
            if (!condition) throw new Exception(what);
        }

        static void Eq(object actual, object expected, string what)
        {
            checks++;
            string a = actual == null ? "(null)" : actual.ToString();
            string e = expected == null ? "(null)" : expected.ToString();
            if (a != e)
                throw new Exception(what + ": expected [" + e + "] got [" + a + "]");
        }

        // The bound is what makes the zero-width test able to fail. Without it,
        // a Matcher that reports endless empty matches hangs the harness
        // instead of failing it, and a test that can only hang is not a test.
        const int RunawayGuard = 10000;

        static int CountMatches(Matcher m, string line)
        {
            int n = 0, from = 0, s, len;
            while (n < RunawayGuard && m.Next(line, from, out s, out len))
            {
                n++;
                from = s + len;
            }
            return n;
        }

        // ---- Matcher -----------------------------------------------------

        static void MatcherTests()
        {
            Matcher plain = new Matcher("smartctl", false, false, false);
            Eq(CountMatches(plain, "root@LAB1:~# smartctl -a /dev/nvme0"), 1, "literal finds one");
            Eq(CountMatches(plain, "SMARTCTL and smartctl"), 2, "literal is case-insensitive by default");

            Matcher cased = new Matcher("smartctl", true, false, false);
            Eq(CountMatches(cased, "SMARTCTL and smartctl"), 1, "match case honored");

            // The whole-word case that matters for the log workload: the drive
            // model "nvme0n1" must not answer a search for "nvme0".
            Matcher word = new Matcher("nvme0", false, true, false);
            Eq(CountMatches(word, "/dev/nvme0 and /dev/nvme0n1"), 1, "whole word rejects a longer run");
            Eq(CountMatches(word, "nvme0"), 1, "whole word matches a line that is only the word");
            Eq(CountMatches(new Matcher("sensor", false, true, false), "sensors"), 0,
               "whole word rejects a suffixed word");

            Matcher re = new Matcher("nvme[0-9]+", false, false, true);
            Eq(CountMatches(re, "/dev/nvme0 /dev/nvme11"), 2, "regex finds both");

            // Whole word must wrap the alternation, not bind to its ends.
            Matcher alt = new Matcher("cat|dog", false, true, true);
            Eq(CountMatches(alt, "the dog"), 1, "regex whole word matches an alternative");
            Eq(CountMatches(alt, "dogma"), 0, "regex whole word rejects a prefix of a longer word");

            bool threw = false;
            try { new Matcher("nvme[0-9", false, false, true); }
            catch (PatternError) { threw = true; }
            Ok(threw, "a half-typed regex raises PatternError rather than escaping");

            threw = false;
            try { new Matcher("", false, false, false); }
            catch (PatternError) { threw = true; }
            Ok(threw, "an empty query raises PatternError");

            // A pattern that can match nothing must still terminate. Without
            // the zero-width guard this call never returns.
            Matcher zero = new Matcher("x*", false, false, true);
            Ok(CountMatches(zero, "abc") < 100, "a zero-width regex terminates");

            // Catastrophic backtracking is preempted rather than hanging a
            // reader thread past any Cancel the user presses.
            Matcher bomb = new Matcher("(a+)+$", false, false, true);
            DateTime t0 = DateTime.UtcNow;
            CountMatches(bomb, new string('a', 40) + "!");
            Ok((DateTime.UtcNow - t0).TotalSeconds < 10, "a backtracking bomb is timed out, not hung");
        }

        // ---- Masks -------------------------------------------------------

        static void MaskTests()
        {
            Ok(Masks.Matches("LAB1.log", "*.log"), "*.log matches");
            Ok(!Masks.Matches("LAB1.txt", "*.log"), "*.log rejects .txt");
            Ok(Masks.Matches("LAB1.LOG", "*.log"), "masks are case-insensitive");
            Ok(Masks.Matches("LAB1.log", ".log"), "a bare .log is read as *.log");
            Ok(Masks.Matches("notes.md", "notes.md"), "an exact name matches itself");
            Ok(Masks.Matches("a-b-c-d.log", "*-*-*.log"), "several stars match");

            List<string> inc = Masks.Parse("*.log; *.txt,*.md");
            Eq(inc.Count, 3, "semicolons, commas, and spaces all separate");

            List<string> ex = Masks.Parse("*.min.*");
            Ok(Masks.Allows("app.log", inc, ex), "an included name is allowed");
            Ok(!Masks.Allows("app.zip", inc, ex), "a name outside the include list is refused");
            Ok(!Masks.Allows("app.min.log", inc, ex), "exclude beats include");
            Ok(Masks.Allows("anything.bin", new List<string>(), new List<string>()),
               "empty lists mean everything");
        }

        // ---- BOM and binary ----------------------------------------------

        static void BomTests()
        {
            int n;
            Eq(TextFiles.DetectBom(new byte[] { 0xEF, 0xBB, 0xBF, 0x41 }, out n).WebName,
               "utf-8", "UTF-8 BOM detected");
            Eq(n, 3, "UTF-8 BOM is three bytes");

            Eq(TextFiles.DetectBom(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, out n).WebName,
               "utf-16", "UTF-16LE BOM detected");

            // The ordering trap: FF FE is a prefix of the UTF-32LE mark, so a
            // UTF-32 file must not be claimed by the UTF-16 test.
            Eq(TextFiles.DetectBom(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, out n).WebName,
               "utf-32", "UTF-32LE BOM is not mistaken for UTF-16LE");
            Eq(n, 4, "UTF-32 BOM is four bytes");

            Ok(TextFiles.DetectBom(Encoding.ASCII.GetBytes("plain"), out n) == null,
               "a plain file reports no BOM");
        }

        static void BinaryTests()
        {
            Ok(TextFiles.LooksBinary(new byte[] { 0x41, 0x00, 0x42 }), "a NUL byte means binary");
            Ok(!TextFiles.LooksBinary(Encoding.ASCII.GetBytes("root@LAB1:~# smartctl")),
               "an ASCII log is not binary");

            // UTF-16 text is roughly half NUL bytes. Sniffing before consulting
            // the BOM discards every UTF-16 file in the folder.
            byte[] utf16 = Encoding.Unicode.GetPreamble();
            byte[] body = Encoding.Unicode.GetBytes("hello");
            byte[] all = new byte[utf16.Length + body.Length];
            Buffer.BlockCopy(utf16, 0, all, 0, utf16.Length);
            Buffer.BlockCopy(body, 0, all, utf16.Length, body.Length);
            Ok(!TextFiles.LooksBinary(all), "UTF-16 with a BOM is text, not binary");

            // A NUL past the sniff window does not condemn the file.
            byte[] late = new byte[TextFiles.SniffBytes + 16];
            for (int i = 0; i < late.Length; i++) late[i] = 0x41;
            late[late.Length - 1] = 0x00;
            Ok(!TextFiles.LooksBinary(late), "a NUL past the sniff window is not judged");
        }

        static void DecodeTests()
        {
            Encoding enc;
            bool bom;

            Eq(TextFiles.Decode(Encoding.UTF8.GetBytes("nvme0"), out enc, out bom), "nvme0",
               "ASCII decodes as itself");
            Ok(!bom, "no BOM reported for a plain file");

            byte[] withBom = new byte[] { 0xEF, 0xBB, 0xBF, 0x61 };
            Eq(TextFiles.Decode(withBom, out enc, out bom), "a", "the BOM is not part of the text");
            Ok(bom, "the BOM is reported");

            // The fallback that matters: a Windows-1252 byte is not valid
            // UTF-8. A lenient decode would swallow it as U+FFFD and report
            // success, and a search for the character would then find nothing.
            byte[] ansi = new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9 };
            string text = TextFiles.Decode(ansi, out enc, out bom);
            Ok(text.IndexOf((char)0xFFFD) < 0,
               "an ANSI byte is not decoded into a replacement character");
            Eq(text.Length, 4, "the ANSI fallback keeps every character");
        }

        static void SplitTests()
        {
            NewlineStyle style;
            string[] lines;

            lines = TextFiles.SplitLines("a\r\nb\r\nc", out style);
            Eq(lines.Length, 3, "CRLF splits into three");
            Eq(lines[0], "a", "the CR is trimmed from the line text");
            Eq(style, NewlineStyle.Crlf, "CRLF is reported");

            lines = TextFiles.SplitLines("a\nb\n", out style);
            Eq(lines.Length, 2, "a trailing newline does not add a phantom line");
            Eq(style, NewlineStyle.Lf, "LF is reported");

            lines = TextFiles.SplitLines("a\r\nb\nc", out style);
            Eq(style, NewlineStyle.Mixed, "mixed endings are reported as mixed");

            lines = TextFiles.SplitLines("only", out style);
            Eq(lines.Length, 1, "a file with no newline is one line");
            Eq(style, NewlineStyle.None, "no terminator is reported as none");
        }

        static void AnsiTests()
        {
            string colored = Esc + "[1;32mroot@LAB1" + Esc + "[0m:~# smartctl";
            Eq(TextFiles.StripAnsi(colored), "root@LAB1:~# smartctl", "CSI color codes are stripped");

            string title = Esc + "]0;root@LAB1: ~" + Bel + "prompt";
            Eq(TextFiles.StripAnsi(title), "prompt", "an OSC title sequence is stripped whole");

            // ST-terminated OSC. A terminal that avoids BEL writes this form
            // instead, and a strip that only knows BEL eats the rest of the
            // line looking for one.
            string st = Esc + "]0;title" + Esc + "\\after";
            Eq(TextFiles.StripAnsi(st), "after", "an OSC terminated by ST is stripped");

            // The ordering trap that is real: the catch-all two-character rule
            // accepts "[" and "]" as its final byte, so running it ahead of the
            // CSI and OSC rules decapitates them and leaves their payloads
            // behind as text.
            string both = Esc + "]0;root@LAB1: ~" + Bel + Esc + "[1;32mroot@LAB1" + Esc + "[0m:~#";
            Eq(TextFiles.StripAnsi(both), "root@LAB1:~#",
               "an OSC and a CSI on one line are both stripped whole");

            Eq(TextFiles.StripAnsi("plain text"), "plain text", "clean text is returned untouched");
            Eq(TextFiles.StripAnsi("col1\tcol2"), "col1\tcol2", "tabs survive the strip");
        }

        // ---- the engine, end to end ---------------------------------------

        static void EngineTests_EndToEnd(string root)
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            string sub = Path.Combine(root, "nested");
            Directory.CreateDirectory(sub);

            File.WriteAllText(Path.Combine(root, "LAB1.log"),
                "line one\r\nroot@LAB1:~# smartctl -a /dev/nvme0\r\nline three\r\nsmartctl again\r\n");
            File.WriteAllText(Path.Combine(root, "notes.txt"),
                "no hits here\r\n");
            File.WriteAllText(Path.Combine(sub, "LAB2.log"),
                "root@LAB2:~# smartctl -a /dev/sda\r\n");
            File.WriteAllBytes(Path.Combine(root, "dump.bin"),
                new byte[] { 0x73, 0x6D, 0x61, 0x72, 0x74, 0x63, 0x74, 0x6C, 0x00, 0x41 });
            File.WriteAllText(Path.Combine(root, "session-raw.log"),
                Esc + "[1;32mroot@LAB3:~#" + Esc + "[0m smartctl -a /dev/nvme1\r\n");

            SearchOptions o = NewOptions(root, "smartctl");
            Dictionary<string, FileHits> byName = new Dictionary<string, FileHits>();
            SearchProgress p = RunSearch(o, byName);

            Eq(p.Hits, 4, "recursive search finds every hit in text files");
            Eq(p.FilesMatched, 3, "three files match");
            Ok(!byName.ContainsKey("dump.bin"), "a file with a NUL byte is skipped as binary");
            Ok(byName.ContainsKey("LAB2.log"), "a subfolder is searched");
            Eq(byName["LAB1.log"].Hits.Count, 2, "two hits in the same file are both reported");
            Eq(byName["LAB1.log"].Hits[0].LineNumber, 2, "line numbers are 1-based");
            Eq(byName["LAB1.log"].RelativePath, "LAB1.log", "the relative path drops the root");
            Eq(byName["LAB2.log"].RelativePath, Path.Combine("nested", "LAB2.log"),
               "a nested relative path keeps its folder");

            // The raw-mode log matches without stripping only because the
            // escape sits before the word. Strip-ANSI is what makes the whole
            // line readable in the results, and it flags the file unsafe to
            // rewrite because the offsets no longer index the bytes on disk.
            Ok(byName["session-raw.log"].Hits[0].Line.IndexOf(Esc) >= 0,
               "without strip-ANSI the escapes are still in the reported line");
            Ok(byName["session-raw.log"].IsSafeToRewrite,
               "an untransformed file is safe to rewrite");

            o = NewOptions(root, "smartctl");
            o.StripAnsi = true;
            byName.Clear();
            RunSearch(o, byName);
            Eq(byName["session-raw.log"].Hits[0].Line, "root@LAB3:~# smartctl -a /dev/nvme1",
               "strip-ANSI reports the line a person saw");
            Ok(!byName["session-raw.log"].IsSafeToRewrite,
               "a transformed file refuses to be rewritten");

            o = NewOptions(root, "smartctl");
            o.IncludeSubfolders = false;
            byName.Clear();
            p = RunSearch(o, byName);
            Ok(!byName.ContainsKey("LAB2.log"), "subfolders are skipped when the box is unchecked");

            o = NewOptions(root, "smartctl");
            o.IncludeMasks = "*.txt";
            byName.Clear();
            p = RunSearch(o, byName);
            Eq(p.Hits, 0, "a mask that excludes every match returns nothing");

            o = NewOptions(root, "smartctl");
            o.ExcludeBinary = false;
            byName.Clear();
            RunSearch(o, byName);
            Ok(byName.ContainsKey("dump.bin"), "unchecking exclude-binary searches the binary too");

            o = NewOptions(root, "smartctl");
            o.ContextBefore = 1;
            o.ContextAfter = 1;
            byName.Clear();
            RunSearch(o, byName);
            Eq(byName["LAB1.log"].Hits[0].Before[0], "line one", "one line of context before");
            Eq(byName["LAB1.log"].Hits[0].After[0], "line three", "one line of context after");
            Eq(byName["LAB2.log"].Hits[0].Before.Length, 0,
               "context before the first line is empty rather than out of range");

            o = NewOptions(root, "smartctl");
            o.MaxFileMegabytes = 0;   // treated as no limit, so this must still find everything
            byName.Clear();
            p = RunSearch(o, byName);
            Eq(p.Hits, 4, "a zero size cap means no cap");

            o = NewOptions(root, "smartctl");
            o.MaxHitsPerFile = 1;
            byName.Clear();
            p = RunSearch(o, byName);
            Eq(byName["LAB1.log"].Hits.Count, 1, "the per-file cap holds");
            Ok(byName["LAB1.log"].Truncated, "a capped file says so");
            Ok(p.Truncated, "a capped run says so, rather than implying that is all there is");

            // Cancel has to be answered even mid-scan.
            o = NewOptions(root, "smartctl");
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();
            SearchProgress cancelled = new SearchEngine(o).Run(cts.Token, null, null, null);
            Ok(cancelled.Cancelled, "an already-cancelled token stops the search");

            // A folder that does not exist is an error report, not a crash.
            o = NewOptions(Path.Combine(root, "missing"), "smartctl");
            List<string> errors = new List<string>();
            SearchProgress bad = new SearchEngine(o).Run(CancellationToken.None, null, null,
                delegate(string path, string message) { errors.Add(path); });
            Ok(errors.Count > 0, "an unreadable folder is reported through onError");
            Ok(bad.Finished, "the search still finishes after an unreadable folder");
        }

        static SearchOptions NewOptions(string root, string query)
        {
            SearchOptions o = new SearchOptions();
            o.Root = root;
            o.Query = query;
            return o;
        }

        static SearchProgress RunSearch(SearchOptions o, Dictionary<string, FileHits> byName)
        {
            SearchEngine engine = new SearchEngine(o);
            return engine.Run(CancellationToken.None,
                delegate(FileHits fh) { byName[Path.GetFileName(fh.Path)] = fh; },
                null, null);
        }
    }
}
