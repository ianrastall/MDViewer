# MDViewer PDF Import Code Dump

- Generated: 2026-06-05 17:19:51 -05:00
- Repository: D:\GitHub\MDViewer
- Branch: main
- Commit: 6d16d5e

## Working Tree Status

```text
 M Build/RELEASE_README.md
 M MDViewer/MDViewer.csproj
 M MDViewer/ViewModels/MainViewModel.cs
 M MDViewer/Views/MainPage.xaml.cs
 M README.md
?? Build/PDF_IMPORT_CODE_DUMP.md
?? MDViewer/Services/PdfImportService.cs
?? scripts/Export-PdfImportCodeDump.ps1
```

## What This Part Of The Program Does

The PDF import path lets MDViewer open a `.pdf` file and turn it into Markdown without asking the user to install Python, Marker, model weights, or any external PDF/OCR command-line tool. It uses a native C# PDF text extraction library first, then falls back to Windows built-in OCR for pages where the embedded PDF text is missing or looks too weak to trust.

The design is intentionally pragmatic: it favors a dependable no-setup import path over a heavyweight ML layout system. It will not perfectly reconstruct every complex table, multi-column page, or scanned layout, but it should produce useful Markdown for common text PDFs and many scanned/image-only PDFs.

## Main Components

- `PdfImportService`: owns PDF-to-Markdown conversion. It extracts embedded text with PdfPig, runs Windows OCR where needed, builds Markdown, and returns diagnostics.
- `PdfImportResult`: carries the Markdown plus quality signals such as page count, OCR page count, missing pages, and warnings.
- `MainViewModel.LoadFileAsync`: routes `.pdf` files to `PdfImportService` and updates the current document.
- `MainPage.PickOpenFilePathAsync`: adds `.pdf` to the file picker.
- `MDViewer.csproj`: pins the app to Windows x64 and references `PdfPig`.

## End-To-End Flow

1. The user chooses a `.pdf` file from the Open picker.
2. `MainViewModel.LoadFileAsync` detects the `.pdf` extension and creates a `Progress<string>` reporter that writes status messages to the status bar.
3. `PdfImportService.ImportAsMarkdownAsync` validates the path and wraps unexpected failures in `PdfImportException` so the UI gets a cleaner import error.
4. Embedded text extraction runs on a background thread through PdfPig and `ContentOrderTextExtractor`.
5. Each page is scored with `ShouldRunOcr`. Blank, very short, or suspiciously weak text pages are sent through Windows OCR.
6. OCR renders each target page with `Windows.Data.Pdf.PdfPage.RenderToStreamAsync`, decodes it with `BitmapDecoder`, and recognizes text with `Windows.Media.Ocr.OcrEngine`.
7. OCR failures are isolated per page. One bad page records a warning and the import continues.
8. Pages without readable text after extraction/OCR are reported as missing. If every page is unreadable, the import fails instead of returning a fake successful document with only a title.
9. The service normalizes text into Markdown while preserving more structure than a simple line-flattening pass: bullets, indentation, table-like pipes, repeated spacing, and page markers.
10. The ViewModel displays the Markdown and reports whether OCR or unreadable pages were involved.

## Important Implementation Details

- OCR is triggered by a weak-text heuristic, not only empty pages. This catches scanned PDFs with tiny junk text layers.
- Page arrays are pre-populated defensively so downstream code does not dereference null page slots.
- Cancellation is checked during embedded extraction and before/inside expensive OCR work.
- OCR render dimensions are capped against `OcrEngine.MaxImageDimension` so large PDF pages do not exceed Windows OCR limits.
- `Windows.Data.Pdf.PdfDocument` is not wrapped in `using` because this projectâ€™s WinRT projection does not expose it as `IDisposable`; `PdfPage`, `SoftwareBitmap`, and render streams are disposed where supported.
- The service accepts an optional OCR language tag internally, then falls back to the user profile languages when no tag is supplied or the requested language is unavailable.

## Known Limits

- PDF layout recovery is heuristic. Complex tables, multi-column reading order, footnotes, forms, and code blocks may need manual cleanup.
- Windows OCR quality depends on installed Windows OCR language packs and the source image quality.
- PdfPig text order can be imperfect for PDFs with unusual internal structure.
- The current UI exposes import diagnostics in the status bar, not a detailed per-page report panel.

## Validation Commands

```powershell
dotnet build MDViewer.slnx -c Debug
dotnet build MDViewer.slnx -c Release
```

## Files Included

- MDViewer/Services/PdfImportService.cs
- MDViewer/ViewModels/MainViewModel.cs
- MDViewer/Views/MainPage.xaml.cs
- MDViewer/MDViewer.csproj
- README.md
- Build/RELEASE_README.md

# Source Code

## MDViewer/Services/PdfImportService.cs

``csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using PigPdfDocument = UglyToad.PdfPig.PdfDocument;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;

namespace MDViewer.Services;

public sealed class PdfImportException : Exception
{
    public PdfImportException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed record PdfImportResult(
    string Markdown,
    int PageCount,
    int EmbeddedTextPageCount,
    int OcrPageCount,
    int MissingPageCount,
    IReadOnlyList<int> OcrPages,
    IReadOnlyList<int> MissingPages,
    IReadOnlyList<string> Warnings);

public sealed class PdfImportService
{
    private const double OcrTargetScale = 3.0;
    private const double MinimumOcrScale = 0.05;
    private const int ReadableLetterThreshold = 10;
    private const int ReadableWordThreshold = 3;
    private const int OcrCandidateLetterThreshold = 40;
    private const int OcrCandidateWordThreshold = 8;

    private static readonly Regex HyphenatedLineBreakRegex = new(
        @"(?<prefix>\p{L}{3,})-\n(?<suffix>\p{Ll}{3,})",
        RegexOptions.Compiled);

    private static readonly Regex ExcessBlankLineRegex = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex InlineWhitespaceRegex = new(@"[ \t]+", RegexOptions.Compiled);
    private static readonly Regex WordRegex = new(@"\p{L}{2,}", RegexOptions.Compiled);
    private static readonly Regex RepeatedWhitespaceRegex = new(@"[ \t]{3,}", RegexOptions.Compiled);
    private static readonly Regex LeadingIndentRegex = new(@"^\s{2,}", RegexOptions.Compiled);

    private static readonly Regex BulletRegex = new(
        @"^\s*(?<bullet>[*+\-â€¢â€£â—¦â–ªâ€“â€”]|\d+[.)])\s+(?<text>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex NumberedBulletRegex = new(@"^\d+[.)]$", RegexOptions.Compiled);

