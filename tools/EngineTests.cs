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
using System.IO.Compression;
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
            //
            // In its OWN named subdirectory, not testdata itself. The first
            // version deleted the whole of testdata on the way in and on the
            // way out, which is exactly the "establish a starting state by
            // deleting everything" that the house rule forbids - and it took
            // about an hour to bite, silently eating a sample corpus that was
            // sitting beside it under the same parent.
            string root = Path.Combine(
                Path.Combine(
                    Path.GetDirectoryName(Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location)),
                    "testdata"),
                "engine-tests");

            try
            {
                MatcherTests();
                MaskTests();
                BomTests();
                BinaryTests();
                DecodeTests();
                SplitTests();
                AnsiTests();
                WorkbookTests();
                DocumentTests();
                ScrollTests();
                FilterTests();
                CaseTests();
                EngineTests_EndToEnd(root);
                OfficeEndToEnd(root);
                ReplaceTests(root);

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
                // Removes only this run's own directory, by name, leaving
                // anything else under testdata alone.
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

        // ---- Office extraction -----------------------------------------------

        // The fixtures are built with the same in-box zip writer the extractor
        // reads with, rather than committed as binary blobs. A checked-in
        // .xlsx is a file nobody can review in a diff, and one that drifts out
        // of step with what the test claims it contains is worse than no test.
        static byte[] Zip(params string[] namesAndContents)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    for (int i = 0; i < namesAndContents.Length; i += 2)
                    {
                        ZipArchiveEntry e = zip.CreateEntry(namesAndContents[i]);
                        using (StreamWriter w = new StreamWriter(e.Open(), new UTF8Encoding(false)))
                            w.Write(namesAndContents[i + 1]);
                    }
                }
                return ms.ToArray();
            }
        }

        const string SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        static byte[] SampleWorkbook()
        {
            return Zip(
                "xl/workbook.xml",
                "<workbook xmlns=\"" + SheetNs + "\" xmlns:r=\"" + RelNs + "\">"
                + "<sheets><sheet name=\"Rollout Plan\" sheetId=\"1\" r:id=\"rId1\"/>"
                + "<sheet name=\"Notes\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>",

                "xl/_rels/workbook.xml.rels",
                "<Relationships><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/>"
                + "<Relationship Id=\"rId2\" Target=\"worksheets/sheet2.xml\"/></Relationships>",

                "xl/sharedStrings.xml",
                "<sst xmlns=\"" + SheetNs + "\">"
                + "<si><t>decommission the array</t></si>"
                + "<si><r><t>rich </t></r><r><t>text run</t></r></si>"
                + "<si><t>phonetic</t><rPh><t>SHOULD NOT MATCH</t></rPh></si>"
                + "</sst>",

                "xl/worksheets/sheet1.xml",
                "<worksheet xmlns=\"" + SheetNs + "\"><sheetData>"
                + "<row r=\"14\"><c r=\"B14\" t=\"s\"><v>0</v></c>"
                + "<c r=\"C14\"><v>4200</v></c></row>"
                + "<row r=\"15\"><c r=\"B15\" t=\"s\"><v>1</v></c>"
                + "<c r=\"C15\" t=\"inlineStr\"><is><t>inline value</t></is></c></row>"
                + "<row r=\"16\"><c r=\"B16\" t=\"s\"><v>2</v></c>"
                + "<c r=\"D16\"><f>SUM(C14:C15)</f><v>4200</v></c></row>"
                // A formula with no cached result, which is what an
                // uncalculated cell looks like. It is also the only shape that
                // can betray a reader that has started treating <f> as a
                // value: where a cached <v> exists it overwrites the formula
                // and hides the mistake.
                + "<row r=\"17\"><c r=\"B17\"><f>SUM(C14:C16)</f></c></row>"
                + "</sheetData></worksheet>",

                "xl/worksheets/sheet2.xml",
                "<worksheet xmlns=\"" + SheetNs + "\"><sheetData>"
                + "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>second sheet</t></is></c></row>"
                + "</sheetData></worksheet>");
        }

        static void WorkbookTests()
        {
            string error;
            List<OfficeLine> lines = OfficeText.Extract(SampleWorkbook(), "plan.xlsx", out error);
            Ok(lines != null, "a workbook extracts");
            Ok(error == null, "a good workbook reports no error");

            Dictionary<string, string> byCell = new Dictionary<string, string>();
            foreach (OfficeLine l in lines) byCell[l.Location] = l.Text;

            Eq(byCell["Rollout Plan!B14"], "decommission the array",
               "a shared string resolves through its index");
            Eq(byCell["Rollout Plan!C14"], "4200", "a numeric cell is searchable as stored");
            Eq(byCell["Rollout Plan!B15"], "rich text run",
               "a rich text run is joined into one value");
            Eq(byCell["Rollout Plan!C15"], "inline value", "an inline string is read");
            Eq(byCell["Notes!A1"], "second sheet", "every sheet in the workbook is read");
            Eq(byCell["Rollout Plan!B16"], "phonetic",
               "a phonetic run is not appended to the value it annotates");

            // The formula is skipped and its cached result kept, so searching
            // for SUM does not answer with every totaled column in the book.
            Eq(byCell["Rollout Plan!D16"], "4200", "a formula cell yields its result, not its formula");
            Ok(!byCell.ContainsKey("Rollout Plan!B17"),
               "an uncalculated formula cell contributes nothing");
            foreach (OfficeLine l in lines)
                Ok(l.Text.IndexOf("SUM(", StringComparison.Ordinal) < 0,
                   "no extracted value contains the formula text");

            // The sheet label comes from the workbook part through the
            // relationship, not from the part filename.
            Ok(!byCell.ContainsKey("sheet1!B14"), "sheets are labeled by name, not by part filename");

            // A workbook with no usable workbook.xml still gives up its cells
            // rather than returning nothing.
            byte[] headless = Zip(
                "xl/worksheets/sheet1.xml",
                "<worksheet xmlns=\"" + SheetNs + "\"><sheetData>"
                + "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>orphan</t></is></c></row>"
                + "</sheetData></worksheet>");
            lines = OfficeText.Extract(headless, "broken.xlsx", out error);
            Eq(lines.Count, 1, "a workbook with no workbook part still yields its cells");
            Eq(lines[0].Location, "sheet1!A1", "the fallback label is the part filename");

            // A file that is not a zip is reported, not passed off as a file
            // with no matches. That distinction is the whole point: silence
            // reads as "the phrase is not there".
            lines = OfficeText.Extract(Encoding.ASCII.GetBytes("not a zip at all"),
                                       "fake.xlsx", out error);
            Ok(lines == null, "a non-zip .xlsx does not extract");
            Ok(error != null, "a non-zip .xlsx reports why");

            // These files come from other people. An XML parser that expands
            // entities turns a folder search into a file reader pointed
            // wherever the document says, so the DTD is refused outright and
            // the file is reported as unreadable rather than parsed partially.
            byte[] withDtd = Zip(
                "xl/workbook.xml",
                "<workbook xmlns=\"" + SheetNs + "\" xmlns:r=\"" + RelNs + "\">"
                + "<sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
                "xl/_rels/workbook.xml.rels",
                "<Relationships><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/></Relationships>",
                "xl/sharedStrings.xml",
                "<!DOCTYPE sst [<!ENTITY leak \"ENTITY-WAS-EXPANDED\">]>"
                + "<sst xmlns=\"" + SheetNs + "\"><si><t>&leak;</t></si></sst>",
                "xl/worksheets/sheet1.xml",
                "<worksheet xmlns=\"" + SheetNs + "\"><sheetData>"
                + "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c></row></sheetData></worksheet>");

            lines = OfficeText.Extract(withDtd, "hostile.xlsx", out error);
            bool expanded = false;
            if (lines != null)
                foreach (OfficeLine l in lines)
                    if (l.Text.IndexOf("ENTITY-WAS-EXPANDED", StringComparison.Ordinal) >= 0)
                        expanded = true;
            Ok(!expanded, "a DTD in an Office file is refused, not expanded");
            Ok(error != null, "a file carrying a DTD is reported rather than passed over in silence");

            Ok(OfficeText.IsSupported("a.xlsx"), ".xlsx is supported");
            Ok(OfficeText.IsSupported("a.XLSM"), "the extension test is case-insensitive");
            Ok(OfficeText.IsKnownUnreadable("a.pdf"), ".pdf is named as unreadable");
            Ok(OfficeText.IsKnownUnreadable("a.xls"), ".xls is named as unreadable");
            Ok(!OfficeText.IsKnownUnreadable("a.log"), "an ordinary log is not called unreadable");
        }

        static byte[] SampleDocument()
        {
            return Zip(
                "word/document.xml",
                "<w:document xmlns:w=\"" + WordNs + "\"><w:body>"
                + "<w:p><w:r><w:t>The migration window is </w:t></w:r>"
                + "<w:r><w:t>2026-09-14</w:t></w:r></w:p>"
                + "<w:p><w:r><w:t>Column</w:t></w:r><w:r><w:tab/><w:t>Value</w:t></w:r></w:p>"
                + "<w:p><w:del><w:r><w:t>retired paragraph</w:t></w:r></w:del></w:p>"
                + "<w:p><w:r><w:t>kept</w:t></w:r>"
                + "<w:del><w:r><w:t>REMOVED</w:t></w:r></w:del>"
                + "<w:r><w:t> tail</w:t></w:r></w:p>"
                + "</w:body></w:document>",

                "word/header1.xml",
                "<w:hdr xmlns:w=\"" + WordNs + "\">"
                + "<w:p><w:r><w:t>CHANGE-2026-114</w:t></w:r></w:p></w:hdr>",

                "word/footnotes.xml",
                "<w:footnotes xmlns:w=\"" + WordNs + "\">"
                + "<w:p><w:r><w:t>see the runbook</w:t></w:r></w:p></w:footnotes>");
        }

        static void DocumentTests()
        {
            string error;
            List<OfficeLine> lines = OfficeText.Extract(SampleDocument(), "plan.docx", out error);
            Ok(lines != null, "a document extracts");
            Ok(error == null, "a good document reports no error");

            Dictionary<string, string> byWhere = new Dictionary<string, string>();
            foreach (OfficeLine l in lines) byWhere[l.Location] = l.Text;

            // Runs are joined, which is what makes a phrase findable at all:
            // Word splits a sentence across runs at every formatting change,
            // so a naive per-run read cannot match across a bolded word.
            Eq(byWhere["Paragraph 1"], "The migration window is 2026-09-14",
               "runs in a paragraph are joined into one line");
            Eq(byWhere["Paragraph 2"], "Column\tValue", "a tab element becomes a tab");

            // A header is where a change number lives, and it is exactly what
            // someone searches a folder of documents for.
            Eq(byWhere["Header 1, paragraph 1"], "CHANGE-2026-114", "headers are searched");
            Eq(byWhere["Footnote, paragraph 1"], "see the runbook", "footnotes are searched");

            // Tracked deletions are not in the document any more.
            foreach (OfficeLine l in lines)
                Ok(l.Text.IndexOf("REMOVED", StringComparison.Ordinal) < 0,
                   "text inside a tracked deletion is not extracted");
            Ok(!byWhere.ContainsValue("retired paragraph"),
               "a paragraph that is entirely a deletion produces no line");
            Eq(byWhere["Paragraph 4"], "kept tail",
               "a deletion inside a paragraph leaves the surviving text joined");
        }

        // ---- the engine, end to end ---------------------------------------

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

        // Office files going through the real engine, which is where the
        // ordering trap lives: a .xlsx is a zip, a zip is full of NUL bytes,
        // and the binary sniff is right about that. If the office branch runs
        // after the sniff, the exclude-binary default silently discards the
        // one format this feature exists to read.
        static void OfficeEndToEnd(string root)
        {
            string dir = Path.Combine(root, "office");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "rollout.xlsx"), SampleWorkbook());
            File.WriteAllBytes(Path.Combine(dir, "runbook.docx"), SampleDocument());
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "decommission the array\r\n");
            File.WriteAllBytes(Path.Combine(dir, "scan.pdf"), new byte[] { 0x25, 0x50, 0x44, 0x46 });

            SearchOptions o = NewOptions(dir, "decommission");
            Dictionary<string, FileHits> byName = new Dictionary<string, FileHits>();
            SearchProgress p = RunSearch(o, byName);

            Ok(byName.ContainsKey("rollout.xlsx"),
               "a workbook is searched even though exclude-binary is on");
            Eq(byName["rollout.xlsx"].Hits[0].Location, "Rollout Plan!B14",
               "a workbook hit reports its cell, not a line number");
            Ok(byName.ContainsKey("notes.txt"), "ordinary files are still searched alongside");
            Eq(p.FilesMatched, 2, "both the workbook and the text file match");

            // The PDF is counted and named rather than folded into the skip
            // total. A folder of PDFs answering "no hits" is a missing
            // capability wearing the clothes of an answer.
            Eq(p.FilesUnsupported, 1, "an unreadable format is counted");
            Eq(p.UnsupportedKinds, ".pdf", "the unreadable format is named");

            o = NewOptions(dir, "migration window");
            byName.Clear();
            RunSearch(o, byName);
            Ok(byName.ContainsKey("runbook.docx"),
               "a phrase split across Word runs is still found");

            o = NewOptions(dir, "CHANGE-2026-114");
            byName.Clear();
            RunSearch(o, byName);
            Eq(byName["runbook.docx"].Hits[0].Location, "Header 1, paragraph 1",
               "a hit in a header reports where it is");

            // Context lines are meaningless for a cell and are not offered.
            o = NewOptions(dir, "decommission");
            o.ContextBefore = 2;
            o.ContextAfter = 2;
            byName.Clear();
            RunSearch(o, byName);
            Ok(byName["rollout.xlsx"].Hits[0].Before == null,
               "a workbook hit carries no context lines");
            Ok(byName["notes.txt"].Hits[0].Before != null,
               "a text hit in the same run still carries context");

            // Nothing extracted is safe to write back.
            Ok(!byName["rollout.xlsx"].IsSafeToRewrite,
               "an extracted workbook refuses to be rewritten");

            Directory.Delete(dir, true);
        }

        // ---- the results pane's scroll rule ------------------------------------

        static void ScrollTests()
        {
            // The reported bug: a long search, scrolled down to read, then a
            // short one. The viewport ends up past the last row and the pane
            // renders blank - with too few rows to grow a scrollbar, so there
            // is no way to scroll back and the results are simply invisible.
            Ok(ViewRules.NeedsScrollHome(381, 16),
               "a short result set after a scrolled long one pulls the view home");
            Ok(ViewRules.NeedsScrollHome(10, 0),
               "clearing the list pulls the view home");

            // Never scrolled, so there is nothing to correct.
            Ok(!ViewRules.NeedsScrollHome(0, 16),
               "a list already at the top is left alone");

            // Growing is the streaming case. Yanking the view to the top on
            // every batch would fight someone reading what has arrived, and it
            // never happens because a valid top index is below the count.
            Ok(!ViewRules.NeedsScrollHome(0, 100), "an empty list filling up is left alone");
            Ok(!ViewRules.NeedsScrollHome(50, 200),
               "a scrolled list that grows keeps the reader's place");

            // Collapsing one group shrinks the list without invalidating where
            // the reader was.
            Ok(!ViewRules.NeedsScrollHome(5, 940),
               "collapsing a group above the fold keeps the reader's place");
            Ok(ViewRules.NeedsScrollHome(950, 940),
               "collapsing enough to pass the reader's place pulls the view home");

            // The boundary: a top index equal to the new count is one row past
            // the end, because indexes are zero-based.
            Ok(ViewRules.NeedsScrollHome(16, 16), "a top index of exactly the count is past the end");
            Ok(!ViewRules.NeedsScrollHome(15, 16), "the last valid index is not past the end");
        }

        // ---- the results filter ---------------------------------------------------

        static void FilterTests()
        {
            const string Log = "[ssh 192.0.2.14] (2026-08-24_170524) LAB4.log";
            const string Line = "root@LAB4:~# smartctl -a /dev/nvme0";
            const string Other = "[ssh 192.0.2.11] (2026-08-21_170521) LAB2.log";

            // No filter admits everything, which is the state the pane spends
            // most of its life in.
            Ok(ViewRules.FileKeepsEverything("", Log), "an empty filter keeps every file");
            Ok(ViewRules.HitIsShown("", Other, "anything", null), "an empty filter keeps every hit");

            // The case this exists for: the host is in the filename, not in
            // the matched lines, so matching the name has to keep the file
            // whole. Keeping only lines containing LAB4 would answer with
            // almost nothing and look broken.
            Ok(ViewRules.FileKeepsEverything("LAB4", Log), "a filter matching the filename keeps the file");
            Ok(ViewRules.HitIsShown("LAB4", Log, "a line with no host in it", null),
               "every hit in a matching file is kept, whatever the line says");
            Ok(!ViewRules.FileKeepsEverything("LAB4", Other), "a file whose name does not match is not kept whole");

            // A hit still earns its own place.
            Ok(ViewRules.HitIsShown("smartctl", Other, Line, null), "a hit matching the line is kept");
            Ok(!ViewRules.HitIsShown("nvme9", Other, Line, null), "a hit matching nothing is dropped");

            // Filtering by a spreadsheet cell reference, which is what a
            // workbook hit carries instead of a line number.
            Ok(ViewRules.HitIsShown("Schedule", Other, "decommission", "Schedule!A2"),
               "a hit matching its location label is kept");

            Ok(ViewRules.FileKeepsEverything("lab4", Log), "the filter is case-insensitive on names");
            Ok(ViewRules.HitIsShown("SMARTCTL", Other, Line, null), "the filter is case-insensitive on lines");

            // An IP or a timestamp out of the filename is as likely a way in
            // as the host is.
            Ok(ViewRules.FileKeepsEverything("192.0.2.14", Log), "filtering by address works");
            Ok(ViewRules.FileKeepsEverything("170524", Log), "filtering by the session timestamp works");

            Ok(!ViewRules.HitIsShown("LAB4", Other, null, null), "a null line does not match");
        }

        // ---- case preservation -------------------------------------------------

        // charcheck:spelling-off
        // The British spellings below are test data for the feature that
        // converts them, not prose. The scan is suspended for this method and
        // for the replace fixtures, and turned back on straight after.
        static void CaseTests()
        {
            // The British-to-American pass, which is what this exists for.
            Eq(Replacer.PreserveCase("colour", "color"), "color", "lower case stays lower");
            Eq(Replacer.PreserveCase("Colour", "color"), "Color", "a sentence opener stays capitalized");
            Eq(Replacer.PreserveCase("COLOUR", "color"), "COLOR", "a heading stays upper case");

            // Substring replacement carries the suffix along correctly, which
            // is why "colours" does not need its own rule.
            Eq(Replacer.PreserveCase("Colour", "color") + "s", "Colors", "a suffix follows the stem");

            // No shape to copy, so the replacement is used as written.
            Eq(Replacer.PreserveCase("cOloUr", "color"), "color", "mixed case is left as authored");
            Eq(Replacer.PreserveCase("2026", "2027"), "2027", "a match with no letters is left alone");

            // A replacement carrying its own capitals was written that way on
            // purpose. Reshaping it would be the tool overruling the person.
            Eq(Replacer.PreserveCase("tool", "RSFind"), "RSFind", "a capitalized replacement is literal");
            Eq(Replacer.PreserveCase("TOOL", "RSFind"), "RSFind",
               "an upper case match does not upper case a deliberate replacement");
            Eq(Replacer.PreserveCase("macos", "macOS"), "macOS", "an inner capital survives");

            Eq(Replacer.PreserveCase("", "color"), "color", "an empty match is harmless");
            Eq(Replacer.PreserveCase("Colour", ""), "", "an empty replacement is harmless");
        }

        // ---- replace, end to end -----------------------------------------------

        static FileHits FindOne(string dir, string query, string name, bool stripAnsi, bool regex)
        {
            SearchOptions o = NewOptions(dir, query);
            o.StripAnsi = stripAnsi;
            o.UseRegex = regex;
            Dictionary<string, FileHits> byName = new Dictionary<string, FileHits>();
            RunSearch(o, byName);
            FileHits fh;
            return byName.TryGetValue(name, out fh) ? fh : null;
        }

        static void ReplaceTests(string root)
        {
            string dir = Path.Combine(root, "replace");
            Directory.CreateDirectory(dir);
            string undo = Path.Combine(dir, "undo");

            ReplaceOptions ro = new ReplaceOptions();
            ro.Replacement = "color";

            // A documentation pass: three shapes of the same word, CRLF
            // endings, and no trailing newline on the last line.
            // The fourth line never matches. It is there so the three
            // integrity checks can be told apart: an edit confined to it
            // leaves every matched line intact, so only the timestamp can
            // catch it.
            string docPath = Path.Combine(dir, "guide.md");
            string original = "The colour table\r\nColour is set per theme\r\n"
                            + "COLOUR OVERRIDES\r\nunrelated trailing note";
            File.WriteAllText(docPath, original, new UTF8Encoding(false));

            FileHits fh = FindOne(dir, "colour", "guide.md", false, false);
            Ok(fh != null, "the documentation file is found");
            Eq(fh.Hits.Count, 3, "all three shapes of the word are found");

            Matcher m = new Matcher("colour", false, false, false);
            ReplacePlan plan = Replacer.Plan(fh, m, ro);
            Ok(plan.Refusal == null, "a plain text file can be planned");
            Eq(plan.Changes.Count, 3, "every hit becomes a change");
            Eq(plan.Changes[0].After, "The color table", "lower case is replaced in place");
            Eq(plan.Changes[1].After, "Color is set per theme", "the capital is preserved");
            Eq(plan.Changes[2].After, "COLOR OVERRIDES", "the upper case is preserved");

            List<ReplacePlan> plans = new List<ReplacePlan>();
            plans.Add(plan);
            ReplaceResult r = Replacer.Apply(plans, undo);
            Eq(r.FilesWritten, 1, "the file is written");
            Eq(r.ChangesWritten, 3, "all three changes are written");
            Eq(r.Failures.Count, 0, "nothing failed");

            string replaced = "The color table\r\nColor is set per theme\r\n"
                            + "COLOR OVERRIDES\r\nunrelated trailing note";
            string after = File.ReadAllText(docPath);
            Eq(after, replaced, "the file reads back exactly as previewed");
            // Rejoining split lines would normalize these away. Working on
            // offsets keeps them.
            Ok(after.IndexOf("\r\n", StringComparison.Ordinal) >= 0, "CRLF endings survive");
            Ok(!after.EndsWith("\n", StringComparison.Ordinal),
               "a file with no trailing newline does not gain one");

            // Undo puts it back, byte for byte.
            Ok(r.UndoDirectory != null, "an undo record was written");
            ReplaceResult undone = Replacer.Undo(r.UndoDirectory);
            Eq(undone.FilesWritten, 1, "undo restores the file");
            Eq(File.ReadAllText(docPath), original, "undo restores the original exactly");

            // Undo refuses a file edited since the replace, rather than
            // overwriting work someone did in between.
            File.WriteAllText(docPath, original, new UTF8Encoding(false));
            fh = FindOne(dir, "colour", "guide.md", false, false);
            plans.Clear();
            plans.Add(Replacer.Plan(fh, m, ro));
            r = Replacer.Apply(plans, undo);
            Eq(r.FilesWritten, 1, "the second run writes");
            File.WriteAllText(docPath, "someone else edited this\r\n", new UTF8Encoding(false));
            undone = Replacer.Undo(r.UndoDirectory);
            Eq(undone.FilesWritten, 0, "undo does not restore over a later edit");
            Eq(undone.Failures.Count, 1, "and says which file it left alone");
            Eq(File.ReadAllText(docPath), "someone else edited this\r\n",
               "the later edit is untouched");

            // Only the selected changes are written.
            File.WriteAllText(docPath, original, new UTF8Encoding(false));
            fh = FindOne(dir, "colour", "guide.md", false, false);
            plan = Replacer.Plan(fh, m, ro);
            plan.Changes[1].Selected = false;
            plans.Clear();
            plans.Add(plan);
            r = Replacer.Apply(plans, undo);
            Eq(r.ChangesWritten, 2, "an unchecked change is not counted");
            Eq(File.ReadAllText(docPath),
               "The color table\r\nColour is set per theme\r\n"
               + "COLOR OVERRIDES\r\nunrelated trailing note",
               "an unchecked change is not written");

            // Three integrity checks stand between a stale plan and a write,
            // and each is tested against the edit only it can catch. The
            // planted-defect run is what showed that one test covering all
            // three was really only exercising the first.

            // 1. Different size. The cheapest check catches this.
            File.WriteAllText(docPath, original, new UTF8Encoding(false));
            fh = FindOne(dir, "colour", "guide.md", false, false);
            File.WriteAllText(docPath, original + "\r\nappended", new UTF8Encoding(false));
            plan = Replacer.Plan(fh, m, ro);
            Ok(plan.Refusal != null, "a file that changed size since the search is refused");

            // 2. Same size, different timestamp, and the edit is confined to a
            // line with no matches - so every matched line still verifies and
            // the timestamp is the only thing left to notice.
            File.WriteAllText(docPath, original, new UTF8Encoding(false));
            fh = FindOne(dir, "colour", "guide.md", false, false);
            System.Threading.Thread.Sleep(20);
            string elsewhere = original.Replace("unrelated trailing note",
                                                "unrelated trailing NOTE");
            Eq(elsewhere.Length, original.Length, "the fixture edit really is the same length");
            File.WriteAllText(docPath, elsewhere, new UTF8Encoding(false));
            plan = Replacer.Plan(fh, m, ro);
            Ok(plan.Refusal != null, "an edit elsewhere in the file is refused too");

            // 3. Same size AND same timestamp, which a restore or a coarse
            // filesystem clock can produce. Only re-reading the line catches
            // it, and it is the check that makes the other two advisory.
            File.WriteAllText(docPath, original, new UTF8Encoding(false));
            fh = FindOne(dir, "colour", "guide.md", false, false);
            string forged = original.Replace("The colour table", "The colour TABLE");
            Eq(forged.Length, original.Length, "the forged edit really is the same length");
            File.WriteAllText(docPath, forged, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(docPath, fh.LastWriteUtc);
            plan = Replacer.Plan(fh, m, ro);
            Ok(plan.Refusal != null,
               "an edit that kept the size and the timestamp is caught by re-reading the line");

            // Extracted and transformed files are refused, with their own
            // reasons rather than a generic one.
            string officePath = Path.Combine(dir, "book.xlsx");
            File.WriteAllBytes(officePath, SampleWorkbook());
            FileHits office = FindOne(dir, "decommission", "book.xlsx", false, false);
            Ok(office != null, "the workbook is found");
            plan = Replacer.Plan(office, new Matcher("decommission", false, false, false), ro);
            Ok(plan.Refusal != null, "an extracted workbook is refused");
            Ok(plan.Refusal.IndexOf("Office", StringComparison.Ordinal) >= 0,
               "the refusal says it came from an Office file");

            string ansiPath = Path.Combine(dir, "session.log");
            File.WriteAllText(ansiPath, Esc + "[1;32mcolour" + Esc + "[0m here\r\n",
                              new UTF8Encoding(false));
            FileHits ansi = FindOne(dir, "colour", "session.log", true, false);
            Ok(ansi != null, "the raw log is found with strip-ANSI on");
            plan = Replacer.Plan(ansi, m, ro);
            Ok(plan.Refusal != null, "a file whose escapes were stripped is refused");
            Ok(plan.Refusal.IndexOf("Strip ANSI escapes", StringComparison.Ordinal) >= 0,
               "the refusal names the option to turn off");

            // The point of making Transformed conditional: a file with no
            // escapes is untouched by the strip, so it stays replaceable even
            // with the option on - which is the default.
            FileHits clean = FindOne(dir, "colour", "guide.md", true, false);
            Ok(clean != null, "a clean file is found with strip-ANSI on");
            Ok(clean.IsSafeToRewrite,
               "strip-ANSI does not make a file without escapes unreplaceable");

            // An ANSI-encoded file cannot store an em dash, and losing the
            // character silently is worse than refusing the file.
            string ansiEncoded = Path.Combine(dir, "legacy.txt");
            File.WriteAllBytes(ansiEncoded, new byte[] {
                (byte)'c', (byte)'o', (byte)'l', (byte)'o', (byte)'u', (byte)'r',
                (byte)' ', 0xE9, (byte)'\r', (byte)'\n' });
            FileHits legacy = FindOne(dir, "colour", "legacy.txt", false, false);
            Ok(legacy != null, "the ANSI file is found");
            ReplaceOptions greek = new ReplaceOptions();
            greek.Replacement = "χρωμα";   // a word in Greek
            plans.Clear();
            plans.Add(Replacer.Plan(legacy, m, greek));
            r = Replacer.Apply(plans, undo);
            Eq(r.FilesWritten, 0, "a replacement the encoding cannot store is not written");
            Eq(r.Failures.Count, 1, "and it is reported");

            // Regex mode substitutes groups.
            string rePath = Path.Combine(dir, "ports.conf");
            File.WriteAllText(rePath, "listen 9160\r\nlisten 9161\r\n", new UTF8Encoding(false));
            FileHits ports = FindOne(dir, "listen ([0-9]+)", "ports.conf", false, true);
            Ok(ports != null, "the regex search finds the file");
            Matcher rx = new Matcher("listen ([0-9]+)", false, false, true);
            ReplaceOptions groups = new ReplaceOptions();
            groups.Replacement = "bind $1";
            plans.Clear();
            plans.Add(Replacer.Plan(ports, rx, groups));
            r = Replacer.Apply(plans, undo);
            Eq(File.ReadAllText(rePath), "bind 9160\r\nbind 9161\r\n",
               "a regex group is substituted");

            // A literal search must not treat $1 as a group reference. Someone
            // replacing a price would be very surprised.
            File.WriteAllText(rePath, "cost 5\r\n", new UTF8Encoding(false));
            FileHits literal = FindOne(dir, "cost 5", "ports.conf", false, false);
            Matcher lit = new Matcher("cost 5", false, false, false);
            ReplaceOptions dollars = new ReplaceOptions();
            dollars.Replacement = "$1 each";
            plans.Clear();
            plans.Add(Replacer.Plan(literal, lit, dollars));
            r = Replacer.Apply(plans, undo);
            Eq(File.ReadAllText(rePath), "$1 each\r\n",
               "a literal replacement containing $1 is written as typed");

            Directory.Delete(dir, true);
        }
        // charcheck:spelling-on

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
