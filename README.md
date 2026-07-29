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
| PDF, Word and Markdown export | included | paid tier |
| Blur and redact | included, plus automatic password blanking | paid tier |
| Works with no internet | yes | no |
| Writing assistant | optional, and you pick the service: GPT, Claude, Gemini or your own | their model only |
| Screenshot framing | four framings per step, switchable after recording | one |
| Animated steps | built from the screenshot you already took, no extra recording | video is a separate paid product |

## How it works

1. Press **Record**, or the shortcut, from anywhere.
2. Do the task once. A small bar floats on top with a timer, a step count and a Finish button.
   Windows is asked to leave that bar out of every screenshot, so it never appears in the guide.
3. Press **Finish**. Every step is already written and illustrated.
4. Pick how each screenshot is framed: the whole screen, just the window, around the control,
   or tight on it. One click, and you can apply the same choice to every step at once.
5. Edit anything: reword a step, reorder, merge, hide a step, crop, blur, add arrows and labels.
6. Export.

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
* PDF, written directly with the text font embedded, so it looks the same everywhere and the
  words stay selectable and searchable rather than being flattened into pictures
* Copy for pasting, which puts rich content on the clipboard for a knowledge base, an email or a ticket
* Copy as plain text

Any step can also be exported as a short animation that starts wide and settles on the control
that was used, built from the screenshot already captured rather than from a separate recording.
Web pages and Markdown show it moving. A Word document or a PDF uses the still picture, since
neither can animate.

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

## The assistant

Off until you turn it on. Pick a service in Settings and the address and model fill themselves
in: OpenAI for the GPT models, Anthropic for Claude, Google for Gemini, or anything that speaks
the OpenAI shape, which covers your own gateway and a local model. The key is stored encrypted
for your Windows account with the platform data protection interface.

It rewrites the wording of every step and adds a short note where one genuinely helps.

There is a second switch: **let the assistant see each screenshot**. It is off by default, and
it is the one that changes the quality dramatically. The recorder can only name a control as
well as the application describes itself, which inside a browser is often not at all, which is
how you end up with a step that says "Click Omnibox Popup". When the assistant can see the
picture, with the click marked on it, it names what is actually there. With this off, only the
step text and control names are sent. With it on, the picture for each step goes to the service
you chose, and nowhere else.

## Install

Download `Stepwright.exe` from the latest release and run it. Nothing to install and no
runtime needed, because the self contained build carries everything with it.

`Stepwright.small.exe` in the same release is a few hundred kilobytes instead of sixty
megabytes, for machines that already have the .NET 8 desktop runtime.

## The warning Windows shows

Windows warns about a program it has not seen before, and it keeps warning until the file is
signed by someone it can name. There is no way around that from inside the program itself.
These are the real options, cheapest first.

### Azure Trusted Signing, about ten dollars a month

Microsoft issues the certificate and holds the key, so there is no file to look after and
nothing to renew by hand. It is the least painful route by a distance, and the build already
supports it: create a Trusted Signing account and a certificate profile in Azure, then add
these repository secrets and every build signs itself.

```
AZURE_TENANT_ID            AZURE_CLIENT_ID            AZURE_CLIENT_SECRET
TRUSTED_SIGNING_ENDPOINT   TRUSTED_SIGNING_ACCOUNT    TRUSTED_SIGNING_PROFILE
```

Identity has to be verified first. An organisation needs three years of verifiable history, and
there is an individual option for a person. Verification usually takes a few days.

### A certificate bought from a certificate authority

Sixty to about four hundred a year depending on who you buy from and whether it is an
organisation or an extended validation certificate. Since 2023 the key has to live on a
hardware token or in a cloud vault, which makes signing on a build machine more involved.
An extended validation certificate is the only kind that skips the reputation wait outright.
Export it, then add `SIGNING_CERT_BASE64` and `SIGNING_CERT_PASSWORD` as repository secrets.

### A self signed certificate

Free, and it removes the warning **only on machines you have told to trust the certificate**.
That is the right answer for a fleet you manage, where the certificate can be pushed by policy
alongside the program. It does nothing at all for a stranger downloading the file.

```powershell
.\tools\sign.ps1 -Create -Subject "Your company" -Password (Read-Host -AsSecureString)
.\tools\sign.ps1 -TrustOnThisMachine -Password (Read-Host -AsSecureString)
```

### Nothing

Choose "More info" then "Run anyway" the first time. The warning goes away by itself once
enough people have run the same file, which for an internal tool may never happen.

### One thing worth knowing

A signature and a reputation are two different things. Signing tells Windows who made the file,
which removes "Unknown publisher". SmartScreen separately asks whether this file has been seen
before, and only an extended validation or Trusted Signing certificate carries reputation from
the start. With an ordinary certificate the first few people still see a warning.

### Notes

* Recording another program that runs as administrator needs Stepwright to run as
  administrator too. Windows blocks input hooks from a process with fewer privileges, and it
  also hides the accessibility tree, so those steps arrive with no control name and read
  "Click the highlighted spot" until you reword them.
* Windows 10 version 1607 or later. The key translation relies on a flag added in that release.
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
  Export/      html, markdown, word, pdf, and the guide file format
  Export/Pdf/  the document writer, portable on purpose so it can be tested anywhere
  Export/Gif/  the animation writer, portable for the same reason
  Ai/          the optional writing pass
  Ui/          the window, the editor and the floating recorder bar
```

The recorder never does slow work on the thread that owns the input hook. A hook callback only
takes the screen grab, then hands the rest to a worker queue, so Windows never drops the hook
for being late.

The PDF and animation writers under `Export/Pdf` and `Export/Gif` have no dependency on the
platform or on any other library.
It parses the TrueType file to embed it, reads the jpeg header to state the picture geometry,
and writes the object table by hand. Because it is portable, `tools/PdfProbe` builds the same
layout code on any machine, and the build checks it on every push against the real Windows
fonts a person will actually get.
