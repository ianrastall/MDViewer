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

        InitializePickerWithMainWindow(picker);

        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        return file?.Path;
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