    public async Task<PdfImportResult> ImportAsMarkdownAsync(
        string pdfPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        string? ocrLanguageTag = null)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
        {
            throw new ArgumentException("A PDF path is required.", nameof(pdfPath));
        }

        if (!File.Exists(pdfPath))
        {
            throw new FileNotFoundException($"Input PDF not found at {pdfPath}", pdfPath);
        }

        try
        {
            return await ImportAsMarkdownCoreAsync(pdfPath, progress, cancellationToken, ocrLanguageTag).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PdfImportException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PdfImportException(
                $"PDF import failed for '{Path.GetFileName(pdfPath)}'. {ex.Message}",
                ex);
        }
    }

    private static async Task<PdfImportResult> ImportAsMarkdownCoreAsync(
        string pdfPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        string? ocrLanguageTag)
    {
        progress?.Report("Reading PDF text...");

        PdfPageText[] pages = await Task
            .Run(() => ExtractEmbeddedText(pdfPath, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        if (pages.Length == 0)
        {
            throw new PdfImportException("The PDF does not contain any pages.");
        }

        var warnings = new List<string>();
        IReadOnlyList<int> ocrPages = Array.Empty<int>();

        if (pages.Any(ShouldRunOcr))
        {
            progress?.Report("Running Windows OCR for pages with little or no extractable text...");
            ocrPages = await FillWeakPagesWithWindowsOcrAsync(
                pdfPath,
                pages,
                warnings,
                progress,
                cancellationToken,
                ocrLanguageTag).ConfigureAwait(false);
        }

        int[] missingPages = pages
            .Where(static page => !HasReadableText(page.Text))
            .Select(static page => page.Number)
            .ToArray();

        if (missingPages.Length == pages.Length)
        {
            throw new PdfImportException("No readable text could be extracted from this PDF.");
        }

        if (missingPages.Length > 0)
        {
            warnings.Add($"No readable text was extracted from page(s): {string.Join(", ", missingPages)}.");
        }

        string markdown = await Task
            .Run(() => BuildMarkdown(pdfPath, pages), cancellationToken)
            .ConfigureAwait(false);

        int embeddedTextPageCount = pages.Count(static page => !page.IsOcr && HasReadableText(page.Text));

        return new PdfImportResult(
            markdown,
            pages.Length,
            embeddedTextPageCount,
            ocrPages.Count,
            missingPages.Length,
            ocrPages,
            missingPages,
            warnings);
    }

    private static PdfPageText[] ExtractEmbeddedText(
        string pdfPath,
        CancellationToken cancellationToken)
    {
        using PigPdfDocument document = PigPdfDocument.Open(pdfPath);

        var pages = Enumerable
            .Range(1, document.NumberOfPages)
            .Select(static pageNumber => new PdfPageText(pageNumber, string.Empty, IsOcr: false))
            .ToArray();

        foreach (Page page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (page.Number < 1 || page.Number > pages.Length)
            {
                continue;
            }

            string text = ContentOrderTextExtractor.GetText(page) ?? string.Empty;
            pages[page.Number - 1] = new PdfPageText(page.Number, text, IsOcr: false);
        }

        return pages;
    }

    private static async Task<IReadOnlyList<int>> FillWeakPagesWithWindowsOcrAsync(
        string pdfPath,
        PdfPageText[] pages,
        List<string> warnings,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        string? ocrLanguageTag)
    {
        if (!OperatingSystem.IsWindows())
        {
            warnings.Add("Windows OCR is unavailable on this platform.");
            progress?.Report("Windows OCR is unavailable on this platform.");
            return Array.Empty<int>();
        }

        OcrEngine? ocrEngine = CreateOcrEngine(ocrLanguageTag, warnings);

        if (ocrEngine is null)
        {
            warnings.Add("Windows OCR is unavailable for the current user languages.");
            progress?.Report("Windows OCR is unavailable for the current user languages.");
            return Array.Empty<int>();
        }

        StorageFile file = await StorageFile
            .GetFileFromPathAsync(pdfPath)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        WinPdfDocument pdfDocument = await WinPdfDocument
            .LoadFromFileAsync(file)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (pdfDocument.PageCount != (uint)pages.Length)
        {
            warnings.Add(
                $"PDF page count mismatch. Text extraction saw {pages.Length:N0}; Windows rendering saw {pdfDocument.PageCount:N0}. OCR may be incomplete.");
        }

        uint pageCount = Math.Min(pdfDocument.PageCount, (uint)pages.Length);
        var ocrPages = new List<int>();

        for (uint pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PdfPageText pageText = pages[pageIndex];

            if (!ShouldRunOcr(pageText))
            {
                continue;
            }

            int pageNumber = (int)pageIndex + 1;
            progress?.Report($"Running OCR on page {pageNumber:N0} of {pageCount:N0}...");

            try
            {
                using PdfPage page = pdfDocument.GetPage(pageIndex);
                string ocrText = await RecognizePdfPageAsync(page, ocrEngine, cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(ocrText))
                {
                    continue;
                }

                pages[pageIndex] = pageText with { Text = ocrText, IsOcr = true };
                ocrPages.Add(pageNumber);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                string warning = $"OCR failed on page {pageNumber:N0}: {ex.Message}";
                warnings.Add(warning);
                progress?.Report(warning);
            }
        }

        return ocrPages;
    }

    private static OcrEngine? CreateOcrEngine(string? ocrLanguageTag, List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(ocrLanguageTag))
        {
            try
            {
                var language = new Language(ocrLanguageTag);

                if (OcrEngine.IsLanguageSupported(language))
                {
                    return OcrEngine.TryCreateFromLanguage(language);
                }

                warnings.Add($"Windows OCR language '{ocrLanguageTag}' is not installed or supported. Falling back to user profile languages.");
            }
            catch (Exception ex)
            {
                warnings.Add($"Windows OCR language '{ocrLanguageTag}' could not be used: {ex.Message}. Falling back to user profile languages.");
            }
        }

        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static async Task<string> RecognizePdfPageAsync(
        PdfPage page,
        OcrEngine ocrEngine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new InMemoryRandomAccessStream();
        var renderOptions = CreateOcrRenderOptions(page);

        await page
            .RenderToStreamAsync(stream, renderOptions)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        stream.Seek(0);

        BitmapDecoder decoder = await BitmapDecoder
            .CreateAsync(stream)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        using SoftwareBitmap bitmap = await decoder
            .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        OcrResult? result = await ocrEngine
            .RecognizeAsync(bitmap)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            result.Lines.Select(static line => line.Text).Where(static line => !string.IsNullOrWhiteSpace(line)));
    }

    private static PdfPageRenderOptions CreateOcrRenderOptions(PdfPage page)
    {
        double width = page.Size.Width;
        double height = page.Size.Height;

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("PDF page has invalid dimensions.");
        }

        double scale = OcrTargetScale;
        double maxDimension = OcrEngine.MaxImageDimension;

        if (maxDimension > 0)
        {
            double maxAllowedScale = Math.Min(maxDimension / width, maxDimension / height);
            scale = Math.Min(scale, maxAllowedScale);
        }
        else
        {
            scale = Math.Max(MinimumOcrScale, scale);
        }

        scale = Math.Max(double.Epsilon, scale);

        uint destinationWidth = Math.Max(1u, (uint)Math.Round(width * scale));
        uint destinationHeight = Math.Max(1u, (uint)Math.Round(height * scale));

        if (maxDimension > 0)
        {
            uint maxPixels = Math.Max(1u, (uint)Math.Floor(maxDimension));
            destinationWidth = Math.Min(destinationWidth, maxPixels);
            destinationHeight = Math.Min(destinationHeight, maxPixels);
        }

        return new PdfPageRenderOptions
        {
            DestinationWidth = destinationWidth,
            DestinationHeight = destinationHeight,
            BitmapEncoderId = BitmapEncoder.PngEncoderId
        };
    }

