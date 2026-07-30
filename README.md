# Stepwright

A tool for Windows and macOS that watches you do a task once and writes the guide for you.

Click through the procedure. Stepwright captures every click, keystroke, shortcut, drag and
scroll, takes a screenshot at the exact moment of each action, reads the real name of the
control you used, and turns all of it into a numbered guide with pictures. Then you tidy the
wording and export.

It does what Scribe does, plus the parts Scribe puts behind a login, a subscription or a
browser extension.

There are two applications here, one per platform, sharing a file format rather than a code
base. Nothing underneath is portable: the input hooks, the screen capture and the accessibility
tree are different on each, and so are the interface toolkit and the drawing layer. What is
shared is the `.stepwright` file, so a guide recorded on one opens on the other.

## What it gives you that Scribe does not

| | Stepwright | Scribe |
| --- | --- | --- |
| Desktop applications, not just the browser | yes, every window on Windows and macOS | paid tier only |
| Cost | free, no account | free tier is browser only and watermarked |
| Where your screenshots go | nowhere, they stay on your machine | uploaded to their cloud |
| PDF, Word and Markdown export | included | paid tier |
| Blur and redact | included, plus automatic password blanking | paid tier |
| Works with no internet | yes | no |
| Writing assistant | optional, and you pick the service: GPT, Claude, Gemini or your own | their model only |
| Screenshot framing | four framings per step, switchable after recording | one |
| Animated steps | built from the screenshot you already took, no extra recording | video is a separate paid product |
| Publishing | straight into Hudu or Confluence, no file in between, on both platforms | their own workspace only |
| Output format | yours, defined in a file you can edit and share, the same file on both | fixed |
| A whole guide as one animation | included | not offered |

## How it works

1. Press **Record**, or the shortcut, from anywhere.
2. Do the task once. A small bar floats on top with a timer, a step count and a Finish button.
   Windows is asked to leave that bar out of every screenshot, so it never appears in the guide.
3. Press **Finish**. Every step is already written and illustrated.
4. Pick how each screenshot is framed: the whole screen, just the window, around the control,
   or tight on it. One click, and you can apply the same choice to every step at once.
5. Press **Animate** on any step that deserves movement, or animate every step at once.
6. Edit anything: reword a step, reorder, merge, hide a step, crop, blur, add arrows and labels.
7. Optionally press **Improve with AI** to tidy the wording and add notes.
8. Export.

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

## Formats

Every export and every publish is written by a **format**: the typeface, the sizes, whether the
styling travels on each element or in a stylesheet, how pictures are carried, and what goes in
the footer. Four ship with the app.

| Format | What it is for |
| --- | --- |
| Stepwright | The default look: a styled page with rounded pictures |
| Hudu | One container, inline styles, Arial, sixteen point bold headings, fourteen point body, no tables and no colour so Hudu controls light and dark mode |
| Confluence | Storage format, with pictures attached to the page and referred to by name |
| Plain | Headings, paragraphs and pictures with no styling at all |

A format is a small text file. Settings, on the Format page, will import one, export the one you
are using, or duplicate it so you can edit your own copy. They live beside the settings, so a
format can sit in your own configuration and be handed to someone else.

The file holds 32 settings, and the same file works on both apps. Windows keeps them in
`%APPDATA%\Stepwright\formats`, macOS in `~/Library/Application Support/Stepwright/formats`.

```json
{
  "Name": "Hudu",
  "InlineStyles": true,
  "SingleContainer": true,
  "AllowColor": false,
  "FontFamily": "Arial, sans-serif",
  "HeadingSize": 16,
  "BodySize": 14,
  "NotePrefix": "Note: ",
  "UseJpeg": true,
  "ImageDisplayWidth": 700,
  "FooterText": "Published from Stepwright on {date}"
}
```

`ImagePlaceholder` is the one worth knowing about. Set it and the picture is not written into
the markup at all, replaced by whatever you put there with `{n}` becoming the step number. That
is how the Confluence format works, and it is how you would target any other system that keeps
pictures separately.

There is no field by field editor in the app. Duplicate and edit copies the format and opens the
folder, and you change the file in a text editor.

## Exports

* Web page in a single file, with every picture inside it, ready to email or drop on a share
* Web page with a separate images folder
* Markdown with an images folder, for a wiki, a repository or a knowledge base
* Word document, built straight to the open packaging format with no Office needed
* PDF, written directly with the text font embedded, so it looks the same everywhere and the
  words stay selectable and searchable rather than being flattened into pictures
