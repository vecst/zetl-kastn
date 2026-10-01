# Zetl

**Tap your shortcuts like always. Hold them, and Zetl keeps what you're working on.**

Zetl isn't a notes app, and it isn't a clipboard manager. It lives inside the
shortcuts your hands already know. Tap `Ctrl+C` and you copy, the way you always
have. Hold it a moment longer and what you copied is saved: a quote, a number,
a picture, a passing thought, all without leaving the window you're in. Later,
hold `Ctrl+V`, and everything you gathered comes back out as one piece of
writing, a list, or rows for a spreadsheet.

Capture takes a second. Composing takes a minute. Nothing in between asks you to
stop what you're doing.

<!-- Demo GIF goes here once recorded: a full-screen video playing, a held
     Ctrl+X with a key overlay and a timer showing the hold, a quick note
     typed, Ctrl+Enter, and back to the video. Suggested path:
     docs/media/hold-demo.gif -->

## How it feels

- You're watching a video full screen and an idea hits. Hold `Ctrl+X`, type
  it, press `Ctrl+Enter`. The video never stopped.
- You're reading an article. Select a paragraph and hold `Ctrl+C`. It's saved,
  along with the app and page it came from.
- You've collected a column of figures. Click into a spreadsheet, hold
  `Ctrl+V`, choose TSV, and they land in a grid of cells. No import dialog.
- At the end of the day, hold `Ctrl+V` anywhere. Everything you collected is
  ready to paste as notes, a list, or table rows.

## Why holding

There's nothing new to learn and nothing new to remember. Every shortcut still
does exactly what it did; Zetl only answers when you hold one, so it works the
same in every app you use.

The first time you run it, a short tour measures how long your own taps and
holds take and sets the timing to fit your hands. A small ring in the corner of
the screen fills while you hold and shows how long you held, so you can feel
exactly where a tap ends and a hold begins.

## What it's for

- **Collecting while you read, watch, or research.** Quotes, links, numbers,
  and pictures, saved in the moment without switching windows.
- **Catching a thought** the second you have it, before it's gone.
- **Composing what you collected** into a tidy list, a document, or rows for a
  spreadsheet, ready to paste anywhere.
- **Pasting a prepared list back in order**, one `Ctrl+V` at a time, for filling
  in forms or entering data.

## The shortcuts

| Hold | What happens |
| --- | --- |
| `Ctrl+C` | Save what you copied, with a chance to edit it first |
| `Ctrl+X` | Write a quick note |
| `Ctrl+A` | Select everything and save it |
| `Ctrl+V` | Compose: gather your notes into text, a list, or table rows, and paste |
| `Ctrl+B` | Open the Board, where your projects live |
| `Ctrl+T` | Start a new project from a template |
| `Ctrl+J` | Switch between your Journal and your last project |
| `Ctrl+R` | Replay: paste a saved list back one item at a time |
| `Ctrl+P` | Pop: remove each saved item as you paste it |
| `Ctrl+Z` | Undo Zetl's last action |

Add `Shift` (`Ctrl+Shift+C`, `Ctrl+Shift+X`, …) for a second, separate
workspace, so a work project and a personal one can run side by side.

You can change what each hold does in **Settings → Hold Actions**.

## Where your notes go

Anything you save without picking a project lands in your **Journal**, sorted
by day. When you're working on something specific, start a project and Zetl
files your notes there instead.

While a project is active, Zetl also saves every ordinary `Ctrl+C` into it, so
a research session collects itself. The tray icon turns green whenever that's
happening. When it's grey, a normal copy is just a copy, unless you've asked
Zetl in Settings to save every copy to your Journal.

## Zetl and Kastn

Zetl comes in two parts:

- **Zetl** runs quietly in the notification area by the clock. It does the
  catching: quick, out of the way, gone as soon as you're done.
- **Kastn** is the workbench you open when you want to sit down with what you
  collected: browse it, organize it, edit it, link notes together, and export
  it to Markdown, HTML, or PDF. Open it from Zetl's tray menu.

## Install

1. Download the latest `Zetl-…-win-x64.zip` from the
   [Releases page](https://github.com/vecst/zetl-kastn/releases/latest).
2. Extract the whole zip into a folder and keep its files together.
3. Run `Zetl.exe`. Its icon appears in the notification area by the clock,
   and the tour opens. It's in the tray menu if you want it again.

There's nothing else to install. Zetl is new and not yet code-signed, so
Windows may show a SmartScreen warning the first time: choose **More info →
Run anyway**.

Zetl runs on 64-bit Windows. It can't reach apps running as administrator
unless Zetl is run as administrator too.

> Zetl is in release-candidate testing. Expect some rough edges, and please
> [report what you find](https://github.com/vecst/zetl-kastn/issues).

## Your notes stay on your computer

Zetl has no account and no cloud. Everything you save lives in readable files
under `%AppData%\Zetl` on your own machine. Copies that a password manager marks
as private are never read or saved, even while a project is collecting your
copies. The only time Zetl goes online is when you copy a link to an image: it
downloads that image so it can save the picture. When you export a project to
share, a clean copy leaves out which apps and windows your notes came from.

## Learn more

- [User guide](docs/guide.md): everything Zetl and Kastn can do, in detail
- [Building from source](docs/building.md): for developers and the curious

Zetl is free software under the [GNU General Public License v3](LICENSE) or
later.
