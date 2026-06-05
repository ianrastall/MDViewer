param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\Build\PDF_IMPORT_CODE_DUMP.md'),
    [switch]$IncludeGitDiff
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $OutputPath

if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

function Get-RelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    return [System.IO.Path]::GetRelativePath($repoRoot, $fullPath).Replace('\', '/')
}

function Get-GitOutput {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    try {
        return (& git -C $repoRoot @Arguments 2>$null) -join "`n"
    }
    catch {
        return ''
    }
}

function Add-FileDump {
    param(
        [Parameter(Mandatory = $true)][System.Text.StringBuilder]$Builder,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Fence
    )

    $fullPath = Join-Path $repoRoot $Path

    [void]$Builder.AppendLine("## $Path")
    [void]$Builder.AppendLine()

    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        [void]$Builder.AppendLine("_Missing from working tree._")
        [void]$Builder.AppendLine()
        return
    }

    [void]$Builder.AppendLine("````$Fence")
    [void]$Builder.AppendLine((Get-Content -Raw -LiteralPath $fullPath))
    [void]$Builder.AppendLine("````")
    [void]$Builder.AppendLine()
}

$dumpFiles = @(
    @{ Path = 'MDViewer/Services/PdfImportService.cs'; Fence = 'csharp' },
    @{ Path = 'MDViewer/ViewModels/MainViewModel.cs'; Fence = 'csharp' },
    @{ Path = 'MDViewer/Views/MainPage.xaml.cs'; Fence = 'csharp' },
    @{ Path = 'MDViewer/MDViewer.csproj'; Fence = 'xml' },
    @{ Path = 'README.md'; Fence = 'markdown' },
    @{ Path = 'Build/RELEASE_README.md'; Fence = 'markdown' }
)

$branch = Get-GitOutput @('rev-parse', '--abbrev-ref', 'HEAD')
$commit = Get-GitOutput @('rev-parse', '--short', 'HEAD')
$status = Get-GitOutput @('status', '--short')
$generatedAt = Get-Date -Format 'yyyy-MM-dd HH:mm:ss K'

$builder = [System.Text.StringBuilder]::new()

[void]$builder.AppendLine('# MDViewer PDF Import Code Dump')
[void]$builder.AppendLine()
[void]$builder.AppendLine("- Generated: $generatedAt")
[void]$builder.AppendLine("- Repository: $repoRoot")

if (-not [string]::IsNullOrWhiteSpace($branch)) {
    [void]$builder.AppendLine("- Branch: $branch")
}

if (-not [string]::IsNullOrWhiteSpace($commit)) {
    [void]$builder.AppendLine("- Commit: $commit")
}

[void]$builder.AppendLine()

if (-not [string]::IsNullOrWhiteSpace($status)) {
    [void]$builder.AppendLine('## Working Tree Status')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('```text')
    [void]$builder.AppendLine($status)
    [void]$builder.AppendLine('```')
    [void]$builder.AppendLine()
}

[void]$builder.AppendLine('## What This Part Of The Program Does')
[void]$builder.AppendLine()
[void]$builder.AppendLine('The PDF import path lets MDViewer open a `.pdf` file and turn it into Markdown without asking the user to install Python, Marker, model weights, or any external PDF/OCR command-line tool. It uses a native C# PDF text extraction library first, then falls back to Windows built-in OCR for pages where the embedded PDF text is missing or looks too weak to trust.')
[void]$builder.AppendLine()
[void]$builder.AppendLine('The design is intentionally pragmatic: it favors a dependable no-setup import path over a heavyweight ML layout system. It will not perfectly reconstruct every complex table, multi-column page, or scanned layout, but it should produce useful Markdown for common text PDFs and many scanned/image-only PDFs.')
[void]$builder.AppendLine()

[void]$builder.AppendLine('## Main Components')
[void]$builder.AppendLine()
[void]$builder.AppendLine('- `PdfImportService`: owns PDF-to-Markdown conversion. It extracts embedded text with PdfPig, runs Windows OCR where needed, builds Markdown, and returns diagnostics.')
[void]$builder.AppendLine('- `PdfImportResult`: carries the Markdown plus quality signals such as page count, OCR page count, missing pages, and warnings.')
[void]$builder.AppendLine('- `MainViewModel.LoadFileAsync`: routes `.pdf` files to `PdfImportService` and updates the current document.')
[void]$builder.AppendLine('- `MainPage.PickOpenFilePathAsync`: adds `.pdf` to the file picker.')
[void]$builder.AppendLine('- `MDViewer.csproj`: pins the app to Windows x64 and references `PdfPig`.')
[void]$builder.AppendLine()

