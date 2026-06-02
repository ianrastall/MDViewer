# MDViewer Windows x64 Release

This release ships `MDViewer.exe`, a self-contained Windows x64 build of MDViewer for reading, cleaning up, converting, and exporting Markdown documents.

## What This Release Does

- Opens `.md` and `.txt` files directly.
- Renders Markdown in a WinUI 3 rich preview with a raw Markdown toggle.
- Builds a heading outline from the current document so you can jump through long files.
- Shows document stats for line count, character count, UTF-8 encoding, and zoom level.
- Saves Markdown back to `.md` or `.txt`.
- Formats Markdown through Pandoc using Pandoc Markdown, ATX headings, and no hard wrapping.
- Reflows heading levels into a cleaner hierarchy and warns when manual tables of contents or anchor links may need review.
- Imports `.docx`, `.html`, and `.epub` into Markdown through Pandoc.
- Exports Markdown through Pandoc to `.docx`, `.html`, `.epub`, `.rtf`, `.odt`, `.tex`, `.typ`, `.rst`, and `.org`.
- Crawls documentation sites into Markdown with a polite single-threaded crawler that respects `robots.txt`, keeps to the same base path, and caps crawls at 250 pages.
- Includes a `Fetch Pandoc` command that downloads the latest Windows x64 Pandoc build when conversion or crawling features need it, without changing the user's `PATH`.

## Requirements

- Windows x64.
- Windows 10 version 1809 or newer.
- Pandoc is only required for import, export, formatting, and crawl features. Native Markdown viewing, raw view, reflow, save, zoom, and heading navigation work without Pandoc.

## Pandoc Setup

For conversion, formatting, and crawling, use one of these options:

- Click `Fetch Pandoc` inside MDViewer. The app downloads the latest Windows x64 Pandoc release and places `pandoc.exe` beside `MDViewer.exe`.
- Place `pandoc.exe` beside `MDViewer.exe` yourself.
- Place `pandoc.exe` in an `Assets` folder beside `MDViewer.exe` yourself. This is also useful when running from a cloned repo.
- Install Pandoc yourself and make sure `pandoc.exe` is available on `PATH`.

MDViewer checks for Pandoc in this order: `pandoc.exe` beside `MDViewer.exe`, app-local `Assets\pandoc.exe`, then `PATH`. `Fetch Pandoc` does not modify user or machine environment variables.

`Fetch Pandoc` needs the folder containing `MDViewer.exe` to be writable. If the app is in a protected folder, move it to a user-writable folder or install Pandoc on `PATH`.

## Running The App

Download `MDViewer.exe` and run it directly. This is not an installer or MSIX package.

You can also pass a document path to the executable, for example `MDViewer.exe README.md`, or use Windows `Open with` to launch MDViewer for a Markdown file.

Because this build is distributed as a direct executable, Windows SmartScreen may warn that the app is from an unknown publisher. That is expected for an unsigned release binary.

## Current Limits

- This release is Windows x64 only.
- JavaScript-heavy documentation sites may crawl as shell pages if their content is rendered only in the browser.
- Crawling is intentionally conservative: single-threaded, delayed between requests, same-base-path by default, and capped to avoid hammering sites.
- Imported and crawled Markdown may still need review when source documents contain complex tables, unusual HTML, or hand-written heading anchors.
