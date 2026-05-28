# MDViewer

MDViewer is a Windows Markdown viewer and conversion tool built with WinUI 3. It opens Markdown files directly, renders a readable preview, builds a heading outline for navigation, and uses Pandoc when you need to import, export, or crawl content into Markdown.

## Features

- Open `.md` and `.txt` files directly.
- Render Markdown in a native WinUI 3 preview.
- Toggle between rich preview and raw Markdown.
- Navigate long documents with an automatically generated heading tree.
- View line count, character count, UTF-8 encoding, and zoom level.
- Save Markdown as `.md` or `.txt`.
- Format Markdown with normalized spacing, ATX headings, and stable list markers.
- Reflow heading levels into a cleaner hierarchy.
- Import `.docx`, `.html`, and `.epub` into Markdown through Pandoc.
- Export Markdown through Pandoc to `.docx`, `.html`, `.epub`, `.rtf`, `.odt`, `.tex`, `.typ`, `.rst`, and `.org`.
- Crawl documentation sites into Markdown with a conservative single-threaded crawler that respects `robots.txt`.

## Download

Download the latest Windows x64 executable from the GitHub Releases page.

The release is a portable `MDViewer.exe`, not an installer or MSIX package. You can run it directly, pass a file path such as `MDViewer.exe README.md`, or use Windows `Open with` for Markdown files.

Because the executable is unsigned, Windows SmartScreen may show an unknown-publisher warning.

## Pandoc

Pandoc is optional for basic Markdown viewing. You only need Pandoc for import, export, and crawl features.

MDViewer looks for Pandoc in this order:

1. `pandoc.exe` beside `MDViewer.exe`.
2. `Assets\pandoc.exe` beside `MDViewer.exe`.
3. `pandoc.exe` on `PATH`.

Use `Fetch Pandoc` inside MDViewer to download the latest Windows x64 Pandoc release. The app places `pandoc.exe` beside `MDViewer.exe` and does not modify user or machine environment variables.

If the folder containing `MDViewer.exe` is not writable, move the app to a writable folder, place `pandoc.exe` beside it manually, or install Pandoc yourself and put it on `PATH`.

## Building From Source

Requirements:

- Windows x64.
- Windows 10 version 1809 or newer.
- .NET 10 SDK.

Build:

```powershell
dotnet build MDViewer.slnx -c Release
```

Publish a portable single-file Windows x64 executable:

```powershell
dotnet publish MDViewer\MDViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o Build
```

The published executable is written to `Build\MDViewer.exe`.

## Notes

- The current release target is Windows x64.
- JavaScript-heavy sites may not crawl cleanly if their content is rendered only in the browser.
- Crawling is intentionally conservative: same-base-path, single-threaded, delayed between requests, and capped at 250 pages.
- Imported and crawled Markdown should be reviewed when source documents contain complex tables, unusual HTML, or hand-written anchor links.

## License

MIT License. See [LICENSE](LICENSE).
