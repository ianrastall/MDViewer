using System;
using System.Diagnostics;
using System.IO;
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
    private readonly MarkdownFormatterService _markdownFormatterService;

    public PandocConversionService()
        : this(new MarkdownFormatterService())
    {
    }

    public PandocConversionService(MarkdownFormatterService markdownFormatterService)
    {
        _markdownFormatterService = markdownFormatterService ?? throw new ArgumentNullException(nameof(markdownFormatterService));
    }

    public async Task<string> ImportAsMarkdownAsync(string inputFilePath)
    {
        EnsureInputFileExists(inputFilePath);

        ProcessStartInfo startInfo = CreatePandocStartInfo(Pandoc.FindPandocOrThrow());
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(inputFilePath);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("markdown");
        startInfo.ArgumentList.Add("--markdown-headings=atx");
        startInfo.ArgumentList.Add("--wrap=none");

        PandocProcessResult result = await RunPandocAsync(startInfo);
        return _markdownFormatterService.FormatAndLint(result.StandardOutput);
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
        startInfo.ArgumentList.Add("markdown");
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
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private async Task<PandocProcessResult> RunPandocAsync(ProcessStartInfo startInfo, string? standardInput = null)
    {
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
}
