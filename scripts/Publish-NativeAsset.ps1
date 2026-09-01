[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RuntimeIdentifier,

    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$ArtifactsDirectory,

    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\src\Kusto.Cli\Kusto.Cli.csproj'),

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projectPath = [System.IO.Path]::GetFullPath($ProjectPath)
$artifactsDirectory = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("kusto-publish-" + [System.Guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $publishRoot $RuntimeIdentifier
$stagingDirectory = Join-Path $publishRoot ("stage-" + $RuntimeIdentifier)

function Quote-CmdArgument
{
    param([Parameter(Mandatory)][string]$Value)

    if ([string]::IsNullOrEmpty($Value))
    {
        return '""'
    }

    if ($Value -notmatch '[\s"&()^<>|]')
    {
        return $Value
    }

    return '"' + $Value.Replace('"', '""') + '"'
}

function Get-VsDevCmdPath
{
    if (-not $IsWindows)
    {
        return $null
    }

    $vsWherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vsWherePath))
    {
        return $null
    }

    $installationPath = & $vsWherePath -latest -products * -property installationPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installationPath))
    {
        return $null
    }

    $vsDevCmdPath = Join-Path $installationPath.Trim() 'Common7\Tools\VsDevCmd.bat'
    if (-not (Test-Path $vsDevCmdPath))
    {
        return $null
    }

    return $vsDevCmdPath
}

function Get-WindowsTargetArchitecture
{
    param([Parameter(Mandatory)][string]$Rid)

    $architecture = $Rid.Split('-', 2)[1]
    switch ($architecture)
    {
        'x64' { 'amd64' }
        'arm64' { 'arm64' }
        default { throw "Unsupported Windows architecture '$Rid'." }
    }
}

function Get-RequiredPayloadFileNames
{
    param(
        [Parameter(Mandatory)][string]$RuntimeIdentifier,
        [Parameter(Mandatory)][string]$BinaryName
    )

    $platform = $RuntimeIdentifier.Split('-', 2)[0]
    switch ($platform)
    {
        'win'
        {
            $architecture = $RuntimeIdentifier.Split('-', 2)[1]
            $msalRuntime = switch ($architecture)
            {
                'x64' { 'msalruntime.dll' }
                'arm64' { 'msalruntime_arm64.dll' }
                default { throw "Unsupported Windows architecture in runtime identifier '$RuntimeIdentifier'." }
            }

            return @(
                $BinaryName,
                'libSkiaSharp.dll',
                'libHarfBuzzSharp.dll',
                $msalRuntime,
                'libsodium.dll',
                'LICENSE',
                'THIRD-PARTY-NOTICES.md',
                'payload-manifest.json'
            )
        }
        'linux'
        {
            return @(
                $BinaryName,
                'libSkiaSharp.so',
                'libHarfBuzzSharp.so',
                'libsodium.so',
                'LICENSE',
                'THIRD-PARTY-NOTICES.md',
                'payload-manifest.json'
            )
        }
        'osx'
        {
            return @(
                $BinaryName,
                'libSkiaSharp.dylib',
                'libHarfBuzzSharp.dylib',
                'libsodium.dylib',
                'LICENSE',
                'THIRD-PARTY-NOTICES.md',
                'payload-manifest.json'
            )
        }
        default
        {
            throw "Unsupported platform '$platform' in runtime identifier '$RuntimeIdentifier'."
        }
    }
}

