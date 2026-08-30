# Changelog

## Unreleased

**`tools\Make-Dist.ps1` builds the release zip**, rebuilding the exe first
rather than packaging whatever binary is lying in the folder. A distribution
carrying an exe that does not match the sources beside it is worse than one
carrying no exe at all. It writes a `.sha256` next to the zip, because DEPLOY.md
tells the recipient to `Unblock-File` the download - which strips the one record
Windows kept of where it came from - and something has to be verifiable before
that, or "unblock it" is the whole story.

It does not carry a third copy of the source list. `Build-RSFind.cmd` and
`Run-From-Source.ps1` each hold one already, and DEPLOY.md asks whoever adds a
file to keep them in step - which is a rule enforced by remembering. The
packaging script reads the `.cs` files off the disk and compares that set
against both lists, refusing to build a zip when they disagree and naming the
file and the list it is missing from. A stray source file in the folder fails
it too: an unexplained `.cs` in a distribution is exactly what a reader of it
would be right to ask about. Both cases were tested by planting them.

**DEPLOY.md covers mark of the web, and admits the exe cannot be verified by
rebuilding it.** A downloaded zip carries a zone identifier that survives
extraction onto every file inside it, so the symptom is SmartScreen or an exe
that silently does nothing - hash first, then `Unblock-File` the zip before
extracting rather than the files after.

The honest half is the second one: the in-box compiler is not deterministic.
Two builds of a byte-identical tree produce different binaries, because the PE
header carries a fresh module ID each time - measured, not assumed. So the
obvious integrity check a careful person would reach for, rebuild and compare
against the shipped exe, cannot work here, and a mismatch is not evidence of
anything. Saying so is better than leaving someone to discover it and conclude
the download was tampered with.

**The README says what it touches.** One table for the whole footprint:
`settings.ini`, the undo folder, the `.rsfind-tmp` and `.rsfind-old` files that
appear beside a file being replaced, an exported result list, and the two
registry keys - each with when it is written, and which one of them grows. That
was spread across the Options section, the Undo section, and DEPLOY.md before,
so the honest answer to "what does this put on my disk" took three readings and
knowing that the third file existed.

**`Run-From-Source.cmd`, matching RSPaster.** Compiles the sources in memory
with `Add-Type` and runs the same app with no executable involved. It exists
because an executable is a thing people have to decide to trust: a freshly
built, unsigned binary has no reputation, and a machine configured to be careful
about that is being sensible. Handing someone readable `.cs` files costs nothing
extra, because the compiler is already on their machine.

Measured: 1.6 s to a window and 96 MB, against 0.4 s and 37 MB for the exe,
because it compiles on every launch. The exe stays the day-to-day path.

**Startup is now `MainForm.Run(folder)`**, with `Main` as a thin argument
parser over it, so there is one startup sequence rather than a second one living
in a script.

**The Explorer entry is refused when running from source.** It records
`Application.ExecutablePath` as the program to launch, which under a PowerShell
host is `powershell.exe` - a right-click item that opens a console window, under
a registry key named RSFind. It refuses and says which way forward, rather than
writing an entry that lies about what it starts.

**Documented what the preview shows when several matches land on one line.**
Each row renders that one change against the original line, so no single row
shows the line as it will finally read - the write applies all the selected ones
together. A review raised it as a low-severity finding and suggested either
rendering the cumulative line or a README note.

The note, deliberately. Every row being accurate about its own change is what
makes the per-row checkboxes mean anything; showing the cumulative line would
make each row depend on which other boxes are ticked, which trades one confusion
for a worse one.

**Undo backups are bounded at the last 10 runs, and the folder is no longer a
secret.** Nothing pruned them before: there was no retention limit, no cleanup,
and no UI to clear them, and the only mention of the location was in DEPLOY.md.

The disk growth was the smaller half. RSFind makes a deliberate point of never
writing a search query down, on the grounds that one is as likely to be a serial
number or a password as a word - while keeping complete copies of the files
those queries ran against, indefinitely, in a place the user was never told
about. That was the louder inconsistency, and a review named it.

Pruning happens after each replace and again at startup, so a folder that grew
before there was a rule does not wait for the next replace to be trimmed. It
removes only directories holding a `manifest.txt` - the ones this code wrote -
and leaves anything else in that folder alone, which is the same blast-radius
rule the test suite follows about its own scratch directory. The location and
the retention rule now appear in the About box and the README.

This reverses an argument DEPLOY.md previously made, that a tool quietly
deleting the evidence of its own writes would be making the wrong tradeoff.
Bounded retention plus saying where it is beats both that and keeping
everything forever.