    private static string BuildMarkdown(string pdfPath, PdfPageText[] pages)
    {
        var markdown = new StringBuilder();
        string title = Path.GetFileNameWithoutExtension(pdfPath);

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Untitled";
        }

        markdown.Append("# ");
        markdown.AppendLine(title);
        markdown.AppendLine();

        foreach (PdfPageText page in pages)
        {
            if (!HasReadableText(page.Text))
            {
                continue;
            }

            string pageMarkdown = NormalizePageText(page.Text);

            if (string.IsNullOrWhiteSpace(pageMarkdown))
            {
                continue;
            }

            markdown.AppendLine($"<!-- Page {page.Number} -->");
            markdown.AppendLine();
            markdown.AppendLine(pageMarkdown);
            markdown.AppendLine();
        }

        return markdown.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string NormalizePageText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = HyphenatedLineBreakRegex.Replace(normalized, "${prefix}${suffix}");

        string[] lines = normalized.Split('\n');
        var markdown = new StringBuilder();
        var paragraph = new StringBuilder();

        foreach (string rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                FlushParagraph(markdown, paragraph);
                continue;
            }

            Match bulletMatch = BulletRegex.Match(rawLine);

            if (bulletMatch.Success)
            {
                FlushParagraph(markdown, paragraph);
                AppendBulletLine(markdown, rawLine, bulletMatch);
                continue;
            }

            string trimmedLine = rawLine.Trim();
            string compactLine = InlineWhitespaceRegex.Replace(trimmedLine, " ");

            if (ShouldPreserveLineBreak(rawLine))
            {
                FlushParagraph(markdown, paragraph);
                markdown.AppendLine(NormalizePreservedLine(rawLine));
                continue;
            }

            if (paragraph.Length > 0)
            {
                paragraph.Append(' ');
            }

            paragraph.Append(compactLine);
        }

        FlushParagraph(markdown, paragraph);

        return ExcessBlankLineRegex.Replace(markdown.ToString().Trim(), "\n\n");
    }

    private static bool HasReadableText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        int letters = text.Count(char.IsLetter);

        if (letters >= ReadableLetterThreshold)
        {
            return true;
        }

        return WordRegex.Matches(text).Count >= ReadableWordThreshold;
    }

    private static bool ShouldRunOcr(PdfPageText page)
    {
        string text = page.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        int letters = text.Count(char.IsLetter);
        int words = WordRegex.Matches(text).Count;

        return letters < OcrCandidateLetterThreshold || words < OcrCandidateWordThreshold;
    }

    private static bool ShouldPreserveLineBreak(string rawLine)
    {
        if (rawLine.Contains('|', StringComparison.Ordinal))
        {
            return true;
        }

        if (LeadingIndentRegex.IsMatch(rawLine))
        {
            return true;
        }

        return RepeatedWhitespaceRegex.IsMatch(rawLine);
    }

    private static string NormalizePreservedLine(string rawLine)
    {
        return rawLine.Replace('\t', ' ').TrimEnd();
    }

    private static void AppendBulletLine(StringBuilder markdown, string rawLine, Match bulletMatch)
    {
        string bullet = bulletMatch.Groups["bullet"].Value;
        string itemText = InlineWhitespaceRegex.Replace(bulletMatch.Groups["text"].Value.Trim(), " ");
        string indent = GetMarkdownListIndent(rawLine);

        if (NumberedBulletRegex.IsMatch(bullet))
        {
            markdown.Append(indent);
            markdown.Append(bullet.TrimEnd(')'));
            markdown.Append(' ');
            markdown.AppendLine(itemText);
            return;
        }

        markdown.Append(indent);
        markdown.Append("- ");
        markdown.AppendLine(itemText);
    }

    private static string GetMarkdownListIndent(string rawLine)
    {
        int leadingWhitespace = rawLine.TakeWhile(char.IsWhiteSpace).Count();
        int normalizedIndent = Math.Min(12, leadingWhitespace / 2 * 2);
        return normalizedIndent == 0 ? string.Empty : new string(' ', normalizedIndent);
    }

    private static void FlushParagraph(StringBuilder markdown, StringBuilder paragraph)
    {
        if (paragraph.Length == 0)
        {
            return;
        }

        markdown.AppendLine(paragraph.ToString());
        markdown.AppendLine();
        paragraph.Clear();
    }

    private sealed record PdfPageText(int Number, string Text, bool IsOcr);
}

``

## MDViewer/ViewModels/MainViewModel.cs

``csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MDViewer.Models;
using MDViewer.Services;
using Microsoft.UI.Xaml;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MDViewer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int MinimumZoomPercent = 50;
    private const int MaximumZoomPercent = 200;
    private const int ZoomStepPercent = 10;
    private const double BaseContentFontSize = 14;
    private const double BaseHeader1FontSize = 32;
    private const double BaseHeader2FontSize = 24;
    private const double BaseHeader3FontSize = 18;
    private const double BaseHeader4FontSize = 16;
    private static readonly Encoding MarkdownEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex AtxHeadingRegex = new(@"^\s{0,3}(#{1,6})(?:[ \t]+|$)(.*)$", RegexOptions.Compiled);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRenderRichMarkdown))]
    [NotifyPropertyChangedFor(nameof(RenderedMarkdown))]
    [NotifyPropertyChangedFor(nameof(RichViewVisibility))]
    [NotifyPropertyChangedFor(nameof(RawViewVisibility))]
    [NotifyPropertyChangedFor(nameof(CharacterCount))]
    [NotifyPropertyChangedFor(nameof(LineCount))]
    [NotifyPropertyChangedFor(nameof(DocumentStatisticsText))]
    private DocumentContext _currentDocument;

    [ObservableProperty]
    private ObservableCollection<HeadingNode> _headingNodes = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenderedMarkdown))]
    [NotifyPropertyChangedFor(nameof(RichViewVisibility))]
    [NotifyPropertyChangedFor(nameof(RawViewVisibility))]
    private bool _isRawView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrawlOverlayVisibility))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanFetchPandoc))]
    [NotifyCanExecuteChangedFor(nameof(FetchPandocCommand))]
    private bool _isCrawling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFetchPandoc))]
    [NotifyCanExecuteChangedFor(nameof(FetchPandocCommand))]
    private bool _isFetchingPandoc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    private string _crawlStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    private string _documentStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    [NotifyPropertyChangedFor(nameof(DocumentStatisticsText))]
    [NotifyPropertyChangedFor(nameof(ContentFontSize))]
    [NotifyPropertyChangedFor(nameof(Header1FontSize))]
    [NotifyPropertyChangedFor(nameof(Header2FontSize))]
    [NotifyPropertyChangedFor(nameof(Header3FontSize))]
    [NotifyPropertyChangedFor(nameof(Header4FontSize))]
    private int _zoomPercent = 100;

    private readonly MarkdownFormatterService _markdownFormatterService;
    private readonly PandocConversionService _pandocService;
    private readonly PandocDownloadService _pandocDownloadService;
    private readonly PdfImportService _pdfImportService;
    private readonly CrawlUrlService _crawlUrlService;
    private Func<Task<string?>> _pickOpenFileAsync = static () => Task.FromResult<string?>(null);
    private Func<Task<string?>> _pickMarkdownSaveFileAsync = static () => Task.FromResult<string?>(null);
    private Func<Task<string?>> _pickExportFileAsync = static () => Task.FromResult<string?>(null);
    private Func<Task<string?>> _promptForUrlAsync = static () => Task.FromResult<string?>(null);
    private Action _exitApplication = static () => { };
    private CancellationTokenSource? _crawlCancellation;

    public bool CanRenderRichMarkdown => true;

    public string RenderedMarkdown => !IsRawView
        ? CurrentDocument.RawMarkdown
        : string.Empty;

    public Visibility RichViewVisibility => !IsRawView ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RawViewVisibility => IsRawView ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CrawlOverlayVisibility => IsCrawling ? Visibility.Visible : Visibility.Collapsed;

    public bool CanFetchPandoc => !IsCrawling && !IsFetchingPandoc;

    public string StatusMessage => IsCrawling && !string.IsNullOrWhiteSpace(CrawlStatus)
        ? CrawlStatus
        : DocumentStatus;

    public int CharacterCount => CurrentDocument.RawMarkdown.Length;

    public int LineCount => CountLines(CurrentDocument.RawMarkdown);

    public string EncodingLabel => "UTF-8";

    public string ZoomText => $"{ZoomPercent}%";

    public string DocumentStatisticsText =>
        $"{LineCount:N0} lines  |  {CharacterCount:N0} chars  |  {EncodingLabel}  |  Zoom {ZoomText}";

    public double ContentFontSize => ScaleFontSize(BaseContentFontSize);

    public double Header1FontSize => ScaleFontSize(BaseHeader1FontSize);

    public double Header2FontSize => ScaleFontSize(BaseHeader2FontSize);

    public double Header3FontSize => ScaleFontSize(BaseHeader3FontSize);

    public double Header4FontSize => ScaleFontSize(BaseHeader4FontSize);

    public MainViewModel()
    {
        _markdownFormatterService = new MarkdownFormatterService();
        _pandocService = new PandocConversionService();
        _pandocDownloadService = new PandocDownloadService();
        _pdfImportService = new PdfImportService();
        _crawlUrlService = new CrawlUrlService();
        _currentDocument = new DocumentContext
        {
            Origin = DocumentOrigin.NativeMarkdown,
            RawMarkdown = "# Welcome to MDViewer\n\nThis is a native WinUI 3 Markdown rendering surface. Use the Open button to load a file."
        };
        ParseHeadings(_currentDocument.RawMarkdown);
        DocumentStatus = "Ready.";
    }

    public void ConfigureFilePickers(
        Func<Task<string?>> pickOpenFileAsync,
        Func<Task<string?>> pickMarkdownSaveFileAsync,
        Func<Task<string?>> pickExportFileAsync,
        Func<Task<string?>> promptForUrlAsync,
        Action exitApplication)
    {
        _pickOpenFileAsync = pickOpenFileAsync ?? throw new ArgumentNullException(nameof(pickOpenFileAsync));
        _pickMarkdownSaveFileAsync = pickMarkdownSaveFileAsync ?? throw new ArgumentNullException(nameof(pickMarkdownSaveFileAsync));
        _pickExportFileAsync = pickExportFileAsync ?? throw new ArgumentNullException(nameof(pickExportFileAsync));
        _promptForUrlAsync = promptForUrlAsync ?? throw new ArgumentNullException(nameof(promptForUrlAsync));
        _exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        try
        {
            string? filePath = await _pickOpenFileAsync();

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            await LoadFileAsync(filePath);
        }
        catch (Exception ex)
        {
            SetCommandFailureStatus("Open", ex);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        DocumentContext document = CurrentDocument;

        if (document.Origin == DocumentOrigin.NativeMarkdown &&
            !string.IsNullOrWhiteSpace(document.SourceFilePath) &&
            File.Exists(document.SourceFilePath))
        {
            await File.WriteAllTextAsync(document.SourceFilePath, document.RawMarkdown, MarkdownEncoding);
            ParseHeadings(document.RawMarkdown);
            DocumentStatus = $"Saved {Path.GetFileName(document.SourceFilePath)}.";
            return;
        }

        string? filePath = await _pickMarkdownSaveFileAsync();

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        await File.WriteAllTextAsync(filePath, document.RawMarkdown, MarkdownEncoding);

        SetCurrentDocument(new DocumentContext
        {
            SourceFilePath = filePath,
            Origin = DocumentOrigin.NativeMarkdown,
            RawMarkdown = document.RawMarkdown
        });

        DocumentStatus = $"Saved {Path.GetFileName(filePath)}.";
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        string? filePath = await _pickExportFileAsync();

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        string targetExtension = Path.GetExtension(filePath);
        try
        {
            await _pandocService.ExportFromMarkdownAsync(CurrentDocument.RawMarkdown, filePath, targetExtension);
            DocumentStatus = $"Exported {Path.GetFileName(filePath)}.";
        }
        catch (Exception ex)
        {
            SetCommandFailureStatus("Export", ex);
        }
    }

    [RelayCommand]
    private async Task FormatAsync()
    {
        try
        {
            string markdown = await _pandocService.FormatMarkdownAsync(CurrentDocument.RawMarkdown);
            SetCurrentMarkdown(markdown, preserveViewMode: true);
            DocumentStatus = "Formatted as Pandoc Markdown.";
        }
        catch (Exception ex)
        {
            SetCommandFailureStatus("Format", ex);
        }
    }

    [RelayCommand]
    private void Reflow()
    {
        try
        {
            MarkdownReflowResult result = _markdownFormatterService.ReflowHeadings(CurrentDocument.RawMarkdown);

            SetCurrentMarkdown(result.Markdown, preserveViewMode: true);

            DocumentStatus = result.ChangedHeadingCount == 0
                ? BuildReflowStatus("Reflow made no heading-level changes.", result.Warnings)
                : BuildReflowStatus($"Reflow changed {result.ChangedHeadingCount} heading level(s).", result.Warnings);
        }
        catch (Exception ex)
        {
            SetCommandFailureStatus("Reflow", ex);
        }
    }

    [RelayCommand]
    private void CloseDocument()
    {
        SetCurrentDocument(new DocumentContext
        {
            SourceFilePath = null,
            Origin = DocumentOrigin.NativeMarkdown,
            RawMarkdown = string.Empty
        });

        DocumentStatus = "Closed document.";
    }

    [RelayCommand]
    private void Exit()
    {
        _exitApplication();
    }

    [RelayCommand(CanExecute = nameof(CanFetchPandoc))]
    private async Task FetchPandocAsync()
    {
        IsFetchingPandoc = true;

        try
        {
            var progress = new Progress<string>(status => DocumentStatus = status);
            PandocDownloadResult result = await _pandocDownloadService.DownloadLatestWindowsPandocAsync(progress);
            DocumentStatus = $"Pandoc {result.Version} installed at {result.PandocPath}.";
        }
        catch (Exception ex)
        {
            DocumentStatus = $"Pandoc fetch failed: {ex.Message}";
        }
        finally
        {
            IsFetchingPandoc = false;
        }
    }

    [RelayCommand]
    private async Task CrawlAsync()
    {
        if (IsCrawling)
        {
            return;
        }

        string? urlText = await _promptForUrlAsync();

        if (string.IsNullOrWhiteSpace(urlText) || !TryParseHttpUrl(urlText.Trim(), out Uri startUrl))
        {
            return;
        }

        IsCrawling = true;
        CrawlStatus = $"Starting {startUrl}";
        _crawlCancellation?.Dispose();
        _crawlCancellation = new CancellationTokenSource();

        try
        {
            var options = new CrawlerOptions
            {
                DefaultDelay = TimeSpan.FromSeconds(2),
                RequestTimeout = TimeSpan.FromSeconds(30),
                MaxPages = 250,
                MaxPageBytes = 5 * 1024 * 1024,
                MaxRetries = 3,
                SameBasePathOnly = true,
                RespectRobotsTxt = true
            };

            var progress = new Progress<string>(status => CrawlStatus = status);
            string markdown = await _crawlUrlService.CrawlAsync(startUrl, options, progress, _crawlCancellation.Token);

            SetCurrentDocument(new DocumentContext
            {
                SourceFilePath = null,
                Origin = DocumentOrigin.CrawledContent,
                RawMarkdown = markdown
            });

            CrawlStatus = "Crawl complete.";
            DocumentStatus = "Crawl complete.";
        }
        catch (OperationCanceledException)
        {
            CrawlStatus = "Crawl cancelled.";
            DocumentStatus = CrawlStatus;
        }
        catch (Exception ex)
        {
            CrawlStatus = $"Crawl failed: {ex.Message}";
            DocumentStatus = CrawlStatus;
        }
        finally
        {
            IsCrawling = false;
            _crawlCancellation?.Dispose();
            _crawlCancellation = null;
        }
    }

    [RelayCommand]
    private void CancelCrawl()
    {
        _crawlCancellation?.Cancel();
        CrawlStatus = "Cancelling...";
    }

    [RelayCommand]
    private void ZoomIn()
    {
        ZoomPercent = Math.Min(MaximumZoomPercent, ZoomPercent + ZoomStepPercent);
        DocumentStatus = $"Zoom {ZoomPercent}%.";
    }

    [RelayCommand]
    private void ZoomOut()
    {
        ZoomPercent = Math.Max(MinimumZoomPercent, ZoomPercent - ZoomStepPercent);
        DocumentStatus = $"Zoom {ZoomPercent}%.";
    }

    [RelayCommand]
    private void ResetZoom()
    {
        ZoomPercent = 100;
        DocumentStatus = "Zoom reset.";
    }

    public async Task LoadFileAsync(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (extension == ".md" || extension == ".txt")
        {
            string rawMarkdown = await File.ReadAllTextAsync(filePath, MarkdownEncoding);

            SetCurrentDocument(new DocumentContext
            {
                SourceFilePath = filePath,
                Origin = DocumentOrigin.NativeMarkdown,
                RawMarkdown = rawMarkdown
            });

            DocumentStatus = $"Opened {Path.GetFileName(filePath)}.";
            return;
        }

        if (extension == ".docx" || extension == ".html" || extension == ".epub")
        {
            string rawMarkdown = await _pandocService.ImportAsMarkdownAsync(filePath);

            SetCurrentDocument(new DocumentContext
            {
                SourceFilePath = null,
                Origin = DocumentOrigin.ImportedForeign,
                RawMarkdown = rawMarkdown
            });

            DocumentStatus = $"Imported {Path.GetFileName(filePath)}.";
            return;
        }

        if (extension == ".pdf")
        {
            var progress = new Progress<string>(status => DocumentStatus = status);
            PdfImportResult result = await _pdfImportService.ImportAsMarkdownAsync(filePath, progress);

            SetCurrentDocument(new DocumentContext
            {
                SourceFilePath = filePath,
                Origin = DocumentOrigin.ImportedForeign,
                RawMarkdown = result.Markdown
            });

            DocumentStatus = BuildPdfImportStatus(Path.GetFileName(filePath), result);
            return;
        }

        throw new NotSupportedException($"Unsupported file type: {extension}");
    }

    public void ShowRawMarkdownFallback(string message)
    {
        IsRawView = true;
        DocumentStatus = message;
    }

    public void ReportOpenFailure(Exception exception)
    {
        SetCommandFailureStatus("Open", exception);
    }

    private void SetCurrentMarkdown(string markdown, bool preserveViewMode)
    {
        DocumentContext document = CurrentDocument;

        SetCurrentDocument(
            new DocumentContext
            {
                SourceFilePath = document.SourceFilePath,
                Origin = document.Origin,
                RawMarkdown = markdown
            },
            preserveViewMode);
    }

    private void SetCurrentDocument(DocumentContext document, bool preserveViewMode = false)
    {
        bool wasRawView = IsRawView;

        CurrentDocument = document;
        IsRawView = preserveViewMode && wasRawView;
        ParseHeadings(document.RawMarkdown);
    }

    private static string BuildReflowStatus(string summary, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return summary;
        }

        return warnings.Count == 1
            ? $"{summary} {warnings[0]}"
            : $"{summary} {warnings[0]} (+{warnings.Count - 1} more)";
    }

    private static string BuildPdfImportStatus(string fileName, PdfImportResult result)
    {
        var parts = new List<string> { $"Imported {fileName}" };

        if (result.OcrPageCount > 0)
        {
            parts.Add($"OCR on {result.OcrPageCount:N0} page(s)");
        }

        if (result.MissingPageCount > 0)
        {
            parts.Add($"{result.MissingPageCount:N0} unreadable page(s)");
        }
        else if (result.Warnings.Count > 0)
        {
            parts.Add($"{result.Warnings.Count:N0} warning(s)");
        }

        return string.Join("; ", parts) + ".";
    }

    private void SetCommandFailureStatus(string action, Exception exception)
    {
        global::MDViewer.App.LogHandledException($"{action} failed", exception);
        DocumentStatus = $"{action} failed: {exception.Message}";
    }

    private double ScaleFontSize(double baseSize)
    {
        return baseSize * ZoomPercent / 100.0;
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 1;
        }

        int count = 1;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                count++;

                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (text[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private void ParseHeadings(string markdown)
    {
        var roots = new ObservableCollection<HeadingNode>();
        var stack = new Stack<HeadingNode>();
        var renderOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        string? fence = null;

        foreach ((string line, int lineNumber, int characterOffset) in EnumerateLines(markdown))
        {
            if (TryUpdateFence(line, ref fence))
            {
                continue;
            }

            if (fence is not null)
            {
                continue;
            }

            Match match = AtxHeadingRegex.Match(line);

            if (!match.Success)
            {
                continue;
            }

            int level = match.Groups[1].Value.Length;
            string title = Regex.Replace(match.Groups[2].Value.Trim(), @"[ \t]+#+[ \t]*$", "").Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            string renderKey = $"{level}\u001F{title}";
            renderOccurrences.TryGetValue(renderKey, out int renderOccurrence);
            renderOccurrences[renderKey] = renderOccurrence + 1;

            var node = new HeadingNode
            {
                Title = title,
                Level = level,
                LineNumber = lineNumber,
                CharacterOffset = characterOffset,
                RenderOccurrence = renderOccurrence
            };

            while (stack.Count > 0 && stack.Peek().Level >= level)
            {
                stack.Pop();
            }

            if (stack.Count == 0)
            {
                roots.Add(node);
            }
            else
            {
                stack.Peek().Children.Add(node);
            }

            stack.Push(node);
        }

        HeadingNodes = roots;
    }

    private static IEnumerable<(string Line, int LineNumber, int CharacterOffset)> EnumerateLines(string text)
    {
        int lineNumber = 1;
        int lineStart = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n')
            {
                continue;
            }

            yield return (text[lineStart..i], lineNumber, lineStart);

            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            lineNumber++;
            lineStart = i + 1;
        }

        yield return (text[lineStart..], lineNumber, lineStart);
    }

    private static bool TryUpdateFence(string line, ref string? fence)
    {
        string trimmed = line.TrimStart();

        if (fence is null)
        {
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                fence = "```";
                return true;
            }

            if (trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = "~~~";
                return true;
            }

            return false;
        }

        if (trimmed.StartsWith(fence, StringComparison.Ordinal))
        {
            fence = null;
            return true;
        }

        return false;
    }

    private static bool TryParseHttpUrl(string text, out Uri uri)
    {
        uri = null!;

        if (text.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        if (parsed.Scheme is not "http" and not "https")
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Host))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}

