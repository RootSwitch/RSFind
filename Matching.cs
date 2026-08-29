// How a query becomes matches on a line, and how a file mask becomes a
// yes/no on a filename.
//
// Split out from the engine because both halves are pure functions over
// strings: they are the part of RSFind that can be tested without a disk or
// a window, and most of the ways a search tool is quietly wrong live here.
//
// C# 5 only (in-box csc).

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RSFind
{
    // Raised for a pattern the user typed wrong. The message is shown in the
    // search bar; a half-typed regex is the normal state of a text box that
    // searches as you type, so it must never reach a crash dialog.
    public class PatternError : Exception
    {
        public PatternError(string message) : base(message) { }
    }

    public class Matcher
    {
        // Catastrophic backtracking is not hypothetical here: the user is
        // invited to type regex, and a reader thread wedged inside
        // Regex.Match ignores the CancellationToken entirely, so Cancel would
        // appear to do nothing. A per-match timeout is the only thing that
        // preempts it.
        static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

        readonly string literal;
        readonly StringComparison comparison;
        readonly bool wholeWord;
        readonly Regex regex;

        public bool IsRegex { get { return regex != null; } }

        public Matcher(string query, bool matchCase, bool wholeWord, bool useRegex)
        {
            if (query == null || query.Length == 0)
                throw new PatternError("nothing to search for");

            this.wholeWord = wholeWord;

            if (useRegex)
            {
                RegexOptions opts = RegexOptions.CultureInvariant;
                if (!matchCase) opts |= RegexOptions.IgnoreCase;
                // Whole word wraps rather than decorates the pattern, so
                // "a|b" becomes \b(?:a|b)\b and not \ba|b\b, which would bind
                // the boundaries to the outer alternatives only.
                string pattern = wholeWord ? @"\b(?:" + query + @")\b" : query;
                try
                {
                    regex = new Regex(pattern, opts, MatchTimeout);
                }
                catch (ArgumentException ex)
                {
                    throw new PatternError(ex.Message);
                }
            }
            else
            {
                literal = query;
                comparison = matchCase
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
            }
        }

        // Finds the next match at or after 'from'. Returns false when the line
        // holds no more.
        public bool Next(string line, int from, out int start, out int length)
        {
            start = -1;
            length = 0;
            if (line == null || from < 0 || from > line.Length) return false;

            if (regex != null)
            {
                Match m;
                try
                {
                    m = regex.Match(line, from);
                }
                catch (RegexMatchTimeoutException)
                {
                    // Treat the line as a non-match rather than failing the
                    // whole search: one pathological line in a 100k-line log
                    // should not cost the other 99,999 results.
                    return false;
                }
                if (!m.Success) return false;
                start = m.Index;
                // A pattern that can match empty ("x*") would otherwise report
                // an endless run of zero-width hits at one position, because
                // the caller advances by the match length.
                length = m.Length > 0 ? m.Length : 1;
                return true;
            }

            int i = from;
            while (i <= line.Length - literal.Length)
            {
                int at = line.IndexOf(literal, i, comparison);
                if (at < 0) return false;
                if (!wholeWord || IsWholeWordAt(line, at, literal.Length))
                {
                    start = at;
                    length = literal.Length;
                    return true;
                }
                i = at + 1;
            }
            return false;
        }

        // Expands $1 and friends against the match that starts at 'start'.
        // In literal mode the replacement is already literal and comes back
        // untouched - notably WITHOUT treating $1 as a group reference, which
        // would be a nasty surprise for someone replacing a price.
        public string Expand(string line, int start, string replacement)
        {
            if (regex == null) return replacement;
            Match m;
            try
            {
                m = regex.Match(line, start);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new PatternError("the pattern timed out while building the replacement");
            }
            if (!m.Success || m.Index != start)
                throw new PatternError("the pattern no longer matches where the search found it");
            try
            {
                return m.Result(replacement);
            }
            catch (ArgumentException ex)
            {
                throw new PatternError(ex.Message);
            }
            catch (NotSupportedException ex)
            {
                throw new PatternError(ex.Message);
            }
        }

        // Checks a replacement string before anything is planned, so a typo in
        // "$1" reports itself once rather than as a refusal on every file.
        public void ValidateReplacement(string replacement)
        {
            if (regex == null || replacement == null) return;
            try
            {
                Match m = regex.Match("");
                // Result requires a successful match, so an unmatchable
                // pattern cannot be validated here. That is fine: Expand
                // reports it later, per file, with the same exception type.
                if (m.Success) m.Result(replacement);
            }
            catch (ArgumentException ex) { throw new PatternError(ex.Message); }
            catch (NotSupportedException ex) { throw new PatternError(ex.Message); }
            catch (RegexMatchTimeoutException) { }
        }

        // True when the run is not glued to a word character on either side.
        // Closer to what a person means by "whole word" than \b is for a
        // literal that itself starts or ends with punctuation.
        static bool IsWholeWordAt(string line, int start, int length)
        {
            if (start > 0 && IsWordChar(line[start - 1])) return false;
            int end = start + length;
            if (end < line.Length && IsWordChar(line[end])) return false;
            return true;
        }

        static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }
    }

    public static class Masks
    {
        // "*.log;*.txt, *.md" - semicolons, commas, and whitespace all
        // separate, because every tool in this space picked a different one
        // and nobody remembers which.
        public static List<string> Parse(string masks)
        {
            List<string> list = new List<string>();
            if (masks == null) return list;
            string[] parts = masks.Split(new char[] { ';', ',', ' ', '\t' },
                                         StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
            {
                string t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        // An empty include list means everything; an empty exclude list
        // excludes nothing. Both are the blank-box default.
        public static bool Allows(string fileName, List<string> include, List<string> exclude)
        {
            if (exclude != null && exclude.Count > 0 && MatchesAny(fileName, exclude)) return false;
            if (include == null || include.Count == 0) return true;
            return MatchesAny(fileName, include);
        }

        static bool MatchesAny(string fileName, List<string> patterns)
        {
            for (int i = 0; i < patterns.Count; i++)
                if (Matches(fileName, patterns[i])) return true;
            return false;
        }

        public static bool Matches(string fileName, string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || fileName == null) return false;

            // ".log" is what people type when they mean "*.log". Reading it as
            // a filename of exactly ".log" is technically right and useless.
            if (pattern[0] == '.' && pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
                pattern = "*" + pattern;

            return Wildcard(fileName, 0, pattern, 0);
        }

        // Iterative rather than recursive: a pattern of many stars against a
        // long name overflows the stack in the naive recursive version, and a
        // filename comes from outside the program often enough to care.
        static bool Wildcard(string name, int n, string pattern, int p)
        {
            int starP = -1, starN = 0;
            while (n < name.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], name[n])))
                {
                    p++; n++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    starP = p++; starN = n;
                }
                else if (starP >= 0)
                {
                    p = starP + 1;
                    n = ++starN;
                }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        static bool Same(char a, char b)
        {
            return char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
        }
    }
}
