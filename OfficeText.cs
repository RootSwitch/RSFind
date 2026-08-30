// Reading the text out of .xlsx and .docx without a dependency.
//
// Both formats are a ZIP of XML, and .NET Framework 4.5 ships both a zip
// reader and an XML reader in the box. So the one thing every grep-shaped tool
// declines to do - answer "which spreadsheet has that phrase in it" - costs
// two in-box references and this file, rather than an Office install, an
// interop assembly, or a NuGet package that would have to be trusted.
//
// What comes out is a list of lines with a location label instead of a line
// number, because a line number means nothing in a workbook. A hit reports
// itself as Sheet1!B14, which is a thing you can type into the Name Box.
//
// C# 5 only (in-box csc).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace RSFind
{
    public class OfficeLine
    {
        public string Text;
        public string Location;   // "Sheet1!B14", "Paragraph 12", "Header 1, paragraph 3"
    }

    public static class OfficeText
    {
        // A zip of XML expands enormously, and these files arrive from
        // elsewhere. Both bounds are deliberately generous for a real document
        // and ruinous for a crafted one: a 40 KB workbook that claims to hold
        // a gigabyte of shared strings is not a workbook.
        public const long MaxEntryBytes = 96L * 1024 * 1024;
        public const int MaxExtractedChars = 24 * 1024 * 1024;

        // One cell, one run, one shared string. A million characters in a
        // single element is already far past anything a person authored, and
        // without this bound one giant <t> is enough on its own: the element
        // readers accumulate into a StringBuilder before any of the other
        // budgets are consulted.
        public const int MaxElementChars = 1024 * 1024;

        // Guessed from the extension rather than by sniffing the zip. A file
        // named .xlsx that is not one fails the parse and is reported; a
        // workbook named .dat is not something a folder search should be
        // opening speculatively.
        public static bool IsWorkbook(string path)
        {
            string e = Ext(path);
            return e == ".xlsx" || e == ".xlsm";
        }

        public static bool IsDocument(string path)
        {
            string e = Ext(path);
            return e == ".docx" || e == ".docm";
        }

        public static bool IsSupported(string path)
        {
            return IsWorkbook(path) || IsDocument(path);
        }

        // Formats a person would reasonably expect to be searched, that this
        // tool cannot read. Naming them is the point: the alternative is
        // returning nothing and letting the user conclude the phrase is not
        // there. The engine counts these and the summary line says so.
        public static bool IsKnownUnreadable(string path)
        {
            switch (Ext(path))
            {
                case ".pdf":
                case ".xls":
                case ".doc":
                case ".ppt":
                case ".pptx":
                case ".rtf":
                case ".odt":
                case ".ods":
                case ".one":
                case ".msg":
                    return true;
                default:
                    return false;
            }
        }

        static string Ext(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return Path.GetExtension(path).ToLowerInvariant();
        }

        // Returns null when the file is not one of ours or cannot be parsed.
        // 'error' carries a reason worth showing; a corrupt document should say
        // so rather than pass silently as a file with no matches.
        public static List<OfficeLine> Extract(byte[] bytes, string path, out string error)
        {
            error = null;
            if (bytes == null || bytes.Length == 0) return null;

            try
            {
                using (MemoryStream ms = new MemoryStream(bytes, false))
                using (ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    if (IsWorkbook(path)) return Workbook(zip);
                    if (IsDocument(path)) return Document(zip);
                    return null;
                }
            }
            catch (InvalidDataException ex)
            {
                error = "not a readable Office file (" + ex.Message + ")";
                return null;
            }
            catch (XmlException ex)
            {
                error = "malformed Office XML (" + ex.Message + ")";
                return null;
            }
            catch (NotSupportedException ex)
            {
                error = "unsupported Office packaging (" + ex.Message + ")";
                return null;
            }
            // Everything else, deliberately.
            //
            // The named list above covers the failures a merely corrupt file
            // produces. A crafted one is not limited to those: a zip entry name
            // is arbitrary bytes, while a Windows path name is not, so an entry
            // called sheet"1".xml reaches Path.GetFileNameWithoutExtension in
            // the Sheets fallback and raises ArgumentException - which used to
            // escape this method, fault the scanning task, and leave the window
            // stuck on "Searching..." with Cancel unable to recover it.
            //
            // Enumerating what a hostile archive can throw is a losing game.
            // The contract this method actually wants is "any failure means it
            // is not a readable Office file", which is what this says.
            catch (Exception ex)
            {
                error = "could not be read as an Office file (" + ex.GetType().Name
                      + ": " + ex.Message + ")";
                return null;
            }
        }

        // ---- shared plumbing --------------------------------------------------

        // A stream that stops rather than trusting what the archive claims.
        //
        // ZipArchiveEntry.Length is the uncompressed size recorded in the
        // central directory - a number the file supplies about itself. An
        // archive that declares 1 KB and delivers 400 MB passes any check made
        // against it, and DeflateStream produces the 400 MB regardless. So the
        // declared size is kept as a cheap first pass and this bounds the
        // actual delivery, which is the only figure that costs memory.
        class BoundedStream : Stream
        {
            readonly Stream inner;
            readonly long limit;
            long read;

            public BoundedStream(Stream inner, long limit)
            {
                this.inner = inner;
                this.limit = limit;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = inner.Read(buffer, offset, count);
                read += n;
                if (read > limit)
                    throw new InvalidDataException(
                        "the file expands past the " + (limit / (1024 * 1024)).ToString(
                            CultureInfo.InvariantCulture) + " MB limit for one part");
                return n;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
            public override void SetLength(long v) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
        }

        // DTD processing off and no resolver. These files come from other
        // people, and an XML parser that will fetch an external entity is a
        // way to make a file search reach the network and read local files it
        // was never pointed at. Neither format needs a DTD.
        static XmlReader Open(ZipArchiveEntry entry)
        {
            if (entry == null) return null;
            // Cheap first pass on the declared size; the real bound is below.
            if (entry.Length > MaxEntryBytes) return null;

            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Prohibit;
            settings.XmlResolver = null;
            settings.IgnoreComments = true;
            settings.IgnoreProcessingInstructions = true;
            // Whitespace is content here: a cell or a run can legitimately be
            // " - " and xml:space="preserve" is how the format says so.
            settings.IgnoreWhitespace = false;
            settings.CheckCharacters = false;
            // CloseInput so disposing the reader closes the bounded stream and
            // the entry stream under it, rather than leaving both to the
            // archive's own disposal.
            settings.CloseInput = true;
            // The bound that actually holds, and the one that took a
            // measurement to find.
            //
            // Capping what this file appends is not enough, because XmlReader
            // materializes a whole text node into a string before handing it
            // over: by the time r.Value can be inspected, a single 400 MB <t>
            // has already been allocated. Bounding the StringBuilder after that
            // point measures a cost that has been paid. MaxCharactersInDocument
            // is checked during parsing, so it throws XmlException - which
            // Extract already reports as a malformed file - before the
            // allocation happens.
            settings.MaxCharactersInDocument = MaxExtractedChars;
            return XmlReader.Create(new BoundedStream(entry.Open(), MaxEntryBytes), settings);
        }

        static ZipArchiveEntry Find(ZipArchive zip, string name)
        {
            // Package paths are case sensitive in the spec and inconsistent in
            // the wild, and always use forward slashes.
            foreach (ZipArchiveEntry e in zip.Entries)
                if (string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase))
                    return e;
            return null;
        }

        // ---- .xlsx ------------------------------------------------------------

        static List<OfficeLine> Workbook(ZipArchive zip)
        {
            List<string> shared = SharedStrings(zip);
            List<OfficeLine> lines = new List<OfficeLine>();
            int budget = MaxExtractedChars;

            foreach (KeyValuePair<string, string> sheet in Sheets(zip))
            {
                ZipArchiveEntry entry = Find(zip, sheet.Key);
                if (entry == null) continue;
                using (XmlReader r = Open(entry))
                {
                    if (r == null) continue;
                    ReadSheet(r, sheet.Value, shared, lines, ref budget);
                }
                if (budget <= 0) break;
            }
            return lines;
        }

        // Sheet part path -> sheet name, in workbook order. Falls back to the
        // part's own filename when the relationship cannot be followed, which
        // is better than dropping a sheet because its label is unavailable.
        static List<KeyValuePair<string, string>> Sheets(ZipArchive zip)
        {
            List<KeyValuePair<string, string>> found = new List<KeyValuePair<string, string>>();

            Dictionary<string, string> rels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (XmlReader r = Open(Find(zip, "xl/_rels/workbook.xml.rels")))
            {
                while (r != null && r.Read())
                {
                    if (r.NodeType != XmlNodeType.Element || r.LocalName != "Relationship") continue;
                    string id = r.GetAttribute("Id");
                    string target = r.GetAttribute("Target");
                    if (id == null || target == null) continue;
                    if (target.StartsWith("/", StringComparison.Ordinal)) target = target.Substring(1);
                    else target = "xl/" + target;
                    rels[id] = target.Replace('\\', '/');
                }
            }

            using (XmlReader r = Open(Find(zip, "xl/workbook.xml")))
            {
                while (r != null && r.Read())
                {
                    if (r.NodeType != XmlNodeType.Element || r.LocalName != "sheet") continue;
                    string name = r.GetAttribute("name");
                    string id = r.GetAttribute("id",
                        "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    if (id == null) id = r.GetAttribute("r:id");
                    string target;
                    if (id != null && rels.TryGetValue(id, out target))
                        found.Add(new KeyValuePair<string, string>(target, name != null ? name : target));
                }
            }

            if (found.Count > 0) return found;

            // No usable workbook part: take every worksheet in the package and
            // label it by filename.
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                if (!e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                found.Add(new KeyValuePair<string, string>(e.FullName,
                    Path.GetFileNameWithoutExtension(e.FullName)));
            }
            return found;
        }

        // Budgeted, like the sheet and paragraph readers.
        //
        // This table is where a workbook's text actually lives, and it had no
        // budget at all - so the one part most worth bounding was the one part
        // that was not. A 63 KB archive holding a single huge shared string
        // cost 640 MB of heap and returned no lines whatsoever.
        static List<string> SharedStrings(ZipArchive zip)
        {
            List<string> strings = new List<string>();
            int budget = MaxExtractedChars;
            using (XmlReader r = Open(Find(zip, "xl/sharedStrings.xml")))
            {
                if (r == null) return strings;
                StringBuilder sb = new StringBuilder();
                bool inItem = false, inPhonetic = false;

                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element)
                    {
                        if (r.LocalName == "si")
                        {
                            inItem = true;
                            sb.Length = 0;
                            if (r.IsEmptyElement) { strings.Add(""); inItem = false; }
                        }
                        // Phonetic runs are pronunciation hints attached to the
                        // same cell. Including them would make a Japanese
                        // workbook match twice and read as duplicated text.
                        else if (r.LocalName == "rPh") inPhonetic = true;
                    }
                    else if (r.NodeType == XmlNodeType.EndElement)
                    {
                        if (r.LocalName == "si" && inItem)
                        {
                            strings.Add(sb.ToString());
                            budget -= sb.Length;
                            inItem = false;
                            // Stop building the table, but keep the entries
                            // already read: the sheet indexes into this list by
                            // position, so returning a short list is better
                            // than returning none.
                            if (budget <= 0) return strings;
                        }
                        else if (r.LocalName == "rPh") inPhonetic = false;
                    }
                    else if (inItem && !inPhonetic &&
                             (r.NodeType == XmlNodeType.Text ||
                              r.NodeType == XmlNodeType.SignificantWhitespace ||
                              r.NodeType == XmlNodeType.Whitespace))
                    {
                        if (sb.Length < MaxElementChars) sb.Append(r.Value);
                    }
                }
            }
            return strings;
        }

        static void ReadSheet(XmlReader r, string sheetName, List<string> shared,
                              List<OfficeLine> lines, ref int budget)
        {
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName != "c") continue;

                string reference = r.GetAttribute("r");
                string type = r.GetAttribute("t");
                if (r.IsEmptyElement) continue;

                string value = CellValue(r, type, shared);
                if (string.IsNullOrEmpty(value)) continue;

                OfficeLine line = new OfficeLine();
                line.Text = value;
                line.Location = sheetName + "!" + (reference != null ? reference : "?");
                lines.Add(line);

                budget -= value.Length;
                if (budget <= 0) return;
            }
        }

        static string CellValue(XmlReader r, string type, List<string> shared)
        {
            StringBuilder inline = new StringBuilder();
            string v = null;
            int depth = r.Depth;

            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "c" && r.Depth == depth)
                    break;

                if (r.NodeType == XmlNodeType.Element)
                {
                    // Only <v> and <is> are read, so a search for "SUM" cannot
                    // answer with every totaled column in the workbook - the
                    // formula text is never captured in the first place, and
                    // the Skip below is a fast-forward over a subtree rather
                    // than the thing that keeps it out. Adding <f> to the line
                    // beneath is the way this breaks, which is what the
                    // planted defect for it does.
                    if (r.LocalName == "f" && !r.IsEmptyElement) { Skip(r); continue; }
                    if (r.LocalName == "v" && !r.IsEmptyElement) { v = ReadTextOf(r); continue; }
                    if (r.LocalName == "is" && !r.IsEmptyElement) { inline.Append(ReadTextOf(r)); continue; }
                }
            }

            if (inline.Length > 0) return inline.ToString();
            if (v == null) return null;

            if (type == "s")
            {
                int index;
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
                    && index >= 0 && index < shared.Count)
                    return shared[index];
                return null;
            }
            // Everything else - numbers, dates as serial numbers, booleans as
            // 0 and 1, cached formula results - is searched as it is stored.
            return v;
        }

        // Reads all text under the current element, leaving the reader on its
        // end tag.
        //
        // Bounded, because this is where a crafted file does its damage: the
        // budgets in the sheet and paragraph readers are spent per emitted
        // line, and a single enormous element is consumed in full before any
        // line is emitted at all. The reader is still driven to the end tag
        // past the bound so the caller stays correctly positioned.
        static string ReadTextOf(XmlReader r)
        {
            StringBuilder sb = new StringBuilder();
            string name = r.LocalName;
            int depth = r.Depth;
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.EndElement && r.LocalName == name && r.Depth == depth)
                    break;
                if (r.NodeType == XmlNodeType.Text ||
                    r.NodeType == XmlNodeType.SignificantWhitespace ||
                    r.NodeType == XmlNodeType.Whitespace)
                {
                    if (sb.Length < MaxElementChars) sb.Append(r.Value);
                }
            }
            return sb.ToString();
        }

        static void Skip(XmlReader r)
        {
            string name = r.LocalName;
            int depth = r.Depth;
            while (r.Read())
                if (r.NodeType == XmlNodeType.EndElement && r.LocalName == name && r.Depth == depth)
                    return;
        }

        // ---- .docx --------------------------------------------------------------

        static List<OfficeLine> Document(ZipArchive zip)
        {
            List<OfficeLine> lines = new List<OfficeLine>();
            int budget = MaxExtractedChars;

            foreach (KeyValuePair<string, string> part in DocumentParts(zip))
            {
                using (XmlReader r = Open(Find(zip, part.Key)))
                {
                    if (r == null) continue;
                    ReadParagraphs(r, part.Value, lines, ref budget);
                }
                if (budget <= 0) break;
            }
            return lines;
        }

        // The body first, then everything else that holds prose. Headers and
        // footnotes are where a document number or a classification marking
        // lives, and those are exactly the strings someone searches a folder
        // of documents for - skipping them would return nothing and look like
        // an answer.
        static List<KeyValuePair<string, string>> DocumentParts(ZipArchive zip)
        {
            List<KeyValuePair<string, string>> parts = new List<KeyValuePair<string, string>>();
            if (Find(zip, "word/document.xml") != null)
                parts.Add(new KeyValuePair<string, string>("word/document.xml", null));

            List<ZipArchiveEntry> rest = new List<ZipArchiveEntry>(zip.Entries);
            rest.Sort(delegate(ZipArchiveEntry a, ZipArchiveEntry b)
            {
                return string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);
            });

            foreach (ZipArchiveEntry e in rest)
            {
                string label = PartLabel(e.FullName);
                if (label != null)
                    parts.Add(new KeyValuePair<string, string>(e.FullName, label));
            }
            return parts;
        }

        static string PartLabel(string fullName)
        {
            string name = fullName.ToLowerInvariant();
            if (!name.StartsWith("word/", StringComparison.Ordinal)) return null;
            if (!name.EndsWith(".xml", StringComparison.Ordinal)) return null;

            string file = name.Substring("word/".Length);
            if (file.IndexOf('/') >= 0) return null;   // not a top-level part

            if (file.StartsWith("header", StringComparison.Ordinal))
                return "Header " + Digits(file);
            if (file.StartsWith("footer", StringComparison.Ordinal))
                return "Footer " + Digits(file);
            if (file == "footnotes.xml") return "Footnote";
            if (file == "endnotes.xml") return "Endnote";
            if (file == "comments.xml") return "Comment";
            return null;
        }

        static string Digits(string s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
                if (char.IsDigit(s[i])) sb.Append(s[i]);
            return sb.Length > 0 ? sb.ToString() : "1";
        }

        static void ReadParagraphs(XmlReader r, string partLabel, List<OfficeLine> lines,
                                   ref int budget)
        {
            StringBuilder sb = new StringBuilder();
            int paragraph = 0;
            bool inParagraph = false;
            bool inDeleted = false;

            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element)
                {
                    switch (r.LocalName)
                    {
                        case "p":
                            if (!r.IsEmptyElement) { inParagraph = true; paragraph++; sb.Length = 0; }
                            continue;
                        // Text inside a tracked deletion is not in the document
                        // any more. Matching it would report a phrase that is
                        // not there when the file is opened.
                        case "del":
                            if (!r.IsEmptyElement) inDeleted = true;
                            continue;
                        case "t":
                            if (inParagraph && !inDeleted && !r.IsEmptyElement) sb.Append(ReadTextOf(r));
                            continue;
                        case "tab":
                            if (inParagraph && !inDeleted) sb.Append('\t');
                            continue;
                        case "br":
                        case "cr":
                            if (inParagraph && !inDeleted) sb.Append(' ');
                            continue;
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (r.LocalName == "del") { inDeleted = false; continue; }
                    if (r.LocalName != "p" || !inParagraph) continue;

                    inParagraph = false;
                    string text = sb.ToString();
                    if (text.Length == 0) continue;

                    OfficeLine line = new OfficeLine();
                    line.Text = text;
                    string n = paragraph.ToString(CultureInfo.InvariantCulture);
                    line.Location = partLabel == null
                        ? "Paragraph " + n
                        : partLabel + ", paragraph " + n;
                    lines.Add(line);

                    budget -= text.Length;
                    if (budget <= 0) return;
                }
            }
        }
    }
}