**Double-clicking a result no longer shell-executes it.** With no editor command
set, opening a hit called `Process.Start` on the path, which uses the file's
association - normally a text editor, because normally the file is a log. But
blank the file mask and uncheck **Exclude binary files**, which the README
describes as the way to find a string inside a firmware image, and a `.exe`,
`.bat`, `.ps1`, `.js`, `.lnk` or `.reg` containing the search string as ASCII
appears in the results like anything else. Double-clicking it ran it.

The replace path already refused binaries "regardless of what the search options
said". The open path had not been given the same reasoning; now it has.

The rule consults two lists: a built-in one for what is dangerous on any Windows
machine, and the machine's own `PATHEXT` for what this box has decided is
executable - the only way to catch one where `.py` has been added to it. It is a
refusal rather than a confirmation, because a prompt on a double-click is a
thing people dismiss, and the message names both ways forward: an editor command
opens the file safely, since handing a path to a text editor does not execute
it, and Open Containing Folder was already there for anything else.

Verified end to end with a `.cmd` that would have written a file had it run.

**A zero-width regex replace overwrote a character instead of inserting one.**
Replacing `^` with `> ` across a file produced `> ello` from `hello` - every
line prefixed and its first character eaten.

One line was doing two jobs badly. `Matcher.Next` reported a zero-width match as
length 1, which kept a search loop adding that length from spinning forever on a
pattern like `x*` - and `Replacer` took the same number as the span to
overwrite. The search was safe and the replace was wrong, from the same
constant.

They are separated now: `Next` reports the true length, zero included, and
`Matcher.Advance` is how a loop moves past a match. A length is a fact about the
match; advancing is the loop's problem. Both halves have their own planted
defect, because either one alone reintroduces half the bug.

"Prefix every line" is a common enough use of a regex replace that this was
going to be found. The preview did show `> ello`, so the backstop worked - but
the tool was still doing something other than what was asked.

**A malformed Office file could hang the window permanently.** A zip entry name
is arbitrary bytes; a Windows path name is not. An entry called `sheet"1".xml`
reached `Path.GetFileNameWithoutExtension` and raised `ArgumentException`, which
was not in `Extract`'s catch list. It then escaped `ScanFile` - whose two most
exception-prone calls were both outside its `try` - faulted the scanning task,
and left the window on "Searching..." with Find disabled and **Cancel unable to
help**, because the task it cancels was already dead. Only killing the process
recovered it. A 173-byte file was enough.

Three independent guards, because each is worth having alone: `Extract` now
treats any failure as "not a readable Office file", `ScanFile` wraps everything
so one bad file is a skipped file, and the search task always leaves the running
state. `Run` and the UI both report a fault on the summary line rather than
falling silent.

**A file past the per-file hit cap was silently half-replaced.** The cap and the
preview limit are both 5,000, so a capped file landed exactly on the gate and
slipped under a strict greater-than every time: 7,000 occurrences in, 5,000
replaced, 2,000 left, and a success message mentioning none of it. This was the
one place the project's own rule about caps was broken - everywhere else a cap
that bites says so, and here it bit during a *write*. Truncated files are now
refused in `Plan`, using the refusal row the preview already had.

**A failed write could destroy the file and both copies of it.** When
`File.Replace` failed, the fallback deleted the original and then moved the temp
into place. A scanner grabbing the path between those two steps - the ordinary
way this happens - failed the move, whereupon the temp was deleted as tidy-up
and the caller deleted the backup. All three gone, from a design whose whole
promise is that an interrupted write leaves the original intact. The fallback
now renames the original aside instead of deleting it, restores it if the move
fails, and the backup is kept on failure rather than cleaned up.

**A 63 KB workbook could cost 640 MB.** `ZipArchiveEntry.Length` is the
uncompressed size declared in the zip's central directory - a number the file
supplies about itself - so the guard tested the claim rather than the delivery.
Reads are now bounded by a limiting stream, and the parser carries
`MaxCharactersInDocument`.

That last part is where the measurement mattered: capping what the extractor
appends does nothing, because `XmlReader` materializes a whole text node into a
string before handing it over, so by the time `r.Value` can be inspected the
allocation has happened. `MaxCharactersInDocument` is checked *during* parsing.
Measured on the same fixture: 640 MB before, 48 MB after, and reported as a
malformed file instead of silently returning nothing.

**Every replace was silently stripping the byte-order mark.** Found by an
external review, reproduced, and fixed.