* Copy for pasting, which puts rich content on the clipboard for a knowledge base, an email or a ticket
* Copy as plain text

### Animation

Two kinds, and they answer different needs.

* **A single step.** Press Animate while editing it. The picture starts wide, so the reader
  sees where they are, then settles on the control that was used. Web pages and Markdown show
  it moving. A Word document or a PDF quietly uses the still picture, since neither can animate.
* **The whole guide, as one animation.** Export, then "The whole guide as one animation". Every
  step in order, each held long enough to read, with its number and a bar showing how far along
  it is. This is the one to paste into a chat or put at the top of a page.

Both are built from the screenshots already captured, so there is nothing to record twice and
nothing to time. The same step always produces the same animation. The only two settings, under
Look, are how lively the movement is and how wide the file is written.

### Publishing straight into a knowledge base

Publish, then Hudu or Confluence. The guide goes across as an article with no file in between.

* **Hudu** keeps pictures inside the article, so it goes in one piece. Choose the company, the
  folder, and whether to create a new article or replace one that is already there.
* **Confluence** keeps pictures as attachments, so the page is created first and each picture
  is attached to it afterwards, with the text already referring to them by name. Choose the
  space and, if you want, a page to file it under.

Set both up under Settings, on the Publishing page. Hudu needs the address of your site and an
API key from Admin then API, which is the only thing its API accepts. Confluence takes either
the email you sign in with plus an API token, or a browser sign in.

To sign in through the browser, register an application once in the Atlassian developer
console, give it the Confluence permissions, and add `http://localhost:53682/callback` as its
callback address. Put the identifier and the secret into Settings and press **Sign in to
Atlassian**: the browser asks you, the answer comes back to Stepwright, and from then on it
renews itself without asking again. Atlassian issues these tokens to a named application, which
is why the application has to be yours rather than one shipped inside the app.

Each has a button that proves the connection before you rely on it. The secrets are encrypted
for your Windows account on Windows, and kept in the keychain on macOS.

### Steps without a picture

Not every step needs one. **Add note** writes a step that is words only, and Options then
**Use words only for this step** takes the picture off one that has it. The picture is only set
aside, so pressing it again brings it back, and it is dropped for good the next time the guide
is saved. Text only steps carry through every export and both destinations.

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

Press **Find models** and Stepwright asks that service which models your key is actually allowed
to use, then fills the list. You can still type a name yourself.

### Using a subscription you already pay for

**How it signs in** offers a second route. If you already pay for Claude Pro or Max, ChatGPT
Plus, or you have a Google account, the command line app that comes with that plan is already
signed in on your machine, and Stepwright can put its questions to that instead of to a key
billed by the token.

| Service | The app it runs | What pays for it |
| --- | --- | --- |
| Anthropic | `claude` | Claude Pro or Claude Max |
| OpenAI | `codex` | ChatGPT Plus, Pro, Business or Enterprise |
| Google | `gemini` | a Google account, or Gemini Code Assist |

Install the app, run it once in a terminal and sign in, then choose that route in Settings and
press **Check the app**. Stepwright looks along the search path and in the usual install
folders, and there is a box for the path when yours lives somewhere unusual. Nothing is stored
here: the app holds its own sign in, and any key sitting in the environment is deliberately
kept out of the child process so the work cannot land on paid billing by accident.

Leave the model empty to use whatever that app is already set to. The screenshot switch still
works: each picture is written into a folder that is deleted afterwards, and the app is asked
to open it, which costs an extra moment per step compared with the key route.

There is a third route, marked advanced, that sends a Claude subscription token straight to the
service. It is faster, and it is outside the terms of a consumer plan, since those tokens are
issued for the vendor's own app. Accounts have been suspended for it. The first two routes are
the ones to use.

It rewrites the wording of every step and adds a short note where one genuinely helps.

Then it does the harder thing: it decides how many steps there should have been. A recorder
writes one step per action, which is finer than any reader wants. Opening a tab, typing an
address and pressing Enter are three recorded actions and one instruction. The assistant folds
runs like that into a single step, splits a step that holds two actions, and sets aside the
ones that carry nothing: a stray click, a repeat, a value typed and then typed again
differently, a page opened and then left. Eight recorded steps commonly come out as two.

Nothing is deleted. A step that is folded away or set aside stays in the editor marked hidden,
so the Hide button brings it back, and the whole pass can be undone with one answer when it is
finished.

