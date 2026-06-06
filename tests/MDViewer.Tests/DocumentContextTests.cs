using MDViewer.Models;

namespace MDViewer.Tests;

public class DocumentContextTests
{
    [Fact]
    public void DocumentTitle_ReturnsUntitled_WhenSourceFilePathIsMissing()
    {
        var document = new DocumentContext();

        Assert.Equal("Untitled", document.DocumentTitle);
    }

    [Fact]
    public void DocumentTitle_ReturnsFileName_WhenSourceFilePathIsPresent()
    {
        var document = new DocumentContext
        {
            SourceFilePath = @"C:\Docs\example.md"
        };

        Assert.Equal("example.md", document.DocumentTitle);
    }
}