`DetectBom` built its encodings with the emit-a-mark flag off -
`new UTF8Encoding(false)`, `new UnicodeEncoding(false, false)`, and so on. That
flag is the only thing `GetPreamble()` reports, and the write path rebuilds a
file as `GetPreamble()` + `GetBytes(text)`. So the branch that preserves the
mark faithfully prepended nothing, on every BOM'd file, every time.

For UTF-8 that is untidy. For UTF-16 and UTF-32 it destroys the file: the bytes
survive but nothing can identify them any more, RSFind included - with no mark
the binary sniff sees NUL bytes, correctly calls the file binary, and drops it
from its own search. A second replace would then be refused as "this file looks
binary".

The verify-twice machinery, the temp-file swap, and the encoding round-trip
guard all worked perfectly and then handed off to a write that corrupted the
file anyway.

It was invisible to the test suite because every fixture in the replace tests
used `UTF8Encoding(false)` - no mark, so nothing to lose. There is now a
round-trip case per mark shape, each asserting the preamble survives, the
replacement applied, and - the assertion that matters - that the file is still
findable afterwards. Plus a planted defect on the UTF-16 case, the shape where
the consequence is destructive rather than cosmetic.

**The screenshot panels are the client area only, framed by the HTML.** The
first version carried a Windows 7 looking title bar in its inactive colors,
which is not what the app looks like on any machine it runs on. A window's
frame is composited by the Desktop Window Manager, not painted by the window,
so a capture taken inside the process cannot see it: `DrawToBitmap` sends
`WM_PRINT`, which asks the window for a frame it does not own and gets the
legacy `DefWindowProc` caption back. Confirmed by flipping the dark-title-bar
attribute between two captures and getting byte-identical title bars.

Cropping is the better answer anyway. A real frame in a product shot varies
with the Windows version, accent color, and light or dark setting of whatever
machine took the picture, none of which are facts about RSFind.

**Hero and social preview images, composed as HTML.** `docs/src/hero.html` and
`docs/src/social.html` are rendered to PNG by `tools/Render-Png.ps1` using the
headless Chrome or Edge already on the machine. The layout is text, so a
caption typo is a one-line fix and a re-render rather than an image edit -
which is the point, because an image cannot be grepped or corrected once links
to it are cached. The renderer fails rather than writing a cropped file if the
page overflows its frame.

The four hero panels are real captures rather than mock-ups, and each shows a
different thing the tool does on a different palette instead of the same window
in four colors.

**charcheck now scans `.html`.** The hero and social captions are the most
expensive text in the project to get wrong, for the same reason.

**The sample estate is fictional throughout.** Hosts are `LAB1` to `LAB6` on
the RFC 5737 documentation range, in the fixtures, the comments, the README,
and every screenshot. No real hostname or address appears in a tracked file.

**The replace button is called Replace... rather than Preview, and sits under
Find.** Reported from a full run through the tool: someone who has just typed a
replacement goes looking for the verb they are about to perform, and a button
labeled Preview reads as an optional extra beside the real one - so they hunt
for a Replace button that is not there. The ellipsis is the standard promise
that it opens something rather than acting, and what it opens is unmistakably a
preview: the window is titled Preview Replace and its button says Apply.

It also moved out of the Cancel column and into the Find column. Find and
Replace are the two verbs the window offers and belong stacked together; Cancel
belongs to the search that is running, and lining Replace up beneath it put the
row's action in the row above's afterthought column.

**Ctrl+F narrows the results.** A search across a folder of session logs
answers with a thousand hits, and the next question is which of them came from
one host. That is a question about the results, not a reason to search the disk
again. The bar filters rather than stepping match to match: a host appears on
dozens of rows, so find-next would mean pressing it dozens of times where a
filter answers in one go and leaves a short list to double-click. Escape
restores everything, and a new search clears it - a filter left over from a
previous result set would hide the new one, which is the same trap as a stale
scroll position.

A filter matching a **filename** keeps that file's hits whole, because the host
is in the name and not in any of the matched lines; keeping only the lines that
happen to contain it would answer with almost nothing and look broken. A filter
matching a **line** keeps that hit on its own.

**Copy All Results, on the results right-click menu.** Pasting a whole result
set into a second file and reading it there is a normal way to work through
one. It copies everything the filter admits regardless of whether a group is
collapsed: a filter is a statement about which results are wanted, while
collapsing is a way to get a long list out of the way while reading.