There is a second switch: **let the assistant see each screenshot**. It is off by default, and
it is the one that changes the quality dramatically. The recorder can only name a control as
well as the application describes itself, which inside a browser is often not at all, which is
how you end up with a step that says "Click Omnibox Popup". When the assistant can see the
picture, with the click marked on it, it names what is actually there. With this off, only the
step text and control names are sent. With it on, the picture for each step goes to the service
you chose, and nowhere else.

### Pictures you already have

Not everything worth writing up was recorded live. **Add pictures** takes a folder of
screenshots, a set someone sent you, or anything else you have, and turns each one into a step.
They are ordered the way their names read, so picture 2 lands before picture 10, and the Up and
Down buttons fix anything the names got wrong. The files are copied into the guide, so deleting
the originals afterwards cannot empty a guide that looked finished.

Then you choose who writes. Type the steps yourself, or let the assistant read the pictures and
draft them, which it does one at a time with the step before it as context so the result reads
as a guide rather than a list of captions. Either way you can edit every word afterwards.

### Recording inside a remote session

Remote Desktop, ScreenConnect, Splashtop, TeamViewer, AnyDesk and the rest all show the far
machine as a picture inside a window. There is no accessibility tree behind that picture, so
nothing inside it can be named, and the only thing the system will report is the viewer's own
window title, complete with its version, its session number and a clock that ticks every
second.

Stepwright recognises those applications and refuses to pass any of that off as the name of a
control. A step recorded in one reads "Click the highlighted spot in AZ-UTILITY01 through
ScreenConnect", with the far machine named the same way on every step.

This is the case the picture switch was built for. Turn on **let the assistant see each
screenshot** and the assistant reads the pixels, which is the only thing that can name a button
on the far side.

One thing to know before you record an admin session. Passwords are hidden when Windows says
the box is a password box, and there is no such signal behind a remote picture, so anything
typed on the far machine is written into the guide as text. That is deliberate: a remote guide
is mostly made of the values you type, and blanking them would leave it useless. Your own
redaction patterns under Settings still apply, and the recorder can be paused, so use one of
those when credentials are about to be typed.

## Install

### Windows

Download `Stepwright.exe` from the latest release and run it. Nothing to install and no
runtime needed, because the self contained build carries everything with it.

`Stepwright.small.exe` in the same release is a few hundred kilobytes instead of sixty
megabytes, for machines that already have the .NET 8 desktop runtime.

### macOS

Download `Stepwright.dmg`, open it, and **drag Stepwright to the Applications folder** shown
beside it. Then open it from Applications. It is a native app of under a megabyte, with nothing
to install alongside it. Ventura or later.

Moving it is not a formality. macOS runs an app straight from a download at a throwaway path
that changes on every launch, so any permission you grant is attached to a path that will not
exist next time. That is why permissions can appear to be ignored no matter how many times you
grant them. From Applications they stay granted. Stepwright notices when it is in the wrong
place and offers to move itself.

Because the app is not signed by a developer Apple recognises, the first launch needs a right
click then Open, rather than a double click. See the section below on that.

`Stepwright-mac.zip` is the same app without the disk image, for anyone who prefers it.

### The three permissions macOS asks for

Stepwright cannot record until all three are on. It shows them in a window that updates itself
as you grant each one, with a button through to the right settings pane.

| Permission | What it is for |
| --- | --- |
| Accessibility | Reads the name of the control you click, so a step can say what you actually pressed |
| Input Monitoring | Sees your clicks and keystrokes, which is what the steps are made from |
| Screen Recording | Takes the screenshot at the moment of each action |

**Input Monitoring and Screen Recording only take effect after the app is opened again.** macOS
reads them once when a process starts. Turning the switch on while Stepwright is running changes
nothing until it restarts, which is why it looks like the grant did not work. There is a Quit
and open again button in the permissions window that does it for you.

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

### If a permission will not stick

In order:

1. Is the app in your Applications folder? If it is anywhere else, especially still in
   Downloads, macOS may be running it from a temporary path and forgetting every grant. The
   permissions window says so when this is happening.
2. Did you open the app again after granting? Input Monitoring and Screen Recording are only
   read at startup.
3. Is there an old entry for Stepwright in the list with its switch on? Remove it with the
   minus button and add the current app again. An unsigned app is identified partly by its
   contents, so an entry left over from a previous version can refer to something else.

### What macOS shows instead

macOS refuses to open an app from an unidentified developer on a double click. Right click the
app and choose Open, and the dialog gains an Open button. That is a one time thing per machine.

