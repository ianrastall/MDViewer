using MDViewer.Services;

namespace MDViewer.Tests;

public class MarkdownFormatterServiceTests
{
    [Fact]
    public void ReflowHeadings_NormalizesSkippedHeadingLevels()
    {
        var service = new MarkdownFormatterService();

        MarkdownReflowResult result = service.ReflowHeadings(
            "# Title\n\n#### Too deep\n\n### Peer\n");

        Assert.Contains("## Too deep", result.Markdown);
        Assert.Contains("## Peer", result.Markdown);
        Assert.Equal(2, result.ChangedHeadingCount);
    }

    [Fact]
    public void ReflowHeadings_PreservesFrontMatter()
    {
        var service = new MarkdownFormatterService();

        MarkdownReflowResult result = service.ReflowHeadings(
            "---\ntitle: Sample\n---\n\n# Heading\n\nBody text\n");

        Assert.StartsWith("---\ntitle: Sample\n---", result.Markdown);
        Assert.Contains("# Heading", result.Markdown);
    }

    [Fact]
    public void FormatAndLint_ReturnsOriginalMarkdown_WhenFrontMatterIsUnclosed()
    {
        var service = new MarkdownFormatterService();
        const string markdown = "---\ntitle: Sample\n\n# Heading\n";

        string result = service.FormatAndLint(markdown);

        Assert.Contains("# Heading", result);
    }
}