``

## MDViewer/Views/MainPage.xaml.cs

``csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MDViewer.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using MDViewer.ViewModels;
using WinRT.Interop;

namespace MDViewer.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = new MainViewModel();
        this.InitializeComponent();
        Application.Current.UnhandledException += OnApplicationUnhandledException;
        Unloaded += OnUnloaded;

        ViewModel.ConfigureFilePickers(
            PickOpenFilePathAsync,
            PickMarkdownSaveFilePathAsync,
            PickExportFilePathAsync,
            PromptForUrlAsync,
            ExitApplication);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Application.Current.UnhandledException -= OnApplicationUnhandledException;
        Unloaded -= OnUnloaded;
    }

    private void OnApplicationUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (e.Exception is not LayoutCycleException)
        {
            return;
        }

        e.Handled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.ShowRawMarkdownFallback("Rich Markdown layout failed; showing raw Markdown instead.");
            UpdateRichMarkdownWidth();
        });
    }

    private void RichMarkdownScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateRichMarkdownWidth();
    }

    private void RichMarkdownScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRichMarkdownWidth();
    }

    private void UpdateRichMarkdownWidth()
    {
        Thickness padding = RichMarkdownScrollViewer.Padding;
        double availableWidth = RichMarkdownScrollViewer.ActualWidth - padding.Left - padding.Right;

        if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        RichMarkdownTextBlock.Width = availableWidth;
    }

    private void HeadingTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (TryGetHeadingNode(args.InvokedItem, out HeadingNode? heading) && heading is not null)
        {
            NavigateToHeading(heading);
        }
    }

    private void HeadingTreeView_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (TryGetHeadingNode(sender.SelectedItem, out HeadingNode? heading) && heading is not null)
        {
            NavigateToHeading(heading);
        }
    }

    private void NavigateToHeading(HeadingNode heading)
    {
        if (ViewModel.IsRawView)
        {
            NavigateRawTextToHeading(heading);
            return;
        }

        NavigateRichTextToHeading(heading);
    }

    private void NavigateRawTextToHeading(HeadingNode heading)
    {
        int offset = Math.Clamp(heading.CharacterOffset, 0, RawMarkdownTextBox.Text.Length);

        RawMarkdownTextBox.SelectionStart = offset;
        RawMarkdownTextBox.SelectionLength = 0;
        RawMarkdownTextBox.Focus(FocusState.Programmatic);
    }

    private void NavigateRichTextToHeading(HeadingNode heading)
    {
        RichMarkdownScrollViewer.UpdateLayout();

        if (TryFindRenderedHeading(heading, out FrameworkElement? target) && target is not null)
        {
            ScrollElementIntoView(target);
            return;
        }

        int totalLines = Math.Max(1, CountLines(ViewModel.CurrentDocument.RawMarkdown));
        double position = (double)Math.Max(0, heading.LineNumber - 1) / totalLines;
        double targetOffset = position * RichMarkdownScrollViewer.ScrollableHeight;

        RichMarkdownScrollViewer.ChangeView(
            horizontalOffset: null,
            verticalOffset: targetOffset,
            zoomFactor: null,
            disableAnimation: false);
    }

    private bool TryFindRenderedHeading(HeadingNode heading, out FrameworkElement? target)
    {
        double expectedFontSize = GetExpectedHeadingFontSize(heading.Level);
        var textMatches = new List<FrameworkElement>();
        var headingSizedMatches = new List<FrameworkElement>();

        foreach (FrameworkElement element in EnumerateVisualDescendants(RichMarkdownTextBlock).OfType<FrameworkElement>())
        {
            if (!ElementTextMatchesHeading(element, heading.Title))
            {
                continue;
            }

            textMatches.Add(element);

            if (ElementFontSizeMatchesHeading(element, expectedFontSize))
            {
                headingSizedMatches.Add(element);
            }
        }

        List<FrameworkElement> candidates = headingSizedMatches.Count > 0
            ? headingSizedMatches
            : textMatches;

        if (candidates.Count == 0)
        {
            target = null;
            return false;
        }

        candidates.Sort(CompareByVerticalPosition);
        target = candidates[Math.Min(heading.RenderOccurrence, candidates.Count - 1)];
        return true;
    }

    private void ScrollElementIntoView(FrameworkElement target)
    {
        try
        {
            Windows.Foundation.Point point = target
                .TransformToVisual(RichMarkdownScrollViewer)
                .TransformPoint(new Windows.Foundation.Point(0, 0));

            double targetOffset = Math.Max(0, RichMarkdownScrollViewer.VerticalOffset + point.Y - 16);

            RichMarkdownScrollViewer.ChangeView(
                horizontalOffset: null,
                verticalOffset: targetOffset,
                zoomFactor: null,
                disableAnimation: false);
        }
        catch (InvalidOperationException)
        {
            target.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true,
                VerticalAlignmentRatio = 0
            });
        }
    }

    private double GetExpectedHeadingFontSize(int level)
    {
        return level switch
        {
            1 => ViewModel.Header1FontSize,
            2 => ViewModel.Header2FontSize,
            3 => ViewModel.Header3FontSize,
            4 => ViewModel.Header4FontSize,
            _ => ViewModel.ContentFontSize
        };
    }

    private static bool ElementTextMatchesHeading(FrameworkElement element, string title)
    {
        string? text = element switch
        {
            TextBlock textBlock => GetTextBlockText(textBlock),
            RichTextBlock richTextBlock => GetRichTextBlockText(richTextBlock),
            _ => null
        };

        return string.Equals(text?.Trim(), title, StringComparison.Ordinal);
    }

    private static bool ElementFontSizeMatchesHeading(FrameworkElement element, double expectedFontSize)
    {
        double fontSize = element switch
        {
            TextBlock textBlock => textBlock.FontSize,
            RichTextBlock richTextBlock => richTextBlock.FontSize,
            _ => 0
        };

        return Math.Abs(fontSize - expectedFontSize) < 0.5;
    }

    private int CompareByVerticalPosition(FrameworkElement first, FrameworkElement second)
    {
        return GetVerticalPosition(first).CompareTo(GetVerticalPosition(second));
    }

    private double GetVerticalPosition(FrameworkElement element)
    {
        try
        {
            Windows.Foundation.Point point = element
                .TransformToVisual(RichMarkdownScrollViewer)
                .TransformPoint(new Windows.Foundation.Point(0, 0));

            return RichMarkdownScrollViewer.VerticalOffset + point.Y;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static IEnumerable<DependencyObject> EnumerateVisualDescendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (DependencyObject descendant in EnumerateVisualDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static string GetRichTextBlockText(RichTextBlock richTextBlock)
    {
        return string.Concat(richTextBlock.Blocks.OfType<Paragraph>().Select(GetParagraphText));
    }

    private static string GetTextBlockText(TextBlock textBlock)
    {
        return !string.IsNullOrEmpty(textBlock.Text)
            ? textBlock.Text
            : string.Concat(textBlock.Inlines.Select(GetInlineText));
    }

    private static string GetParagraphText(Paragraph paragraph)
    {
        return string.Concat(paragraph.Inlines.Select(GetInlineText));
    }

    private static string GetInlineText(Inline inline)
    {
        return inline switch
        {
            Run run => run.Text,
            Span span => string.Concat(span.Inlines.Select(GetInlineText)),
            LineBreak => "\n",
            _ => string.Empty
        };
    }

    private static bool TryGetHeadingNode(object? item, out HeadingNode? heading)
    {
        heading = item switch
        {
            HeadingNode node => node,
            TreeViewItem treeViewItem => treeViewItem.Tag as HeadingNode ?? treeViewItem.DataContext as HeadingNode,
            TreeViewNode treeViewNode => treeViewNode.Content as HeadingNode,
            _ => null
        };

        return heading is not null;
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 1;
        }

        int count = 1;

        foreach (char character in text)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private async Task<string?> PickOpenFilePathAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".docx");
        picker.FileTypeFilter.Add(".html");
        picker.FileTypeFilter.Add(".epub");
        picker.FileTypeFilter.Add(".pdf");

        InitializePickerWithMainWindow(picker);

        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task OpenExternalFileAsync(string filePath)
    {
        try
        {
            await ViewModel.LoadFileAsync(filePath);
        }
        catch (Exception ex)
        {
            ViewModel.ReportOpenFailure(ex);
        }
    }

    private async Task<string?> PickMarkdownSaveFilePathAsync()
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = ViewModel.CurrentDocument.DocumentTitle,
            DefaultFileExtension = ".md"
        };

        picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
        picker.FileTypeChoices.Add("Plain Text", new List<string> { ".txt" });

        InitializePickerWithMainWindow(picker);

        Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private async Task<string?> PickExportFilePathAsync()
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = ViewModel.CurrentDocument.DocumentTitle,
            DefaultFileExtension = ".docx"
        };

        picker.FileTypeChoices.Add("Word Document", new List<string> { ".docx" });
        picker.FileTypeChoices.Add("HTML Document", new List<string> { ".html" });
        picker.FileTypeChoices.Add("EPUB Publication", new List<string> { ".epub" });
        picker.FileTypeChoices.Add("Rich Text Format", new List<string> { ".rtf" });
        picker.FileTypeChoices.Add("OpenDocument Text", new List<string> { ".odt" });
        picker.FileTypeChoices.Add("LaTeX Document", new List<string> { ".tex" });
        picker.FileTypeChoices.Add("Typst Document", new List<string> { ".typ" });
        picker.FileTypeChoices.Add("reStructuredText Document", new List<string> { ".rst" });
        picker.FileTypeChoices.Add("Org Mode Document", new List<string> { ".org" });

        InitializePickerWithMainWindow(picker);

        Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private async Task<string?> PromptForUrlAsync()
    {
        var urlTextBox = new TextBox
        {
            Header = "URL",
            PlaceholderText = "https://example.com/docs/",
            Width = 420
        };

        urlTextBox.Loaded += (_, _) => urlTextBox.Focus(FocusState.Programmatic);

        var dialog = new ContentDialog
        {
            Title = "Crawl documentation",
            Content = urlTextBox,
            PrimaryButtonText = "Crawl",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? urlTextBox.Text.Trim() : null;
    }

    private static void ExitApplication()
    {
        (Application.Current as App)?.MainWindow?.Close();
    }

    private static void InitializePickerWithMainWindow(object picker)
    {
        Window? window = (Application.Current as App)?.MainWindow;

        if (window is null)
        {
            throw new InvalidOperationException("The main window is not available for picker initialization.");
        }

        IntPtr hwnd = WindowNative.GetWindowHandle(window);

        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("The main window handle is not available for picker initialization.");
        }

        InitializeWithWindow.Initialize(picker, hwnd);
    }
}

