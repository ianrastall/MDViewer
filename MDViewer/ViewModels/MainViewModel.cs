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
        _pandocService = new PandocConversionService(_markdownFormatterService);
        _pandocDownloadService = new PandocDownloadService();
        _crawlUrlService = new CrawlUrlService(_markdownFormatterService);
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
    private void Format()
    {
        try
        {
            string markdown = _markdownFormatterService.FormatAndLint(CurrentDocument.RawMarkdown);
            SetCurrentMarkdown(markdown, preserveViewMode: true);
            DocumentStatus = "Formatted Markdown.";
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

    private void SetCommandFailureStatus(string action, Exception exception)
    {
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
