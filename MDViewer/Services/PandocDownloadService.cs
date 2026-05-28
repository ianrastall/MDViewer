using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MDViewer.Services;

public sealed record PandocDownloadResult(
    string Version,
    string PandocPath,
    long DownloadedBytes);

public sealed class PandocDownloadService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/jgm/pandoc/releases/latest";
    private const string WindowsX64AssetPattern = @"^pandoc-(?<version>.+)-windows-x86_64\.zip$";
    private const int ProgressIntervalPercent = 5;
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<PandocDownloadResult> DownloadLatestWindowsPandocAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Automatic Pandoc download is currently implemented for Windows.");
        }

        progress?.Report("Finding latest Pandoc release...");

        PandocReleaseAsset asset = await GetLatestWindowsX64ReleaseAssetAsync(cancellationToken);
        string targetPath = GetWritableInstallPath();
        string tempRoot = Path.Combine(Path.GetTempPath(), $"MDViewer-Pandoc-{Guid.NewGuid():N}");
        string zipPath = Path.Combine(tempRoot, asset.FileName);
        string extractPath = Path.Combine(tempRoot, "extracted");

        try
        {
            Directory.CreateDirectory(tempRoot);

            progress?.Report($"Downloading Pandoc {asset.Version}...");
            long downloadedBytes = await DownloadFileAsync(asset.DownloadUrl, zipPath, progress, cancellationToken);

            progress?.Report("Extracting Pandoc...");
            ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true);

            string downloadedPandoc = FindExtractedPandoc(extractPath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            progress?.Report("Installing Pandoc...");
            File.Copy(downloadedPandoc, targetPath, overwrite: true);

            progress?.Report($"Pandoc {asset.Version} installed.");
            return new PandocDownloadResult(asset.Version, targetPath, downloadedBytes);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch
                {
                    // Temporary files can be cleaned up by the OS if an antivirus scanner still has a handle open.
                }
            }
        }
    }

    private static async Task<PandocReleaseAsset> GetLatestWindowsX64ReleaseAssetAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await HttpClient.GetAsync(LatestReleaseApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        foreach (JsonElement asset in document.RootElement.GetProperty("assets").EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            Match match = Regex.Match(name, WindowsX64AssetPattern, RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                continue;
            }

            string? downloadUrl = asset.GetProperty("browser_download_url").GetString();

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                continue;
            }

            return new PandocReleaseAsset(
                FileName: name,
                Version: match.Groups["version"].Value,
                DownloadUrl: downloadUrl);
        }

        throw new InvalidOperationException("The latest Pandoc release did not include a Windows x64 ZIP asset.");
    }

    private static async Task<long> DownloadFileAsync(
        string url,
        string targetPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        long? contentLength = response.Content.Headers.ContentLength;
        long totalRead = 0;
        int nextProgressPercent = ProgressIntervalPercent;

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream target = new(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 128,
            useAsync: true);

        byte[] buffer = new byte[1024 * 128];

        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;

            if (contentLength is not > 0)
            {
                continue;
            }

            int percent = (int)(totalRead * 100 / contentLength.Value);

            if (percent < nextProgressPercent)
            {
                continue;
            }

            progress?.Report($"Downloading Pandoc {percent}%...");
            nextProgressPercent = Math.Min(100, percent + ProgressIntervalPercent);
        }

        return totalRead;
    }

    private static string FindExtractedPandoc(string extractPath)
    {
        foreach (string candidate in Directory.EnumerateFiles(extractPath, "pandoc.exe", SearchOption.AllDirectories))
        {
            return candidate;
        }

        throw new InvalidOperationException("The Pandoc ZIP did not contain pandoc.exe.");
    }

    private static string GetWritableInstallPath()
    {
        foreach (string candidate in Pandoc.GetPandocInstallCandidates())
        {
            string? directory = Path.GetDirectoryName(candidate);

            if (string.IsNullOrWhiteSpace(directory) || !CanWriteToDirectory(directory))
            {
                continue;
            }

            return candidate;
        }

        throw new IOException("No writable Pandoc installation folder was found.");
    }

    private static bool CanWriteToDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            string probePath = Path.Combine(directory, $".mdviewer-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30)
        };

        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MDViewer/1.0");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return httpClient;
    }

    private sealed record PandocReleaseAsset(
        string FileName,
        string Version,
        string DownloadUrl);
}