To remove it properly you need an Apple Developer account, ninety nine dollars a year, which
lets you sign the app and send it to Apple for notarising. That is the only route, and it is
per account rather than per app.

### One thing worth knowing

A signature and a reputation are two different things. Signing tells Windows who made the file,
which removes "Unknown publisher". SmartScreen separately asks whether this file has been seen
before, and only an extended validation or Trusted Signing certificate carries reputation from
the start. With an ordinary certificate the first few people still see a warning.

## Good to know

### On macOS

* Two permissions are required and the app will say so: Accessibility and Screen Recording.
* The recorder bar is kept out of every screenshot by marking the panel as not for sharing,
  which is the platform's own facility for exactly this.
* macOS says when it has switched a slow event tap off, so the recorder switches it back on
  rather than quietly recording nothing.
* Function key shortcuts are F9 to start or pause, F10 to finish and F8 to capture, the same as
  on Windows. If your keyboard sends media keys instead, hold Fn, or change the behaviour under
  Keyboard in System Settings.

### On Windows

* Recording another program that runs as administrator needs Stepwright to run as
  administrator too. Windows blocks input hooks from a process with fewer privileges, and it
  also hides the accessibility tree, so those steps arrive with no control name and read
  "Click the highlighted spot" until you reword them.
* Windows 10 version 1607 or later. The key translation relies on a flag added in that release.
* The bar is hidden from screenshots on Windows 10 version 2004 and later. On older builds it
  is visible in the capture, so move it out of the way.

## Build it yourself

### Windows

```bash
dotnet publish src/Stepwright -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The project targets `net8.0-windows`. It cross compiles from macOS or Linux with
`-p:EnableWindowsTargeting=true`, which is what `build.sh` at the root does.

### macOS

```bash
mac/Stepwright/build.sh
```

Swift and the command line tools are all it needs, with no Xcode project involved. The script
compiles the sources and assembles `Stepwright.app` around them, then signs it so macOS gives
it a stable identity for the permission prompts.

## How it is put together

The Windows app:

```
src/Stepwright
  Native/      low level mouse and keyboard hooks, window queries, global shortcuts
  Capture/     screen grabs and the accessibility lookup that names what was clicked
  Recording/   turns raw input into steps, merges typing, redacts secrets
  Model/       the guide document
  Render/      crop, zoom, click marker, blur, arrows, labels and the animations
  Export/      html, markdown, word, pdf, and the guide file format
  Publish/     sending an article to Hudu or Confluence
  Export/Pdf/  the document writer, portable on purpose so it can be tested anywhere
  Export/Gif/  the animation writer, portable for the same reason
  Ai/          the optional writing pass
  Ui/          the window, the editor and the floating recorder bar
```

The macOS app:

```
mac/Stepwright/Sources
  Platform.swift    permissions, screen capture and picture files
  Inspector.swift   the accessibility lookup that names what was clicked
  Recorder.swift    the event tap and the state machine on top of it
  Renderer.swift    crop, zoom, click marker, blur, arrows and labels
  Animation.swift   the per step movement and the whole guide reel
  Exporters.swift   web page, markdown and the guide file format
  PdfExport.swift   the document, drawn by the platform
  Assistant.swift   the optional writing pass
  Agents.swift      the signed in command line apps a subscription can be reached through
  Publish.swift     sending a page to Hudu or Confluence
  Atlassian.swift   the browser sign in, and the listener that catches the answer
  Views.swift       the preview, the step rows and the floating bar
  MainWindow.swift  the editor
```

Both recorders follow the same discipline: the callback that sees an event does one thing, take
a single screen grab, because that has to happen before the application redraws itself.
Everything slow, the accessibility lookup and writing the picture out, happens on a worker
behind a queue. Windows drops a hook that takes too long without saying so, which is why the
rule matters there; macOS says when it has done it, so the tap is simply switched back on.

The PDF and animation writers under `Export/Pdf` and `Export/Gif` carry no dependency on the
platform or on any other library, and that is deliberate rather than a point of pride.

The document writer parses the TrueType file to embed the font, reads the jpeg header to state
each picture's geometry, and writes the object table and cross reference table by hand. The
animation writer reduces the colours with a median cut palette and does the compression the
format requires.

Because both are portable, `tools/PdfProbe` and `tools/GifProbe` build the very same code that
ships and produce real output on any machine, which is how they were checked: the document
parsed by a reader and rendered to images, the animation decoded frame by frame with its
durations, its looping marker and its colours compared against the source. The build repeats
both checks on every push, the document one against the real Windows fonts a person will get.
