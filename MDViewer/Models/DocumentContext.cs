namespace MDViewer.Models;

public enum DocumentOrigin
{
    NativeMarkdown,
    ImportedForeign,
    CrawledContent
}

public class DocumentContext
{
    public string? SourceFilePath { get; set; }
    public DocumentOrigin Origin { get; set; }
    public string RawMarkdown { get; set; } = string.Empty;
    public string DocumentTitle => string.IsNullOrEmpty(SourceFilePath) 
        ? "Untitled" 
        : System.IO.Path.GetFileName(SourceFilePath);
}