``

## MDViewer/MDViewer.csproj

``xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <RootNamespace>MDViewer</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <Platform>x64</Platform>
    <Platforms>x64</Platforms>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PlatformTarget>x64</PlatformTarget>
    <UseWinUI>true</UseWinUI>
    <EnableMsixTooling>true</EnableMsixTooling>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationIcon>..\Assets\AppIcon.ico</ApplicationIcon>
    
    <WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="AngleSharp" Version="1.4.0" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.2" />
    <PackageReference Include="CommunityToolkit.WinUI.UI.Controls.Layout" Version="7.1.2" />
    <PackageReference Include="CommunityToolkit.WinUI.UI.Controls.Markdown" Version="7.1.2" />
    <PackageReference Include="Markdig" Version="1.2.0" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.28000.1839" />
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.1.3" />
    <PackageReference Include="PdfPig" Version="0.1.14" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="..\Assets\**\*">
      <Link>Assets\%(RecursiveDir)%(Filename)%(Extension)</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <CopyToPublishDirectory>Never</CopyToPublishDirectory>
    </Content>
  </ItemGroup>
</Project>

``

## README.md

``markdown
# MDViewer

MDViewer is a Windows Markdown viewer and conversion tool built with WinUI 3. It opens Markdown files directly, renders a readable preview, builds a heading outline for navigation, and uses Pandoc when you need to import, export, or crawl content into Markdown.

