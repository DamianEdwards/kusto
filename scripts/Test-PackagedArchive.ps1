# Hermetic packaged-artifact smoke test.
#
# Validates the release archive shape that users actually receive:
#   1. Publish-NativeAsset.ps1 emits a self-contained archive.
#   2. The archive expands to a directory containing kusto[.exe] plus all
#      required native sidecars (libSkiaSharp.*, libHarfBuzzSharp.*).
#   3. The extracted binary loads its native rendering stack and produces a
#      valid PNG via the hidden `_diag chart-self-test` command. This catches
#      DllNotFoundException-class failures that succeeded against the build
#      output but explode when run from the archive payload.
#
# The test does not need network access, Kusto auth, or a live cluster — it
# exercises the same SkiaSharp/HarfBuzz native loading path that real chart
# rendering uses, against the actual archived binary.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RuntimeIdentifier,

    [string]$Version = '0.0.0-smoketest',

    [string]$ArtifactsDirectory = (Join-Path $PSScriptRoot '..\artifacts\smoketest'),

    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsDirectory = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
$publishScript = Join-Path $PSScriptRoot 'Publish-NativeAsset.ps1'

if (-not (Test-Path $publishScript))
{
    throw "Publish script '$publishScript' not found."
}

$platform = $RuntimeIdentifier.Split('-', 2)[0]
$binaryName = if ($platform -eq 'win') { 'kusto.exe' } else { 'kusto' }
$archiveName = if ($platform -eq 'win') { "kusto-$RuntimeIdentifier.zip" } else { "kusto-$RuntimeIdentifier.tar.gz" }

