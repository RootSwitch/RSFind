# RSFind

Search the text *inside* the files in a folder, on demand. Point it at a
directory, type what you remember, and get every line that contains it,
grouped by the file it came from.

It builds no index, installs no service, and watches nothing in the
background. That is the point: it is the tool you reach for on the day
Windows Search returns filenames and nothing else.

![RSFind searching a folder of terminal session logs](docs/screenshot.png)

## What it does

- **Searches file contents, right now.** No indexer to wait for, no catalog to
  go stale, no folders to opt in ahead of time.
- **Groups hits by file**, collapsible, with the match highlighted in place and
  optional context lines above and below.
- **Streams results as it goes**, with a Cancel button that actually stops the
  scan rather than hiding it.
- **Understands terminal logs.** Raw session logs carry ANSI escapes; RSFind
  strips them before matching, so a phrase that straddles a color code is still
  findable and the results read like the terminal did.
- **Handles encodings that trip other tools.** UTF-8 with or without a BOM,
  UTF-16 and UTF-32 with one, and old ANSI exports, all in the same folder.
- **Adds itself to the folder right-click menu**, per user, without admin.

## Quickstart

Build it with the C# compiler already inside Windows. There is no SDK to
install, nothing to restore, and no network access at any point:

```bash
Build-RSFind.cmd
```

Then run `RSFind.exe`. To put it in Explorer, open **Menu > Add to Explorer
Right-Click Menu**; right-clicking a folder (or the empty space inside one)
then offers **Find Text with RSFind**, which opens the app already pointed at
that folder. The same menu item removes it again.

You can also pass a folder on the command line: `RSFind.exe C:\logs`.

## Options

| Option | What it does |
|---|---|
| Match case | Off by default, so `Serial` finds `SERIAL`. |
| Match whole word | `nvme0` stops matching inside `nvme0n1`. |
| Use regex | .NET regular expressions. A half-typed pattern reports itself on the summary line instead of throwing. |
| Include subfolders | On by default. |
| Exclude binary files | On by default. A NUL byte in the first 8 KB means binary - unless a byte-order mark says otherwise, because UTF-16 text is half NUL bytes. |
| Strip ANSI escapes | On by default. See the note below. |
| File mask | `*.log;*.txt` - semicolons, commas, or spaces all separate. Blank means every file. A bare `.log` is read as `*.log`. |
| Exclude | Same syntax, applied first. Exclude beats include. |
| Skip over | Files larger than this many MB are not read. 0 means no limit. |
| Context lines | How many lines to show before and after each hit. |

Preferences live in `%APPDATA%\RSFind\settings.ini`, which is a plain
`key=value` file you can read and edit. **Search queries are never written
there, and no history of past searches is kept.** A query typed against a
folder of console logs is as likely to be a serial number, a hostname, or a
password as it is to be `smartctl`, and a search tool that quietly keeps a list
of everything you looked for is a tool you have to think about before using.

### Two defaults worth knowing about

**Strip ANSI escapes is on.** Terminal session logs are full of escape
sequences; with stripping off, every prompt line renders as a row of boxes and
a phrase that spans a color code cannot be matched at all. Turn it off when you
want to see exactly what is in the bytes. It is one checkbox either direction.

**Results are capped** at 5,000 hits per file and 200,000 in total. A search
for a common word across a log folder is one keystroke away, and the cap is
what keeps that from becoming a memory problem. Whenever a cap bites, the
summary line says so - a short list that does not admit it is short reads as
"that is all there is", which is the one thing a search tool must never imply.

## Opening a result

Double-click a hit, or press Enter. By default the file opens with whatever
Windows associates. To jump straight to the line, set an editor command under
**Menu > Editor Command**, using `{file}` and `{line}` as placeholders:

```
notepad++ {file} -n{line}
```

## What it deliberately does not do

- **No index and no background service.** Nothing runs when the window is
  closed, so there is nothing to trust, nothing to update, and nothing that
  could have been reading your disk while you were not looking.
- **No network access, ever.** No update check, no telemetry, no crash
  reporting. The binary references only assemblies that ship with Windows.
- **No Replace.** Not yet, and possibly not at all - it is the one button that
  can quietly damage a directory. If it lands, it will act only on the result
  set you are already looking at, with a per-file preview before anything is
  written. The engine already records the encoding, byte-order mark, and line
  ending of every file it reads so that a rewrite could be exact.
- **No `.xls` or `.pdf`.** `.xlsx` and `.docx` support is planned; the older
  binary formats are not.
- **No search history.** See above.

## Requirements

Windows with .NET Framework 4.x, which every supported version of Windows
already has. Nothing else.

## License

The Unlicense. See [LICENSE](LICENSE).
