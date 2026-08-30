# Deploying RSFind

RSFind is a single executable with no installer, no runtime download, and no
dependency that is not already on the machine. This document covers building
it, putting it somewhere sensible, wiring it into Explorer, and removing every
trace of it again.

## Build

```bash
Build-RSFind.cmd
```

That invokes `csc.exe` from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`,
which ships inside Windows. There is no SDK to install, no NuGet restore, and
no network access at any point in the build. The script falls back to the
32-bit compiler path if the 64-bit one is absent.

The output is `RSFind.exe`, a few hundred KB, referencing only
`System.Windows.Forms`, `System.Drawing`, `System.Xml`, and
`System.IO.Compression` from the framework already installed. The last two are
what read `.xlsx` and `.docx`; they have shipped in the box since .NET
Framework 4.5.

### The one thing that will bite

**The build is pinned to C# 5**, because that is the ceiling of the in-box
compiler. String interpolation, null-conditional operators, expression-bodied
members, `nameof`, and `out var` are all unavailable, and the error messages
for them are unhelpful - the compiler reports a syntax error at the character
rather than saying the feature is too new. If a build fails on a line that
looks obviously correct, check it against C# 5 before checking anything else.

## Install

There is nothing to install. Copy `RSFind.exe` wherever you keep tools; it
writes only to `%APPDATA%\RSFind\settings.ini` and, if you ask it to, to your
own registry hive.

Put it somewhere stable **before** adding the Explorer entry: the entry records
the absolute path of the executable at the moment you add it, so moving the exe
afterwards leaves a menu item that fails silently. Re-adding it fixes that.

## The Explorer right-click entry

**Menu > Add to Explorer Right-Click Menu** writes two keys under `HKCU`:

```
HKCU\Software\Classes\Directory\shell\RSFind
HKCU\Software\Classes\Directory\Background\shell\RSFind
```

Each carries the label, an `Icon` value pointing at the exe, and a `command`
subkey of `"<path to exe>" "%V"`.

Three notes on that:

- **`HKCU`, not `HKLM`.** No elevation prompt, nothing written outside this
  user's hive, and an uninstall that is a registry delete rather than a
  scavenger hunt.
- **Both `Directory` and `Directory\Background`.** The first is right-clicking
  a folder; the second is right-clicking the empty space inside one. People use
  both and expect the same menu.
- **`%V`, not `%1`.** `%1` is empty for the background verb, so a command built
  with it opens the app with no folder when invoked from inside a directory -
  which looks like the app ignoring the click.

## Uninstall

1. Use the same menu item to remove the Explorer entry (it toggles), or delete
   the two keys above by hand.
2. Delete `%APPDATA%\RSFind`.
3. Delete `RSFind.exe`.

That is everything RSFind touches. No service, no scheduled task, no files
outside those two locations.

**Before you delete `%APPDATA%\RSFind`, know what is in it.** Alongside
`settings.ini` there is an `undo\` folder holding a copy of every file changed
by a replace, one folder per run. That is the only record of what those files
looked like beforehand. Nothing prunes it automatically - a tool that quietly
deleted the evidence of its own writes would be making exactly the wrong
tradeoff - so it is yours to clear out when you are satisfied with a change.

## What it does on a network

Nothing. RSFind makes no outbound connections of any kind: no update check, no
telemetry, no crash reporting. The build references only in-box framework
assemblies, so this is verifiable from the build script rather than something
you have to take on trust.

It will happily search a UNC path or a mapped drive if you point it at one, at
whatever speed the share allows. There is no network-specific optimization, and
a slow share will feel slow.

## Running the checks

```bash
tools\Run-Tests.cmd
```

Builds and runs 274 engine checks with the same in-box compiler, then the house
style check. To confirm the checks can still fail after a refactor:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Plant-Defects.ps1
```

That copies the tree, plants thirty-seven defects into the copies one at a time,
and verifies that each is caught by the check that owns it. It never modifies
the working files.

## Re-rendering the images

`docs/hero-quadrants.png` and `docs/social-preview.png` are composed as HTML in
`docs/src/`, not drawn. The layout is text, so a caption typo is a one-line fix
and a re-render rather than an image edit - which matters because an image
cannot be grepped or corrected once links to it are cached.

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Render-Png.ps1 -Html docs\src\hero.html -Out docs\hero-quadrants.png -Width 2000 -Height 1392
```

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Render-Png.ps1 -Html docs\src\social.html -Out docs\social-preview.png -Width 1280 -Height 640
```

It renders with the headless Chrome or Edge already on the machine, and fails
rather than publishing a cropped image if the page overflows its frame.

The four panels under `docs/src/panels/` are real captures, not mock-ups. The
hosts and addresses in them are invented - `LAB1` to `LAB6` on the RFC 5737
documentation range - and that is a rule, not a convenience: no real hostname
or address belongs in a tracked file, screenshots included.

### Why the panels have no title bar

They are the client area only, and the frame around them is drawn by the HTML.

A window's title bar and border are not painted by the window on anything since
Vista - the Desktop Window Manager composites them from a separate surface. A
capture taken from inside the process therefore cannot see them.
`Control.DrawToBitmap` sends `WM_PRINT`, which asks the window to draw a frame
it does not own, and gets back the legacy `DefWindowProc` caption: the one that
looks like Windows 7, in its inactive colors because an automated session has
no foreground window for the form to become. `PrintWindow` with
`PW_RENDERFULLCONTENT` returns a different wrong frame rather than a right one.

The proof, if you want to repeat it: capture twice with
`DWMWA_USE_IMMERSIVE_DARK_MODE` set to 1 and then 0. That attribute changes only
the DWM frame. The two title bars come back byte-identical, so the capture is
not seeing DWM at all.

Cropping is also the better answer regardless of the mechanism. A real frame in
a product shot varies with the Windows version, the accent color, and the light
or dark setting of whatever machine took the picture, none of which are facts
about RSFind.

**To use real frames instead**, take four screenshots on an interactive desktop,
drop them into `docs/src/panels/` under the same names, and re-render. Nothing
else changes, and the HTML frame will simply sit behind a picture that already
has one - remove the `box-shadow` ring in that case.

**The social preview has to be uploaded through GitHub's repository settings.**
Committing the file only versions it; it does not become the card. That upload
is only possible once the repository is public.
