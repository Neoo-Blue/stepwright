# Stepwright

A Windows tool that watches you do a task once and writes the guide for you.

Click through the procedure. Stepwright captures every click, keystroke, shortcut, drag and
scroll, takes a screenshot at the exact moment of each action, reads the real name of the
control you used, and turns all of it into a numbered guide with pictures. Then you tidy the
wording and export.

It does what Scribe does, plus the parts Scribe puts behind a login, a subscription or a
browser extension.

## What it gives you that Scribe does not

| | Stepwright | Scribe |
| --- | --- | --- |
| Desktop applications, not just the browser | yes, every window on Windows | paid tier only |
| Cost | free, no account | free tier is browser only and watermarked |
| Where your screenshots go | nowhere, they stay on your machine | uploaded to their cloud |
| Word and Markdown export | included | paid tier |
| Blur and redact | included, plus automatic password blanking | paid tier |
| Works with no internet | yes | no |
| Writing assistant | optional, points at any endpoint you choose, including your own | their model only |

## How it works

1. Press **Record**, or the shortcut, from anywhere.
2. Do the task once. A small bar floats on top with a timer, a step count and a Finish button.
   Windows is asked to leave that bar out of every screenshot, so it never appears in the guide.
3. Press **Finish**. Every step is already written and illustrated.
4. Edit anything: reword a step, reorder, merge, hide a step, crop, blur, add arrows and labels.
5. Export.

## What gets recorded

| Action | What appears in the guide |
| --- | --- |
| Click | Click "Save" button in Notepad, with the control outlined and the click marked |
| Double click, right click, middle click | phrased correctly for each one |
| Typing | Type "invoice 4192" in the "Search" field, merged into one step per burst |
| Password box | Enter your password. The characters are never stored anywhere |
| Ctrl and Alt shortcuts | Press Ctrl + S |
| Enter, Tab, Esc, arrows, function keys | Press Enter |
| Scrolling | Scroll down, with one step per burst rather than one per notch |
| Dragging | Drag "Column header" to the right |
| Manual capture | press the capture shortcut for a screenshot with no action attached |

Step text comes from the Windows accessibility tree, so the names in the guide are the real
names of the buttons and fields, not guesses from pixels.

## Exports

* Web page in a single file, with every picture inside it, ready to email or drop on a share
* Web page with a separate images folder
* Markdown with an images folder, for a wiki, a repository or a knowledge base
* Word document, built straight to the open packaging format with no Office needed
* Copy for pasting, which puts rich content on the clipboard for a knowledge base, an email or a ticket
* Copy as plain text

The guide itself saves as one `.stepwright` file that holds the text and every screenshot,
so you can reopen and edit it later.

## Shortcuts

| Key | Action |
| --- | --- |
| F9 | start recording, or pause and resume |
| F10 | finish recording |
| F8 | capture the screen as a step right now |
| Delete | remove the selected step |
| Ctrl and Up or Down | move the selected step |
| Ctrl and S | save, add Shift to save under a new name |
| Ctrl and O | open a guide |

Turn on "Also hold Ctrl and Shift for these shortcuts" in Settings when a plain function key
clashes with something else you run.

## Privacy

Everything stays on your computer. There is no account, no telemetry and no upload.
Screenshots live in your local application data folder while a guide is open and inside the
`.stepwright` file once you save.

Two safety nets are on by default:

* Anything typed into a password box is never stored, not even in memory beyond the keystroke
* You can add your own patterns in Settings, and anything matching them is replaced before it
  reaches a step

The optional writing assistant is off until you turn it on. When it is on, only the step text
and control names are sent to the endpoint you configured. Screenshots are never sent. The key
is stored encrypted for your Windows account with the platform data protection interface.

## Install

Download `Stepwright.exe` from the latest release and run it. Nothing to install and no
runtime needed, because the self contained build carries everything with it.

`Stepwright.small.exe` in the same release is a few hundred kilobytes instead of sixty
megabytes, for machines that already have the .NET 8 desktop runtime.

### Notes

* Recording another program that runs as administrator needs Stepwright to run as
  administrator too, because Windows blocks input hooks from a lower privilege process.
* The bar is hidden from screenshots on Windows 10 version 2004 and later. On older builds it
  is visible in the capture, so move it out of the way.

## Build it yourself

```bash
dotnet publish src/Stepwright -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The project targets `net8.0-windows`. It cross compiles from macOS or Linux with
`-p:EnableWindowsTargeting=true`, which is what the local build script does.

## How it is put together

```
src/Stepwright
  Native/      low level mouse and keyboard hooks, window queries, global shortcuts
  Capture/     screen grabs and the accessibility lookup that names what was clicked
  Recording/   turns raw input into steps, merges typing, redacts secrets
  Model/       the guide document
  Render/      crop, zoom, click marker, blur, arrows and labels
  Export/      html, markdown, word, and the guide file format
  Ai/          the optional writing pass
  Ui/          the window, the editor and the floating recorder bar
```

The recorder never does slow work on the thread that owns the input hook. A hook callback only
takes the screen grab, then hands the rest to a worker queue, so Windows never drops the hook
for being late.