function Assert-StagedPayloadComplete
{
    param(
        [Parameter(Mandatory)][string]$StagingDirectory,
        [Parameter(Mandatory)][string]$RuntimeIdentifier,
        [Parameter(Mandatory)][string]$BinaryName
    )

    $required = Get-RequiredPayloadFileNames -RuntimeIdentifier $RuntimeIdentifier -BinaryName $BinaryName
    $stagedFileNames = (Get-ChildItem -Path $StagingDirectory -File -Recurse | ForEach-Object { $_.Name }) | Sort-Object -Unique
    $missing = @($required | Where-Object { $_ -notin $stagedFileNames })

    if ($missing.Count -gt 0)
    {
        $stagedListing = (Get-ChildItem -Path $StagingDirectory -File -Recurse | ForEach-Object { $_.FullName.Substring($StagingDirectory.Length).TrimStart('\','/') }) -join "`n  "
        throw "Staged payload for runtime '$RuntimeIdentifier' is missing required file(s): $($missing -join ', '). Staged contents:`n  $stagedListing"
    }
}

function Invoke-DotNetPublish
{
    param(
        [Parameter(Mandatory)][string]$Rid,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $platform = $Rid.Split('-', 2)[0]
    if ($IsWindows -and $platform -eq 'win')
    {
        $vsDevCmdPath = Get-VsDevCmdPath
        if (-not [string]::IsNullOrWhiteSpace($vsDevCmdPath))
        {
            $targetArchitecture = Get-WindowsTargetArchitecture -Rid $Rid
            $vsWhereDirectory = Split-Path -Parent (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe')
            $commandSegments = @(
                'set',
                (Quote-CmdArgument -Value "PATH=$vsWhereDirectory;%PATH%"),
                '&&',
                'call',
                (Quote-CmdArgument -Value $vsDevCmdPath),
                '-no_logo',
                '-host_arch=amd64',
                "-arch=$targetArchitecture",
                '&&',
                'dotnet'
            ) + ($Arguments | ForEach-Object { Quote-CmdArgument -Value $_ })

            & cmd.exe /d /c ($commandSegments -join ' ')
            return
        }
    }

    & dotnet @Arguments
}

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

try
{
    $publishArguments = @(
        'publish',
        $projectPath,
        '--configuration', $Configuration,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--nologo',
        '--tl:off',
        "-p:Version=$Version",
        '-p:ContinuousIntegrationBuild=true',
        '-o', $publishDirectory
    )

    Invoke-DotNetPublish -Rid $RuntimeIdentifier -Arguments $publishArguments

    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet publish failed for runtime '$RuntimeIdentifier'. Ensure the required NativeAOT toolchain is available for this platform."
    }

    $parts = $RuntimeIdentifier.Split('-', 2)
    if ($parts.Length -ne 2)
    {
        throw "Unexpected runtime identifier format '$RuntimeIdentifier'."
    }

    $platform = $parts[0]
    $architecture = $parts[1]
    $binaryName = if ($platform -eq 'win') { 'kusto.exe' } else { 'kusto' }
    $binaryPath = Join-Path $publishDirectory $binaryName

    if (-not (Test-Path $binaryPath))
    {
        throw "Published binary '$binaryPath' was not found."
    }

    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

    # Copy the entire publish payload (not just the binary) so native sidecars
    # required by SkiaSharp/HarfBuzz at runtime are included in the release
    # archive. Skip debug symbols and IDE/build artifacts that aren't needed
    # at runtime — these would only inflate the archive.
    $excludePatterns = @('*.pdb', '*.xml', '*.deps.json.map', '*.dbg', '*.dwarf')
    $payloadFiles = Get-ChildItem -Path $publishDirectory -File -Recurse |
        Where-Object {
            $relative = $_.FullName.Substring($publishDirectory.Length).TrimStart('\','/')
            foreach ($pattern in $excludePatterns)
            {
                if ($_.Name -like $pattern) { return $false }
            }
            return $true
        }

    foreach ($file in $payloadFiles)
    {
        $relative = $file.FullName.Substring($publishDirectory.Length).TrimStart('\','/')
        $destination = Join-Path $stagingDirectory $relative
        $destinationDir = Split-Path -Parent $destination
        if (-not [string]::IsNullOrEmpty($destinationDir) -and -not (Test-Path $destinationDir))
        {
            New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
        }

        Copy-Item $file.FullName $destination -Force
    }

    Copy-Item (Join-Path $repoRoot 'LICENSE') (Join-Path $stagingDirectory 'LICENSE') -Force

    $thirdPartyNoticesPath = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md'
    if (-not (Test-Path $thirdPartyNoticesPath))
    {
        throw "Required notices file '$thirdPartyNoticesPath' was not found."
    }
    Copy-Item $thirdPartyNoticesPath (Join-Path $stagingDirectory 'THIRD-PARTY-NOTICES.md') -Force

    # The manifest intentionally does not list itself. Every other path is
    # install-relative and uses '/' so all platforms consume the same schema.
    $manifestFileName = 'payload-manifest.json'
    [string[]]$manifestFiles = @(
        Get-ChildItem -Path $stagingDirectory -File -Recurse |
            ForEach-Object {
                [System.IO.Path]::GetRelativePath($stagingDirectory, $_.FullName).Replace('\', '/')
            }
    )
    if ($manifestFiles.Count -eq 0)
    {
        throw "Cannot generate '$manifestFileName' for an empty staged payload."
    }
    [System.Array]::Sort($manifestFiles, [System.StringComparer]::Ordinal)
    [ordered]@{ files = $manifestFiles } |
        ConvertTo-Json -Depth 3 |
        Set-Content -Path (Join-Path $stagingDirectory $manifestFileName) -Encoding utf8

    # Assert the staged payload contains every file users need at runtime. If
    # ScottPlot/SkiaSharp ever drops a backend, restructures its native packaging,
    # or stops publishing native assets for a RID, the missing-file failure here
    # will fail the build instead of producing a broken archive that explodes
    # with DllNotFoundException at first chart render.
    Assert-StagedPayloadComplete -StagingDirectory $stagingDirectory -RuntimeIdentifier $RuntimeIdentifier -BinaryName $binaryName

    $assetPath =
        if ($platform -eq 'win')
        {
            $path = Join-Path $artifactsDirectory "kusto-$RuntimeIdentifier.zip"
            if (Test-Path $path)
            {
                Remove-Item $path -Force
            }

            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [System.IO.Compression.ZipFile]::CreateFromDirectory($stagingDirectory, $path)
            $path
        }
        else
        {
            $path = Join-Path $artifactsDirectory "kusto-$RuntimeIdentifier.tar.gz"
            if (Test-Path $path)
            {
                Remove-Item $path -Force
            }

            tar -czf $path -C $stagingDirectory .
            if ($LASTEXITCODE -ne 0)
            {
                throw "Failed to create archive '$path'."
            }

            $path
        }

    $hash = (Get-FileHash $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $assetName = [System.IO.Path]::GetFileName($assetPath)
    $hashPath = Join-Path $artifactsDirectory ($assetName + '.sha256')
    "$hash  $assetName" | Set-Content -Path $hashPath -NoNewline

    $metadata = [ordered]@{
        version = $Version
        runtimeIdentifier = $RuntimeIdentifier
        platform = $platform
        architecture = $architecture
        assetName = $assetName
        fileType = if ($platform -eq 'win') { 'zip' } else { 'tar.gz' }
        commandName = 'kusto'
        sha256 = $hash
        sourceCommit = if ([string]::IsNullOrWhiteSpace($env:GITHUB_SHA)) { $null } else { $env:GITHUB_SHA }
    }

    $metadataPath = Join-Path $artifactsDirectory ("kusto-$RuntimeIdentifier.json")
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -Path $metadataPath
}
finally
{
    if (Test-Path $publishRoot)
    {
        Remove-Item $publishRoot -Recurse -Force
    }
}
