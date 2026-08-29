# Changelog

## Unreleased

**The search engine, with the guards that decide whether a search tool works
at all.** A hand-rolled directory walk, file masks, a literal and regex
matcher, a reader fanned out across the cores, and hits streamed through
callbacks under a CancellationToken. Three of the guards are named here because
each is a way a search returns nothing and leaves the user blaming the folder:
the binary sniff consults the byte-order mark before it counts NUL bytes, or
every UTF-16 file is discarded as binary; the UTF-8 decode is strict, because
the lenient one turns an ANSI byte into U+FFFD and reports success, so
searching for that character finds nothing; and the walk is hand-rolled because
`Directory.EnumerateFiles` with `AllDirectories` abandons the entire tree on
the first folder it cannot open.

**A per-match regex timeout, so Cancel is never a lie.** A catastrophically
backtracking pattern inside `Regex.Match` ignores the CancellationToken
entirely. Without a timeout, a reader thread wedged in one would leave the
Cancel button doing nothing visible, which reads as a hung app rather than as a
bad pattern. Two seconds, after which the line is treated as a non-match.

**Encoding, byte-order mark, and line-ending style are recorded during the
read.** Nothing uses them yet. They are what a future Replace would need in
order to write a file back byte-identical apart from the match, and collecting
them afterwards would mean a second pass over the whole folder. A file read
with strip-ANSI on is flagged as unsafe to rewrite, because its match offsets
index transformed text rather than the bytes on disk.

**Results are capped, and say so.** 5,000 hits per file and 200,000 per run.
Whenever a cap bites, the summary line reports it. A silently truncated list
reads as "that is all there is".

**The window.** A collapsible per-file results view over a virtual-mode
ListView, streaming in as files are scanned, with the match highlighted in
place and optional context lines. Themed with the Canvas Suite palette, all 29
themes, dark title bar and scrollbars included.

**Highlights are positioned by character cell, not by measured runs.**
`TextRenderer.MeasureText` rounds every string it is handed, so summing the
widths of the prefix, the match, and the remainder drifts by a pixel or two
across a row. That is invisible on the text and glaring on the highlight, which
is the one thing on the row the eye is drawn to. Cell arithmetic against a
fixed-pitch font is exact.

**The results column is re-fitted whenever the row count changes.** The
vertical scrollbar appears when rows are added, which takes 17 pixels out of
the client area without raising a resize event; a column left at the old width
then grows a horizontal scrollbar that scrolls by exactly the width of the
vertical one.

**Strip ANSI escapes defaults to on in the app and off in the engine.** The
library hands back what is on disk; the application defaults to the folder it
was built for, which is full of raw terminal logs where leaving it off means
every prompt line renders as a row of boxes and a phrase spanning a color code
cannot be found at all.

**An Explorer right-click entry, per user.** Written under `HKCU`, on both
`Directory` and `Directory\Background`, so it needs no elevation and removing
it removes every trace. A tool that asks for admin to add a context menu item
has misjudged what it is.

**Search queries are never persisted, and no history is kept.** A query typed
against a folder of console logs is as likely to be a credential as a command.

**`tools\Plant-Defects.ps1`.** Nine defects planted into a copy of the tree,
each verifying that the check which owns it can actually fail. Re-runnable
after any refactor, rather than a thing someone did once by hand and wrote down.