[void]$builder.AppendLine('## End-To-End Flow')
[void]$builder.AppendLine()
[void]$builder.AppendLine('1. The user chooses a `.pdf` file from the Open picker.')
[void]$builder.AppendLine('2. `MainViewModel.LoadFileAsync` detects the `.pdf` extension and creates a `Progress<string>` reporter that writes status messages to the status bar.')
[void]$builder.AppendLine('3. `PdfImportService.ImportAsMarkdownAsync` validates the path and wraps unexpected failures in `PdfImportException` so the UI gets a cleaner import error.')
[void]$builder.AppendLine('4. Embedded text extraction runs on a background thread through PdfPig and `ContentOrderTextExtractor`.')
[void]$builder.AppendLine('5. Each page is scored with `ShouldRunOcr`. Blank, very short, or suspiciously weak text pages are sent through Windows OCR.')
[void]$builder.AppendLine('6. OCR renders each target page with `Windows.Data.Pdf.PdfPage.RenderToStreamAsync`, decodes it with `BitmapDecoder`, and recognizes text with `Windows.Media.Ocr.OcrEngine`.')
[void]$builder.AppendLine('7. OCR failures are isolated per page. One bad page records a warning and the import continues.')
[void]$builder.AppendLine('8. Pages without readable text after extraction/OCR are reported as missing. If every page is unreadable, the import fails instead of returning a fake successful document with only a title.')
[void]$builder.AppendLine('9. The service normalizes text into Markdown while preserving more structure than a simple line-flattening pass: bullets, indentation, table-like pipes, repeated spacing, and page markers.')
[void]$builder.AppendLine('10. The ViewModel displays the Markdown and reports whether OCR or unreadable pages were involved.')
[void]$builder.AppendLine()

[void]$builder.AppendLine('## Important Implementation Details')
[void]$builder.AppendLine()
[void]$builder.AppendLine('- OCR is triggered by a weak-text heuristic, not only empty pages. This catches scanned PDFs with tiny junk text layers.')
[void]$builder.AppendLine('- Page arrays are pre-populated defensively so downstream code does not dereference null page slots.')
[void]$builder.AppendLine('- Cancellation is checked during embedded extraction and before/inside expensive OCR work.')
[void]$builder.AppendLine('- OCR render dimensions are capped against `OcrEngine.MaxImageDimension` so large PDF pages do not exceed Windows OCR limits.')
[void]$builder.AppendLine('- `Windows.Data.Pdf.PdfDocument` is not wrapped in `using` because this project’s WinRT projection does not expose it as `IDisposable`; `PdfPage`, `SoftwareBitmap`, and render streams are disposed where supported.')
[void]$builder.AppendLine('- The service accepts an optional OCR language tag internally, then falls back to the user profile languages when no tag is supplied or the requested language is unavailable.')
[void]$builder.AppendLine()

[void]$builder.AppendLine('## Known Limits')
[void]$builder.AppendLine()
[void]$builder.AppendLine('- PDF layout recovery is heuristic. Complex tables, multi-column reading order, footnotes, forms, and code blocks may need manual cleanup.')
[void]$builder.AppendLine('- Windows OCR quality depends on installed Windows OCR language packs and the source image quality.')
[void]$builder.AppendLine('- PdfPig text order can be imperfect for PDFs with unusual internal structure.')
[void]$builder.AppendLine('- The current UI exposes import diagnostics in the status bar, not a detailed per-page report panel.')
[void]$builder.AppendLine()

[void]$builder.AppendLine('## Validation Commands')
[void]$builder.AppendLine()
[void]$builder.AppendLine('```powershell')
[void]$builder.AppendLine('dotnet build MDViewer.slnx -c Debug')
[void]$builder.AppendLine('dotnet build MDViewer.slnx -c Release')
[void]$builder.AppendLine('```')
[void]$builder.AppendLine()

[void]$builder.AppendLine('## Files Included')
[void]$builder.AppendLine()

foreach ($file in $dumpFiles) {
    [void]$builder.AppendLine("- $($file.Path)")
}

[void]$builder.AppendLine()
[void]$builder.AppendLine('# Source Code')
[void]$builder.AppendLine()

foreach ($file in $dumpFiles) {
    Add-FileDump -Builder $builder -Path $file.Path -Fence $file.Fence
}

if ($IncludeGitDiff) {
    $diff = Get-GitOutput @('diff', '--', 'MDViewer/Services/PdfImportService.cs', 'MDViewer/ViewModels/MainViewModel.cs', 'MDViewer/Views/MainPage.xaml.cs', 'MDViewer/MDViewer.csproj', 'README.md', 'Build/RELEASE_README.md')

    [void]$builder.AppendLine('# Current Git Diff')
    [void]$builder.AppendLine()

    if ([string]::IsNullOrWhiteSpace($diff)) {
        [void]$builder.AppendLine('_No diff for the PDF import files._')
    }
    else {
        [void]$builder.AppendLine('````diff')
        [void]$builder.AppendLine($diff)
        [void]$builder.AppendLine('````')
    }

    [void]$builder.AppendLine()
}

[System.IO.File]::WriteAllText($OutputPath, $builder.ToString(), [System.Text.UTF8Encoding]::new($false))

Write-Host "PDF import code dump written to $OutputPath"