<img width="1920" height="1031" alt="screenshot-1" src="https://github.com/user-attachments/assets/ab6aad69-eb4e-4514-9168-37baa1084bcc" />

## Features

- Open `.md` and `.txt` files directly.
- Render Markdown in a native WinUI 3 preview.
- Toggle between rich preview and raw Markdown.
- Navigate long documents with an automatically generated heading tree.
- View line count, character count, UTF-8 encoding, and zoom level.
- Save Markdown as `.md` or `.txt`.
- Format Markdown through Pandoc using Pandoc Markdown, ATX headings, and no hard wrapping.
- Reflow heading levels into a cleaner hierarchy.
- Import `.docx`, `.html`, and `.epub` into Markdown through Pandoc.
- Import `.pdf` with native C# text extraction and Windows OCR fallback for image-only pages.
- Export Markdown through Pandoc to `.docx`, `.html`, `.epub`, `.rtf`, `.odt`, `.tex`, `.typ`, `.rst`, and `.org`.
- Crawl documentation sites into Markdown with a conservative single-threaded crawler that respects `robots.txt`.

## Download

Download the latest Windows x64 executable from the GitHub Releases page.

The release is a portable `MDViewer.exe`, not an installer or MSIX package. You can run it directly, pass a file path such as `MDViewer.exe README.md`, or use Windows `Open with` for Markdown files.

