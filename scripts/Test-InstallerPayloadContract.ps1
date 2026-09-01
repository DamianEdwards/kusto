[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'install\install-kusto-cli.ps1') -NoExecute

function New-TestPayload
{
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string[]]$Files
    )

    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    foreach ($relativePath in $Files)
    {
        $path = Join-Path $Directory $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        Set-Content -LiteralPath $path -Value $relativePath -NoNewline
    }

    $manifestFiles = @($Files | ForEach-Object { $_.Replace('\', '/') })
    [System.Array]::Sort($manifestFiles, [System.StringComparer]::Ordinal)
    [ordered]@{ files = $manifestFiles } |
        ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath (Join-Path $Directory 'payload-manifest.json')
}

function Assert-FileExists
{
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "Expected file '$Path' was not found."
    }
}

function Assert-FileMissing
{
    param([Parameter(Mandatory)][string]$Path)

    if (Test-Path -LiteralPath $Path)
    {
        throw "File '$Path' should have been removed."
    }
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("kusto-installer-contract-" + [guid]::NewGuid().ToString('N'))
$firstPayload = Join-Path $root 'first'
$secondPayload = Join-Path $root 'second'
$installDirectory = Join-Path $root 'install'

try
{
    $firstFiles = @(
        'kusto.exe',
        'libSkiaSharp.dll',
        'msalruntime.dll',
        'LICENSE',
        'obsolete.dat'
    )
    New-TestPayload -Directory $firstPayload -Files $firstFiles
    $firstManifest = @(Assert-PayloadManifestMatches -ExtractDirectory $firstPayload)
    Assert-ExtractedPayloadComplete -ExtractDirectory $firstPayload -RequiredFileNames @('kusto.exe', 'payload-manifest.json')

    $executables = @(Get-WindowsExecutablePayloadFiles -PayloadFiles $firstManifest)
    if (Compare-Object @('kusto.exe', 'libSkiaSharp.dll', 'msalruntime.dll') $executables)
    {
        throw "Executable payload discovery did not return every .exe and .dll file."
    }

    Install-KustoPayload `
        -SourceDirectory $firstPayload `
        -InstallDirectory $installDirectory `
        -KnownPayloadFileNames $firstManifest
    Assert-FileExists -Path (Join-Path $installDirectory 'obsolete.dat')

    $secondFiles = @(
        'kusto.exe',
        'msalruntime.dll',
        'future-sidecar.dll',
        'data\future-format.json'
    )
    New-TestPayload -Directory $secondPayload -Files $secondFiles
    $secondManifest = @(Assert-PayloadManifestMatches -ExtractDirectory $secondPayload)
    Install-KustoPayload `
        -SourceDirectory $secondPayload `
        -InstallDirectory $installDirectory `
        -KnownPayloadFileNames $secondManifest

    Assert-FileMissing -Path (Join-Path $installDirectory 'libSkiaSharp.dll')
    Assert-FileMissing -Path (Join-Path $installDirectory 'obsolete.dat')
    Assert-FileExists -Path (Join-Path $installDirectory 'future-sidecar.dll')
    Assert-FileExists -Path (Join-Path $installDirectory 'data\future-format.json')

    Write-Host 'Installer payload contract validation passed.'
}
finally
{
    if (Test-Path $root)
    {
        Remove-Item $root -Recurse -Force
    }
}