**Double-clicking a file header opens the file**, at its first match rather
than at line 1 - someone double-clicking the header of a log that matched on
line 1402 wants to be at line 1402, not at the top of a session transcript.
Collapsing moved to the indent left of the filename, which is also what made
the double-click possible: while a click anywhere on the header toggled the
group, the two clicks arrived first and cancelled each other out. The indent
width is now one number shared by the drawing and the hit test, so a strip
that looks like the arrow but does not toggle cannot appear.

**The planted-defect harness gives each plant its own directory.** Reusing one
folder meant deleting the previous `Planted.exe` every iteration, and Windows
does not always release an executable the moment its process exits - a lingering
handle turned that delete into an access-denied that aborted the run halfway
through and reported it as a permissions error rather than as anything to do
with the plants.

**A second search after scrolling through the first one rendered a blank
pane.** Reported from real use: search `smartctl` across a folder of session
logs, scroll down to read the results, then search for a drive serial. The
status line said it had found matches and the pane showed nothing.

A native ListView does not clamp its scroll offset when the item count shrinks
under it. Going from 960 rows to 16 while parked at row 381 left the viewport
past the last row, and the control then reported its top index as **-381**. The
part that made it look broken rather than merely odd is that a short result set
has no scrollbar, so there was no way to scroll back into range - the results
were unreachable, and the count in the status line read as the tool lying about
having found something. A longer follow-up search grew a scrollbar, and
dragging it snapped the view back, which is why it looked like a repaint
problem.

The fix reads the scroll offset **before** the row count changes, while the
control is still in a state it can act on, and pulls the view home only when
the offset would end up past the end. Growing is left alone, so results
streaming in during a scan do not yank the view away from someone reading them,
and collapsing a group above the fold keeps their place.

The rule lives in `ViewRules.cs` as a pure function so it is tested rather than
only reproducible by driving a window. Writing the planted defect for it showed
that the explicit "if the list grew, do nothing" guard was dead code - a valid
top index is always below the count, so growth cannot trigger the rule. The
guard was removed rather than kept with an untestable plant beside it.

**Replace, behind a preview that cannot be skipped.** There is no command-line
replace, no "replace all" that bypasses the window, and no path to the write
code except by reading what it would do first. Eyeballing the results before
replacing stops being a habit someone has to remember and becomes the only way
through the feature.

**The replacement takes the case of what it replaced.** Lower stays lower, a
sentence opener stays capitalized, and a heading in capitals stays in capitals.
Without this a documentation pass produces a hundred new sentence-case mistakes
alongside the fix it was meant to make. A replacement carrying its own capitals
is used literally, because it was typed that way on purpose.

**Three integrity checks stand between a stale preview and a write, and each is
tested against the edit only it can catch.** Size, timestamp, and re-reading
every line being edited. Writing the planted-defect entries for these is what
revealed that the original single test only ever exercised the size check; the
other two guards were untested and both plants passed. There are now three
tests and three plants.

**Verification happens twice.** Once to build the preview and again
immediately before the write, because minutes can pass while someone reads it.

**Files are rebuilt from character offsets, not by splitting and rejoining
lines.** Rejoining normalizes line endings, adds or drops a trailing newline,
and rewrites every line in a file where one was meant to change. The tests
assert that CRLF survives and that a file with no final newline does not gain
one.

**An encoding that cannot store the replacement is refused, not written.**
`Encoding.Default` turns anything outside its codepage into a question mark and
reports success, so replacing a word in a Windows-1252 file with one containing
a Greek letter would silently destroy it. Every write is round-tripped through
its own encoding first and refused if it does not come back identical.

**Undo restores a whole run as a unit, and refuses any file edited since.**
Originals are copied to `%APPDATA%\RSFind\undo\<timestamp>\` before anything is
written, with a manifest recording what was written so a later edit can be
detected. Restoring over someone's subsequent work would be a worse mistake
than the one being undone.

**Strip-ANSI now marks a file transformed only if it actually stripped
something.** It defaults to on, so a flag set unconditionally would have made
Replace refuse every file in a folder of Markdown - the option would have been
a trap rather than a default.

**Runs are capped at 5,000 changes.** Above that a preview stops being a
preview, so the run is refused with advice to narrow the search rather than
showing a sample and implying the rest was reviewed.

**charcheck grew a scoped opt-out.** The case-preservation code and its tests
have to spell out the British spellings they convert. Exempting whole files
would let real drift hide in the two largest sources in the project, so the
opt-out is a marked region that suspends only the spelling scan - dashes are
still checked inside it, and drift outside it still fails.

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
