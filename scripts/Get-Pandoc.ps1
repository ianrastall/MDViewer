param(
    [string]$AssetsDirectory = (Join-Path $PSScriptRoot '..\Assets'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$AssetsDirectory = [System.IO.Path]::GetFullPath($AssetsDirectory)
$pandocExe = Join-Path $AssetsDirectory 'pandoc.exe'

if ((Test-Path -LiteralPath $pandocExe) -and -not $Force) {
    Write-Host "Pandoc already exists at $pandocExe"
    Write-Host "Use -Force to download and replace it."
    exit 0
}

New-Item -ItemType Directory -Path $AssetsDirectory -Force | Out-Null

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$headers = @{
    'User-Agent' = 'MDViewer-pandoc-bootstrap'
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("MDViewer-Pandoc-" + [System.Guid]::NewGuid().ToString('N'))
$zipPath = Join-Path $tempRoot 'pandoc.zip'

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    Write-Host 'Finding the latest Pandoc Windows release...'
    $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/jgm/pandoc/releases/latest' -Headers $headers
    $asset = $release.assets |
        Where-Object { $_.name -match '^pandoc-.+-windows-x86_64\.zip$' } |
        Select-Object -First 1

    if ($null -eq $asset) {
        throw 'Could not find a windows-x86_64 Pandoc ZIP asset on the latest release.'
    }

    Write-Host "Downloading $($asset.name)..."
    Invoke-WebRequest -Uri $asset.browser_download_url -Headers $headers -OutFile $zipPath

    Write-Host 'Extracting Pandoc...'
    Expand-Archive -Path $zipPath -DestinationPath $tempRoot -Force

    $downloadedPandoc = Get-ChildItem -Path $tempRoot -Filter 'pandoc.exe' -Recurse -File |
        Select-Object -First 1

    if ($null -eq $downloadedPandoc) {
        throw 'The downloaded Pandoc archive did not contain pandoc.exe.'
    }

    Copy-Item -LiteralPath $downloadedPandoc.FullName -Destination $pandocExe -Force
    Write-Host "Pandoc installed at $pandocExe"
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
