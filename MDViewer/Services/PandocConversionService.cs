using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MDViewer.Services;

public sealed class PandocConversionException : InvalidOperationException
{
    public PandocConversionException(string message, int exitCode, string standardError)
        : base($"{message} Pandoc exited with code {exitCode}. {standardError}".Trim())
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public int ExitCode { get; }

    public string StandardError { get; }
}

public class PandocConversionService
{
    private const string MetadataSafeMarkdownReaderFormat = "markdown-yaml_metadata_block";
    private static readonly Encoding PandocEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public PandocConversionService()
    {
    }

    public async Task<string> ImportAsMarkdownAsync(string inputFilePath)
    {
        EnsureInputFileExists(inputFilePath);

        ProcessStartInfo startInfo = CreatePandocStartInfo(Pandoc.FindPandocOrThrow());
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(inputFilePath);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(Pandoc.MarkdownFormat);
        Pandoc.AddMarkdownWriterOptions(startInfo);

        PandocProcessResult result = await RunPandocAsync(startInfo);
        return result.StandardOutput;
    }

    public async Task<string> FormatMarkdownAsync(string rawMarkdown)
    {
        ArgumentNullException.ThrowIfNull(rawMarkdown);

        MarkdownTextParts parts = SplitOpeningMetadataBlock(rawMarkdown);
        ProcessStartInfo startInfo = CreatePandocStartInfo(Pandoc.FindPandocOrThrow());
        startInfo.RedirectStandardInput = true;
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(MetadataSafeMarkdownReaderFormat);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(Pandoc.MarkdownFormat);
        Pandoc.AddMarkdownWriterOptions(startInfo);

        PandocProcessResult result = await RunPandocAsync(startInfo, parts.Body);
        return ReattachOpeningMetadataBlock(parts.MetadataBlock, result.StandardOutput);
    }

    public async Task ExportFromMarkdownAsync(string rawMarkdown, string targetFilePath, string targetExtension)
    {
        if (string.IsNullOrWhiteSpace(targetFilePath))
        {
            throw new ArgumentException("A target file path is required.", nameof(targetFilePath));
        }

        string outputFormat = GetPandocOutputFormat(targetExtension);
        string? targetDirectory = Path.GetDirectoryName(targetFilePath);

        if (!string.IsNullOrWhiteSpace(targetDirectory) && !Directory.Exists(targetDirectory))
        {
            throw new DirectoryNotFoundException($"Target directory not found: {targetDirectory}");
        }

        ProcessStartInfo startInfo = CreatePandocStartInfo(Pandoc.FindPandocOrThrow());
        startInfo.RedirectStandardInput = true;
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(Pandoc.MarkdownFormat);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(outputFormat);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(targetFilePath);

        await RunPandocAsync(startInfo, rawMarkdown);
    }

    public Task<string> ConvertToMarkdownAsync(string inputFilePath)
    {
        return ImportAsMarkdownAsync(inputFilePath);
    }

    private static ProcessStartInfo CreatePandocStartInfo(string pandocPath)
    {
        return new ProcessStartInfo
        {
            FileName = pandocPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = PandocEncoding,
            StandardErrorEncoding = PandocEncoding,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private async Task<PandocProcessResult> RunPandocAsync(ProcessStartInfo startInfo, string? standardInput = null)
    {
        if (standardInput is not null)
        {
            startInfo.RedirectStandardInput = true;
            startInfo.StandardInputEncoding = PandocEncoding;
        }

        using Process process = new() { StartInfo = startInfo };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start Pandoc.");
        }

        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync();

        string standardOutput = await standardOutputTask;
        string standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            throw new PandocConversionException("Pandoc conversion failed.", process.ExitCode, standardError);
        }

        return new PandocProcessResult(standardOutput, standardError);
    }

    private static void EnsureInputFileExists(string inputFilePath)
    {
        if (!File.Exists(inputFilePath))
        {
            throw new FileNotFoundException($"Input file not found at {inputFilePath}");
        }
    }

    private static MarkdownTextParts SplitOpeningMetadataBlock(string markdown)
    {
        string normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return new MarkdownTextParts(null, markdown);
        }

        int firstLineEnd = "---\n".Length;

        if (firstLineEnd >= normalized.Length || normalized[firstLineEnd] is '\n' or '\r')
        {
            return new MarkdownTextParts(null, markdown);
        }

        foreach (var (line, lineEnd) in EnumerateNormalizedLines(normalized[firstLineEnd..], firstLineEnd))
        {
            string trimmed = line.Trim();

            if (trimmed is not ("---" or "..."))
            {
                continue;
            }

            string metadataBlock = normalized[..lineEnd].TrimEnd();
            string body = normalized[lineEnd..].TrimStart('\n');
            return new MarkdownTextParts(metadataBlock, body);
        }

        return new MarkdownTextParts(null, markdown);
    }

    private static IEnumerable<(string Line, int LineEnd)> EnumerateNormalizedLines(string text, int startOffset)
    {
        int lineStart = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            yield return (text[lineStart..i], startOffset + i + 1);
            lineStart = i + 1;
        }

        if (lineStart < text.Length)
        {
            yield return (text[lineStart..], startOffset + text.Length);
        }
    }

    private static string ReattachOpeningMetadataBlock(string? metadataBlock, string body)
    {
        if (string.IsNullOrWhiteSpace(metadataBlock))
        {
            return body;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return metadataBlock + Environment.NewLine;
        }

        return metadataBlock + Environment.NewLine + Environment.NewLine + body.TrimStart();
    }

    private static string GetPandocOutputFormat(string targetExtension)
    {
        string normalizedExtension = targetExtension.StartsWith('.')
            ? targetExtension.ToLowerInvariant()
            : $".{targetExtension.ToLowerInvariant()}";

        return normalizedExtension switch
        {
            ".docx" => "docx",
            ".html" => "html",
            ".epub" => "epub",
            ".rtf" => "rtf",
            ".odt" => "odt",
            ".tex" => "latex",
            ".typ" => "typst",
            ".rst" => "rst",
            ".org" => "org",
            _ => throw new NotSupportedException($"Unsupported export format: {targetExtension}")
        };
    }

    private sealed record PandocProcessResult(string StandardOutput, string StandardError);

    private sealed record MarkdownTextParts(string? MetadataBlock, string Body);
}