Because the executable is unsigned, Windows SmartScreen may show an unknown-publisher warning.

## Pandoc

Pandoc is optional for basic Markdown viewing. You need Pandoc for import, export, formatting, and crawl features.

MDViewer looks for Pandoc in this order:

1. `pandoc.exe` beside `MDViewer.exe`.
2. `Assets\pandoc.exe` beside `MDViewer.exe`.
3. `pandoc.exe` on `PATH`.

Use `Fetch Pandoc` inside MDViewer to download the latest Windows x64 Pandoc release. The app places `pandoc.exe` beside `MDViewer.exe` and does not modify user or machine environment variables.

If the folder containing `MDViewer.exe` is not writable, move the app to a writable folder, place `pandoc.exe` beside it manually, or install Pandoc yourself and put it on `PATH`.

## PDF Import

PDF import is built in and does not require Python, Marker, or model downloads. MDViewer extracts embedded PDF text first, then uses Windows OCR for pages that appear to be image-only. Complex tables and multi-column layouts may still need review after import.

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

``

## Build/RELEASE_README.md

``markdown
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
- Imports `.pdf` with native C# text extraction and Windows OCR fallback for image-only pages.
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

## PDF Import

PDF import is built in and does not require Python, Marker, or model downloads. MDViewer extracts embedded PDF text first, then uses Windows OCR for pages that appear to be image-only. Complex tables and multi-column layouts may still need review after import.

## Running The App

Download `MDViewer.exe` and run it directly. This is not an installer or MSIX package.

You can also pass a document path to the executable, for example `MDViewer.exe README.md`, or use Windows `Open with` to launch MDViewer for a Markdown file.

Because this build is distributed as a direct executable, Windows SmartScreen may warn that the app is from an unknown publisher. That is expected for an unsigned release binary.

## Current Limits

- This release is Windows x64 only.
- JavaScript-heavy documentation sites may crawl as shell pages if their content is rendered only in the browser.
- Crawling is intentionally conservative: single-threaded, delayed between requests, same-base-path by default, and capped to avoid hammering sites.
- Imported and crawled Markdown may still need review when source documents contain complex tables, unusual HTML, or hand-written heading anchors.

``

