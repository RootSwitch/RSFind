// The search itself: walk a directory, decide which files are in scope, read
// them, and stream hits out as they are found.
//
// No index, no service, no watcher. RSFind runs, scans, answers, and exits.
// Being manual is the point - it is the tool you reach for precisely because
// the indexer did not have the answer.
//
// The engine has no reference to a window and no dependency on WinForms, so
// the whole of it is exercised by the console harness in tools\.
//
// C# 5 only (in-box csc).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RSFind
{
    public class SearchOptions
    {
        public string Root;
        public string Query;

        public bool MatchCase;
        public bool WholeWord;
        public bool UseRegex;

        public bool IncludeSubfolders = true;
        public string IncludeMasks;     // "*.log;*.txt" - blank means everything
        public string ExcludeMasks;
        public bool ExcludeBinary = true;
        public int MaxFileMegabytes = 50;

        public int ContextBefore;
        public int ContextAfter;

        // Raw-mode session logs carry terminal escapes. Matching against the
        // text a person actually saw needs them gone first.
        public bool StripAnsi;

        // Caps, so that searching for "e" across a log folder degrades into a
        // truncated answer rather than an out-of-memory dialog. Whenever one
        // bites, the result says so - a silently short list reads as "that is
        // all there is", which is the one thing a search tool must never imply.
        public int MaxHitsPerFile = 5000;
        public int MaxTotalHits = 200000;
    }

    public class Hit
    {
        public int LineNumber;      // 1-based, as every editor counts them
        public string Line;
        public int MatchStart;      // char offset into Line
        public int MatchLength;
        public string[] Before;
        public string[] After;
        // Set for formats where a line number means nothing, such as a
        // spreadsheet cell. Null for plain text.
        public string Location;
    }

    public class FileHits
    {
        public string Path;
        public string RelativePath;
        public List<Hit> Hits = new List<Hit>();
        public bool Truncated;      // MaxHitsPerFile bit here

        // What a Replace would need to write this file back byte-identical
        // apart from the matches. Gathered during the read because a second
        // pass over the folder to collect it would cost as much as the search.
        public string EncodingName;
        public bool HasBom;
        public NewlineStyle Newlines;
        public bool Transformed;    // strip-ANSI was on: offsets are not on-disk offsets
        public long Length;
        public DateTime LastWriteUtc;

        // The one question Replace has to ask before writing.
        public bool IsSafeToRewrite
        {
            get { return !Transformed; }
        }
    }

    public class SearchProgress
    {
        public int FilesScanned;
        public int FilesSkipped;
        public int FilesMatched;
        public int Hits;
        public bool Truncated;
        public bool Cancelled;
        public bool Finished;
        public TimeSpan Elapsed;
    }

    public class SearchEngine
    {
        readonly SearchOptions opts;
        readonly Matcher matcher;
        readonly List<string> include;
        readonly List<string> exclude;
        readonly long maxFileBytes;

        int filesScanned;
        int filesSkipped;
        int filesMatched;
        int hits;
        int truncated;              // int rather than bool: written from many threads

        readonly object callbackLock = new object();

        // Throws PatternError for a query the user typed wrong, which is the
        // caller's cue to show a message rather than start a search.
        public SearchEngine(SearchOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            opts = options;
            matcher = new Matcher(opts.Query, opts.MatchCase, opts.WholeWord, opts.UseRegex);
            include = Masks.Parse(opts.IncludeMasks);
            exclude = Masks.Parse(opts.ExcludeMasks);
            maxFileBytes = opts.MaxFileMegabytes > 0
                ? (long)opts.MaxFileMegabytes * 1024L * 1024L
                : long.MaxValue;
        }

        // Runs to completion on the calling thread, fanning out reads across
        // the cores. onFile and onError are serialized before they are called,
        // so a consumer never needs its own lock.
        public SearchProgress Run(CancellationToken ct,
                                  Action<FileHits> onFile,
                                  Action<SearchProgress> onProgress,
                                  Action<string, string> onError)
        {
            Stopwatch clock = Stopwatch.StartNew();
            bool cancelled = false;
            long lastReport = 0;

            ParallelOptions po = new ParallelOptions();
            po.CancellationToken = ct;
            po.MaxDegreeOfParallelism = Environment.ProcessorCount;

            try
            {
                // NoBuffering: the default partitioner hands each worker a
                // growing chunk of the enumerable, which on a folder of a
                // hundred files means one thread takes most of them and the
                // results arrive in a lump at the end instead of streaming.
                var work = Partitioner.Create(EnumerateFiles(ct, onError),
                                              EnumerablePartitionerOptions.NoBuffering);

                Parallel.ForEach(work, po, path =>
                {
                    FileHits fh = ScanFile(path, ct, onError);
                    if (fh != null && fh.Hits.Count > 0)
                    {
                        Interlocked.Increment(ref filesMatched);
                        Interlocked.Add(ref hits, fh.Hits.Count);
                        if (onFile != null)
                            lock (callbackLock) { onFile(fh); }
                    }

                    if (onProgress != null)
                    {
                        // Roughly four updates a second. One callback per file
                        // would marshal thousands of times onto the UI thread
                        // and make the scan slower than the disk.
                        long now = clock.ElapsedMilliseconds;
                        if (now - Interlocked.Read(ref lastReport) >= 250)
                        {
                            Interlocked.Exchange(ref lastReport, now);
                            lock (callbackLock) { onProgress(Snapshot(clock, false, false)); }
                        }
                    }
                });
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            SearchProgress final = Snapshot(clock, cancelled, true);
            if (onProgress != null) onProgress(final);
            return final;
        }

        SearchProgress Snapshot(Stopwatch clock, bool cancelled, bool finished)
        {
            SearchProgress p = new SearchProgress();
            p.FilesScanned = Thread.VolatileRead(ref filesScanned);
            p.FilesSkipped = Thread.VolatileRead(ref filesSkipped);
            p.FilesMatched = Thread.VolatileRead(ref filesMatched);
            p.Hits = Thread.VolatileRead(ref hits);
            p.Truncated = Thread.VolatileRead(ref truncated) != 0;
            p.Cancelled = cancelled;
            p.Finished = finished;
            p.Elapsed = clock.Elapsed;
            return p;
        }

        // A hand-rolled walk rather than Directory.EnumerateFiles with
        // AllDirectories. The framework version throws the moment it meets one
        // folder it cannot open and abandons the rest of the tree, so a single
        // protected directory loses every result underneath its siblings.
        IEnumerable<string> EnumerateFiles(CancellationToken ct, Action<string, string> onError)
        {
            Stack<string> pending = new Stack<string>();
            pending.Push(opts.Root);

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string dir = pending.Pop();

                string[] files = null;
                try
                {
                    files = Directory.GetFiles(dir);
                }
                catch (Exception ex)
                {
                    Report(onError, dir, ex.Message);
                }

                if (opts.IncludeSubfolders)
                {
                    string[] subs = null;
                    try
                    {
                        subs = Directory.GetDirectories(dir);
                    }
                    catch (Exception ex)
                    {
                        Report(onError, dir, ex.Message);
                    }

                    if (subs != null)
                    {
                        foreach (string sub in subs)
                        {
                            // A junction or symlink pointing at an ancestor
                            // turns the walk into an infinite loop, and the
                            // user sees a scan that never ends rather than an
                            // error. Following them is not worth that.
                            try
                            {
                                FileAttributes a = File.GetAttributes(sub);
                                if ((a & FileAttributes.ReparsePoint) != 0) continue;
                            }
                            catch (Exception ex)
                            {
                                Report(onError, sub, ex.Message);
                                continue;
                            }
                            pending.Push(sub);
                        }
                    }
                }

                if (files == null) continue;
                foreach (string f in files)
                {
                    if (!Masks.Allows(Path.GetFileName(f), include, exclude)) continue;
                    yield return f;
                }
            }
        }

        FileHits ScanFile(string path, CancellationToken ct, Action<string, string> onError)
        {
            if (Thread.VolatileRead(ref hits) >= opts.MaxTotalHits)
            {
                Interlocked.Exchange(ref truncated, 1);
                return null;
            }

            long length;
            DateTime writtenUtc;
            try
            {
                FileInfo fi = new FileInfo(path);
                length = fi.Length;
                writtenUtc = fi.LastWriteTimeUtc;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref filesSkipped);
                Report(onError, path, ex.Message);
                return null;
            }

            if (length > maxFileBytes)
            {
                Interlocked.Increment(ref filesSkipped);
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = TextFiles.ReadAllBytesShared(path);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref filesSkipped);
                Report(onError, path, ex.Message);
                return null;
            }

            TextContent content = TextFiles.ToLines(bytes, opts.ExcludeBinary, opts.StripAnsi);
            if (content == null)
            {
                Interlocked.Increment(ref filesSkipped);
                return null;
            }

            Interlocked.Increment(ref filesScanned);

            FileHits fh = new FileHits();
            fh.Path = path;
            fh.RelativePath = MakeRelative(opts.Root, path);
            fh.EncodingName = content.Encoding != null ? content.Encoding.WebName : "unknown";
            fh.HasBom = content.HasBom;
            fh.Newlines = content.Newlines;
            fh.Transformed = content.Transformed;
            fh.Length = length;
            fh.LastWriteUtc = writtenUtc;

            string[] lines = content.Lines;
            for (int i = 0; i < lines.Length; i++)
            {
                if ((i & 0x3FF) == 0) ct.ThrowIfCancellationRequested();

                string line = lines[i];
                int from = 0, start, len;
                while (matcher.Next(line, from, out start, out len))
                {
                    Hit h = new Hit();
                    h.LineNumber = i + 1;
                    h.Line = line;
                    h.MatchStart = start;
                    h.MatchLength = len;
                    if (opts.ContextBefore > 0) h.Before = Slice(lines, i - opts.ContextBefore, i);
                    if (opts.ContextAfter > 0) h.After = Slice(lines, i + 1, i + 1 + opts.ContextAfter);
                    fh.Hits.Add(h);

                    if (fh.Hits.Count >= opts.MaxHitsPerFile)
                    {
                        fh.Truncated = true;
                        Interlocked.Exchange(ref truncated, 1);
                        return fh;
                    }
                    from = start + len;
                }
            }
            return fh;
        }

        static string[] Slice(string[] lines, int from, int toExclusive)
        {
            if (from < 0) from = 0;
            if (toExclusive > lines.Length) toExclusive = lines.Length;
            int n = toExclusive - from;
            if (n <= 0) return new string[0];
            string[] slice = new string[n];
            Array.Copy(lines, from, slice, 0, n);
            return slice;
        }

        static string MakeRelative(string root, string path)
        {
            if (string.IsNullOrEmpty(root)) return path;
            string r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
            if (path.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                return path.Substring(r.Length);
            return path;
        }

        void Report(Action<string, string> onError, string path, string message)
        {
            if (onError == null) return;
            lock (callbackLock) { onError(path, message); }
        }
    }
}
