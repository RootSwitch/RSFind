// Turning bytes on disk into lines to search.
//
// Everything here is deliberately a pure function over a byte array so it can
// be tested without touching a disk. The three judgment calls it makes - is
// this binary, what encoding is it, where do the lines break - are the three
// places a search tool silently returns nothing and leaves the user blaming
// the folder.
//
// It also records what a future Replace would need in order to write the file
// back unchanged apart from the match: the encoding, whether there was a BOM,
// and which line ending the file uses. Capturing that during the read costs
// nothing; reconstructing it afterwards means reading every file twice.
//
// C# 5 only (in-box csc).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RSFind
{
    public enum NewlineStyle
    {
        None,   // a single line, no terminator seen
        Crlf,
        Lf,
        Mixed   // both present: a Replace must rewrite lines individually
    }

    public class TextContent
    {
        public string[] Lines;
        public Encoding Encoding;
        public bool HasBom;
        public NewlineStyle Newlines;
        // True when Lines are not a faithful decode of the bytes - currently
        // only the strip-ANSI path. A Replace must refuse these: the match
        // offsets index text that is not what is on disk.
        public bool Transformed;
    }

    public static class TextFiles
    {
        // The window a file gets judged on. Large enough that a text file with
        // a long banner is not misread, small enough to stay cheap on a folder
        // of thousands.
        public const int SniffBytes = 8192;

        // Looks for a byte-order mark. Returns null when there is none, which
        // is the common case for terminal logs.
        public static Encoding DetectBom(byte[] bytes, out int bomLength)
        {
            bomLength = 0;
            if (bytes == null) return null;

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                bomLength = 3;
                return new UTF8Encoding(false);
            }
            // The UTF-32 marks must be tested before the UTF-16 ones: FF FE is
            // a prefix of FF FE 00 00, so checking UTF-16 first decodes a
            // UTF-32LE file as UTF-16 and produces convincing garbage.
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            {
                bomLength = 4;
                return new UTF32Encoding(false, false);
            }
            if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            {
                bomLength = 4;
                return new UTF32Encoding(true, false);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                bomLength = 2;
                return new UnicodeEncoding(false, false);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                bomLength = 2;
                return new UnicodeEncoding(true, false);
            }
            return null;
        }

        // A NUL byte in the first few KB means binary. The BOM has to be
        // consulted first, because UTF-16 text is roughly half NUL bytes and
        // the naive sniff throws away every UTF-16 file in the folder.
        //
        // UTF-16 without a BOM still reads as binary. That is a known and
        // accepted limit rather than an oversight: the alternative is guessing
        // from byte statistics, and a wrong guess corrupts results silently.
        public static bool LooksBinary(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;

            int bomLength;
            if (DetectBom(bytes, out bomLength) != null) return false;

            int limit = Math.Min(bytes.Length, SniffBytes);
            for (int i = 0; i < limit; i++)
                if (bytes[i] == 0) return true;
            return false;
        }

        // BOM, else strict UTF-8, else the system ANSI codepage.
        //
        // Strict matters. UTF8Encoding's default fallback replaces bad bytes
        // with U+FFFD and reports success, so a Windows-1252 log decodes
        // "cleanly" with every accented character destroyed, and searching for
        // one finds nothing. Throwing is what lets the ANSI fallback happen.
        public static string Decode(byte[] bytes, out Encoding encoding, out bool hasBom)
        {
            hasBom = false;
            encoding = null;
            if (bytes == null) return string.Empty;

            int bomLength;
            Encoding bom = DetectBom(bytes, out bomLength);
            if (bom != null)
            {
                hasBom = true;
                encoding = bom;
                return bom.GetString(bytes, bomLength, bytes.Length - bomLength);
            }

            try
            {
                UTF8Encoding strict = new UTF8Encoding(false, true);
                string text = strict.GetString(bytes);
                encoding = strict;
                return text;
            }
            catch (DecoderFallbackException)
            {
                encoding = Encoding.Default;   // ANSI codepage on .NET Framework
                return Encoding.Default.GetString(bytes);
            }
        }

        // ESC and BEL are built from their code points instead of written as
        // escape sequences in a string literal. A literal ESC byte in a source
        // file is invisible in every editor and every diff, and it does not
        // survive being copied through a terminal.
        const char Esc = (char)27;
        const char Bel = (char)7;
        static readonly string E = Esc.ToString();
        static readonly string B = Bel.ToString();

        // CSI: ESC [ parameters intermediates final. Covers colors, cursor
        // moves, and the private-mode sequences (ESC [ ? 25 l and friends)
        // that a full-screen tool writes into a session log.
        static readonly Regex AnsiCsi = new Regex(
            E + "\\[[0-9;:?]*[ -/]*[@-~]", RegexOptions.Compiled);

        // OSC: ESC ] ... terminated by BEL or by ST (ESC backslash). This is
        // where the window title lives, and its payload is arbitrary text.
        static readonly Regex AnsiOsc = new Regex(
            E + "\\][^" + B + E + "]*(?:" + B + "|" + E + "\\\\)", RegexOptions.Compiled);

        // The rest: ESC c, ESC =, ESC ( B, and the other short forms. This one
        // runs last because its final-byte class also covers "[" and "]", so
        // ahead of the two above it would eat the head of every CSI and OSC.
        static readonly Regex AnsiTwoChar = new Regex(
            E + "[ -/]*[0-~]", RegexOptions.Compiled);

        // Everything else in C0, plus DEL. Tab, LF, and CR are kept: tab is
        // real content in a log, and the line splitter needs the other two.
        static readonly Regex OtherControls = new Regex(
            "[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\x7F]", RegexOptions.Compiled);

        // Strips terminal escapes so a raw-mode session log matches on the text
        // a person actually saw. Order matters: an OSC payload can contain what
        // looks like a CSI sequence, so OSC goes first, and the catch-all
        // two-character rule goes last or it would eat the "[" of every CSI.
        public static string StripAnsi(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf(Esc) < 0 && !OtherControls.IsMatch(text)) return text;

            string s = AnsiOsc.Replace(text, string.Empty);
            s = AnsiCsi.Replace(s, string.Empty);
            s = AnsiTwoChar.Replace(s, string.Empty);
            return OtherControls.Replace(s, string.Empty);
        }

        // Splits on LF and trims one trailing CR, so a line's text is exactly
        // what a person sees and reconstruction stays exact.
        //
        // A lone CR is left inside the line rather than treated as a break.
        // Terminal progress bars redraw with CR, so splitting on it would read
        // better - but it is a transform, and a transform makes the offsets
        // unusable for Replace. Strip-ANSI mode, which is already a transform,
        // does split on it. See ToLines.
        public static string[] SplitLines(string text, out NewlineStyle style)
        {
            style = NewlineStyle.None;
            if (text == null) return new string[0];

            bool sawCrlf = false, sawLf = false;
            List<string> lines = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                int end = i;
                if (end > start && text[end - 1] == '\r') { end--; sawCrlf = true; }
                else sawLf = true;
                lines.Add(text.Substring(start, end - start));
                start = i + 1;
            }
            // A file ending in a newline has no trailing empty line to report:
            // adding one puts a phantom last row under every file in the
            // results list.
            if (start < text.Length)
                lines.Add(text.Substring(start));

            if (sawCrlf && sawLf) style = NewlineStyle.Mixed;
            else if (sawCrlf) style = NewlineStyle.Crlf;
            else if (sawLf) style = NewlineStyle.Lf;
            return lines.ToArray();
        }

        // The whole decode and split, in the form the engine wants.
        // Returns null when the bytes are binary and binaries are excluded.
        public static TextContent ToLines(byte[] bytes, bool excludeBinary, bool stripAnsi)
        {
            if (excludeBinary && LooksBinary(bytes)) return null;

            TextContent c = new TextContent();
            Encoding enc;
            bool bom;
            string text = Decode(bytes, out enc, out bom);
            c.Encoding = enc;
            c.HasBom = bom;

            if (stripAnsi)
            {
                // Transformed is set only when the strip actually removed
                // something, not merely because the option was on.
                //
                // This matters more than it looks. Strip-ANSI is on by
                // default, and a flag set unconditionally would mark every
                // file unsafe to rewrite - so a Replace across a folder of
                // Markdown, which contains no escapes whatsoever, would refuse
                // every file and the option would be a trap rather than a
                // default. A file the strip did not touch is byte-for-byte the
                // file on disk, and its offsets are as good as if the option
                // had been off.
                string stripped = StripAnsi(text);
                if (!string.Equals(stripped, text, StringComparison.Ordinal))
                {
                    // Only now is it worth splitting CR-only redraw frames
                    // onto their own lines: the content is already altered, so
                    // there is nothing left to preserve.
                    text = stripped.Replace("\r\n", "\n").Replace('\r', '\n');
                    c.Transformed = true;
                }
            }

            NewlineStyle style;
            c.Lines = SplitLines(text, out style);
            c.Newlines = style;
            return c;
        }

        // Reads a file with sharing flags wide enough that a log being written
        // right now is still searchable. A tool that cannot read the file the
        // terminal is currently appending to would miss today's session, which
        // is the one most likely to be wanted.
        public static byte[] ReadAllBytesShared(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete))
            {
                long length = fs.Length;
                byte[] buffer = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = fs.Read(buffer, read, (int)(length - read));
                    if (n <= 0) break;
                    read += n;
                }
                if (read == length) return buffer;
                byte[] trimmed = new byte[read];
                Array.Copy(buffer, trimmed, read);
                return trimmed;
            }
        }
    }
}