if (-not $SkipPublish)
{
    if (Test-Path $artifactsDirectory)
    {
        Remove-Item $artifactsDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null

    Write-Host "Publishing native asset for runtime '$RuntimeIdentifier'..." -ForegroundColor Cyan
    & $publishScript -RuntimeIdentifier $RuntimeIdentifier -Version $Version -ArtifactsDirectory $artifactsDirectory
}

$archivePath = Join-Path $artifactsDirectory $archiveName
if (-not (Test-Path $archivePath))
{
    throw "Expected archive '$archivePath' was not produced."
}

$expandRoot = Join-Path $artifactsDirectory ("expand-" + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $expandRoot -Force | Out-Null

try
{
    Write-Host "Expanding archive '$archivePath' to '$expandRoot'..." -ForegroundColor Cyan
    if ($platform -eq 'win')
    {
        Expand-Archive -Path $archivePath -DestinationPath $expandRoot -Force
    }
    else
    {
        & tar -xzf $archivePath -C $expandRoot
        if ($LASTEXITCODE -ne 0)
        {
            throw "Failed to extract archive '$archivePath'."
        }
    }

    # Structural check: required files must be present in the expanded payload.
    $requiredNames = switch ($platform)
    {
        'win'
        {
            $architecture = $RuntimeIdentifier.Split('-', 2)[1]
            $msalRuntime = switch ($architecture)
            {
                'x64'   { 'msalruntime.dll' }
                'arm64' { 'msalruntime_arm64.dll' }
                default { throw "Unsupported Windows architecture in runtime identifier '$RuntimeIdentifier'." }
            }

            @('kusto.exe', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', $msalRuntime, 'libsodium.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'payload-manifest.json')
        }
        'linux' { @('kusto', 'libSkiaSharp.so', 'libHarfBuzzSharp.so', 'libsodium.so', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'payload-manifest.json') }
        'osx'   { @('kusto', 'libSkiaSharp.dylib', 'libHarfBuzzSharp.dylib', 'libsodium.dylib', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'payload-manifest.json') }
        default { throw "Unsupported platform '$platform'." }
    }

    $presentFiles = @(Get-ChildItem -Path $expandRoot -File -Recurse | ForEach-Object { $_.Name }) | Sort-Object -Unique
    $missing = @($requiredNames | Where-Object { $_ -notin $presentFiles })
    if ($missing.Count -gt 0)
    {
        $listing = ($presentFiles | Sort-Object) -join ', '
        throw "Expanded archive is missing required file(s): $($missing -join ', '). Found: $listing"
    }

    Write-Host "Required payload files present: $($requiredNames -join ', ')" -ForegroundColor Green

    $manifestName = 'payload-manifest.json'
    $manifestPath = Join-Path $expandRoot $manifestName
    $manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
    if ($manifest -isnot [pscustomobject] -or
        $manifest.PSObject.Properties.Name -notcontains 'files' -or
        $manifest.files -isnot [array])
    {
        throw "'$manifestName' must be a JSON object with a 'files' array."
    }
    $declaredFiles = @($manifest.files)
    if ($declaredFiles.Count -eq 0)
    {
        throw "'$manifestName' did not declare any payload files."
    }

    $normalizedDeclaredFiles = @(
        $declaredFiles |
            ForEach-Object {
                if ($_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) -or [System.IO.Path]::IsPathRooted($_))
                {
                    throw "'$manifestName' contains invalid install-relative path '$_'."
                }
                if ($_.Contains('\'))
                {
                    throw "'$manifestName' path '$_' is not slash-normalized."
                }
                $_
            }
    )
    if (@($normalizedDeclaredFiles | Sort-Object -Unique).Count -ne $normalizedDeclaredFiles.Count)
    {
        throw "'$manifestName' contains duplicate paths."
    }
    for ($index = 1; $index -lt $normalizedDeclaredFiles.Count; $index++)
    {
        if ([System.StringComparer]::Ordinal.Compare(
                $normalizedDeclaredFiles[$index - 1],
                $normalizedDeclaredFiles[$index]) -gt 0)
        {
            throw "'$manifestName' paths must be sorted using ordinal comparison."
        }
    }

    $actualManifestFiles = @(
        Get-ChildItem -Path $expandRoot -File -Recurse |
            ForEach-Object { [System.IO.Path]::GetRelativePath($expandRoot, $_.FullName).Replace('\', '/') } |
            Where-Object { $_ -ne $manifestName }
    )
    $manifestDifference = @(Compare-Object $normalizedDeclaredFiles $actualManifestFiles)
    if ($manifestDifference.Count -gt 0)
    {
        $details = $manifestDifference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }
        throw "'$manifestName' does not exactly describe the archive payload (the manifest itself is intentionally excluded): $($details -join ', ')."
    }

    # Runtime check: invoke the binary from the expanded archive, not from the
    # build output. This proves SkiaSharp/HarfBuzz native libraries actually
    # load via the host's normal probing rules from a fresh install layout.
    $binaryPath = Join-Path $expandRoot $binaryName
    if (-not (Test-Path $binaryPath))
    {
        throw "Extracted binary '$binaryPath' was not found."
    }

    if ($platform -ne 'win')
    {
        & chmod +x $binaryPath
    }

    $smokePngPath = Join-Path $expandRoot 'self-test.png'
    Write-Host "Running '$binaryPath _diag chart-self-test --output $smokePngPath'..." -ForegroundColor Cyan
    & $binaryPath '_diag' 'chart-self-test' '--output' $smokePngPath
    if ($LASTEXITCODE -ne 0)
    {
        throw "Chart self-test command exited with code $LASTEXITCODE. Native chart-rendering dependencies likely failed to load from the archived payload."
    }

    if (-not (Test-Path $smokePngPath))
    {
        throw "Chart self-test reported success but '$smokePngPath' was not written."
    }

    $bytes = [System.IO.File]::ReadAllBytes($smokePngPath)
    if ($bytes.Length -lt 100)
    {
        throw "Self-test PNG '$smokePngPath' is suspiciously small ($($bytes.Length) bytes)."
    }

    $pngSignature = @(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    for ($i = 0; $i -lt $pngSignature.Length; $i++)
    {
        if ($bytes[$i] -ne $pngSignature[$i])
        {
            throw "Self-test PNG '$smokePngPath' does not start with the PNG signature at byte $i (got 0x$($bytes[$i].ToString('X2')))."
        }
    }

    Write-Host "Smoke test passed for runtime '$RuntimeIdentifier'. PNG: $smokePngPath ($($bytes.Length) bytes)." -ForegroundColor Green
}
finally
{
    if (Test-Path $expandRoot)
    {
        Remove-Item $expandRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
