# Changelog

## Unreleased

**`.xlsx` and `.docx` are searched, with no dependency.** Both formats are a
ZIP of XML and .NET Framework ships both readers in the box, so the one thing
every grep-shaped tool declines to do costs two in-box references rather than
an Office install or a package that has to be trusted. Spreadsheet hits report
as `Schedule!A2` and document hits as `Header 1, paragraph 1`, because a line
number in a workbook is not a place anyone can go.

**Office files are handled before the binary sniff, not after.** A workbook is
a ZIP, a ZIP is full of NUL bytes, and the sniff is right about that. With the
branches in the other order, the exclude-binary default silently discards the
one format the feature exists to read. There is a planted defect for this
exact inversion.

**Word runs are joined before matching.** Word splits a sentence across runs at
every formatting change, so a per-run reader cannot match a phrase containing
one bolded word - it finds nothing and looks like an answer.

**Formulas are not searched, and tracked deletions are not extracted.**
Searching formulas answers `SUM` with every totaled column in the workbook;
matching deleted text reports a phrase that is not in the document when it is
opened. Phonetic runs are skipped for the same reason - they would double every
value in a Japanese workbook.

**The XML readers refuse a DTD.** These files come from other people, and a
parser that expands entities turns a folder search into a file reader pointed
wherever the document says. `DtdProcessing.Prohibit` and a null resolver, with
uncompressed-size bounds on every entry so a small archive cannot claim to hold
a gigabyte.

**Formats RSFind cannot read are counted and named, not skipped silently.**
`.pdf`, `.xls`, `.doc`, and the rest now appear on the summary line as "1 file
is in a format RSFind cannot read (.pdf)". A folder of PDFs answering "no hits"
is a missing capability wearing the clothes of an answer.

**The planted-defect harness handles multi-line plants.** Adding one for the
formula guard surfaced something worth recording: two independent mechanisms
keep formula text out, so neither single-line plant was caught. That is the
property being guarded twice rather than a gap in the test, and the comment in
the source now says which line is load-bearing and which is a fast-forward.

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
