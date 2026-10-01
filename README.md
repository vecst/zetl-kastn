# Zetl

**Hold the shortcuts you already know.**

Tap `Ctrl+C` and you copy, like always. Hold it for a third of a second and Zetl
saves what you copied as a note, without you leaving what you are doing.

<!-- Demo GIF goes here once recorded: a full-screen video playing, a held
     Ctrl+X with a key overlay and a timer showing the hold, a quick note
     typed, Ctrl+Enter, and back to the video. Suggested path:
     docs/media/hold-demo.gif -->

## What it's for

- **Collecting while you read, watch, or research.** Quotes, links, numbers,
  and pictures, saved in the moment without switching windows.
- **Catching a thought** the second you have it, before it's gone.
- **Turning what you collected into something:** a tidy list, a document, or
  rows for a spreadsheet.
- **Pasting a prepared list back in order**, one `Ctrl+V` at a time, for filling
  in forms or entering data.

## How it feels

- You're watching a video full screen and an idea hits. Hold `Ctrl+X`, type
  it, press `Ctrl+Enter`. The video never stopped.
- You're reading an article. Select a paragraph and hold `Ctrl+C`. It's saved,
  along with which page it came from.
- At the end of the day, hold `Ctrl+V`. Everything you collected is ready to
  paste as notes or as table rows.

Tapping these shortcuts still does what it always did. Zetl steps in when you
hold, or when you've switched on something that asks it to, like Replay.

## The shortcuts

| Hold | What happens |
| --- | --- |
| `Ctrl+C` | Save what you copied, with a chance to edit it first |
| `Ctrl+X` | Write a quick note |
| `Ctrl+A` | Select everything and save it |
| `Ctrl+V` | Compile: turn your notes into text to paste |
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

- **Zetl** is the small part that runs quietly in the notification area by the
  clock. It's the capturing: quick, out of the way, gone as soon as you're done.
- **Kastn** is the workbench you open when you want to sit down with what you
  collected: browse it, organize it, edit it, link notes together, and export
  to Markdown, HTML, or PDF. Open it from Zetl's tray menu.

## Install

1. Download the latest `Zetl-…-win-x64.zip` from the
   [Releases page](https://github.com/vecst/zetl-kastn/releases/latest).
2. Extract the whole zip into a folder and keep its files together.
3. Run `Zetl.exe`. Its icon appears in the notification area by the clock.

There's nothing else to install. Zetl is new and not yet code-signed, so
Windows may show a SmartScreen warning the first time: choose **More info →
Run anyway**.

Zetl runs on 64-bit Windows. It can't reach apps running as administrator
unless Zetl is run as administrator too.

> Zetl is in release-candidate testing. Expect some rough edges, and please
> [report what you find](https://github.com/vecst/zetl-kastn/issues).

## Your notes stay on your computer

Zetl has no account and no cloud. Everything you save lives in readable files
under `%AppData%\Zetl` on your own machine. The only time Zetl goes online is
when you copy a link to an image: it downloads that image so it can save the
picture. When you export a project to share, a clean copy leaves out which apps
and windows your notes came from.

## Learn more

- [User guide](docs/guide.md): everything Zetl and Kastn can do, in detail
- [Building from source](docs/building.md): for developers and the curious

Zetl is free software under the [GNU General Public License v3](LICENSE) or
later.
