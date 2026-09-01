[CmdletBinding()]
param(
    [ValidateSet('Dev', 'PreRelease', 'Stable')]
    [string]$Quality = 'Stable',

    [switch]$Force,

    [string]$TargetPath = (Join-Path $env:USERPROFILE '.kusto\bin'),

    [bool]$UpdatePath = $true,

    [string]$Repository = 'DamianEdwards/kusto-cli',

    [string]$ExpectedSignerSubject = 'CN=Damian Edwards, O=Damian Edwards, L=Issaquah, S=Washington, C=US',

    [Parameter(DontShow = $true)]
    [switch]$NoExecute
)

$ErrorActionPreference = 'Stop'

# Keep this single-sourced here; the release workflow reads these values from the installer script.
$ExpectedSignerIssuerSha512Thumbprints = @(
    '1c93dcf4e032b19949a67722d0c25e683309fbcd36110da84129f45d8175b709ebc6ef3439596ece9eb8f2dae1967b856adc49ba74535244a8a5db5fb48fa7b9'
    '1770433e5d2c028e0bf8640a0345bdb86307e7cc2a99cfbe93acf9d960a996d1c63b2d5cf30d52e7741df4fd057ea778442f75c1b62ee2106c66333078a04e6d'
)
$ExpectedSignerParentIssuerSha512Thumbprints = @(
    '46f16bb99340f8d728c83ff093af9d4cff87811d432f92a804741144f0f3fc0aa8011b1efe0c24e0480bd6c7cb7af699077f9b8fc7ec8a40f9f7a186725224c6'
)

$runningOnWindows = if (Get-Variable -Name IsWindows -ErrorAction SilentlyContinue)
{
    [bool]$IsWindows
}
else
{
    [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
}

if (-not $runningOnWindows)
{
    $scriptName = if ($MyInvocation.MyCommand.Name) { $MyInvocation.MyCommand.Name } else { 'install-kusto-cli.ps1' }
    Write-Error "$scriptName currently supports Windows only. Running on '$([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)' is not yet supported."
    exit 1
}

function Normalize-PathEntry
{
    param([Parameter(Mandatory)][string]$PathEntry)

    return $PathEntry.Trim().TrimEnd('\').ToLowerInvariant()
}

function Ensure-PathContains
{
    param(
        [Parameter(Mandatory)][string]$Entry,
        [Parameter(Mandatory)][System.EnvironmentVariableTarget]$Target
    )

    $current = [System.Environment]::GetEnvironmentVariable('PATH', $Target)
    $existingEntries = @()
    if (-not [string]::IsNullOrWhiteSpace($current))
    {
        $existingEntries = @($current.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries))
    }

    $normalizedTarget = Normalize-PathEntry -PathEntry $Entry
    $alreadyPresent = $existingEntries | Where-Object { (Normalize-PathEntry -PathEntry $_) -eq $normalizedTarget } | Select-Object -First 1
    if ($alreadyPresent)
    {
        return $false
    }

    $updated = if ([string]::IsNullOrWhiteSpace($current))
    {
        $Entry
    }
    else
    {
        $current.TrimEnd(';') + ';' + $Entry
    }

    [System.Environment]::SetEnvironmentVariable('PATH', $updated, $Target)
    return $true
}

function Get-PowerShellCompletionProfilePath
{
    if ($PROFILE -is [string])
    {
        return $PROFILE
    }

    if ($PROFILE.PSObject.Properties.Name -contains 'CurrentUserAllHosts' -and -not [string]::IsNullOrWhiteSpace($PROFILE.CurrentUserAllHosts))
    {
        return $PROFILE.CurrentUserAllHosts
    }

    return [string]$PROFILE
}

function Get-NativeCommandOutput
{
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList)
    {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process)
    {
        throw "Failed to start '$FilePath'."
    }

    try
    {
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($standardOutput))
        {
            $message = if ([string]::IsNullOrWhiteSpace($standardError)) { "Command exited with code $($process.ExitCode)." } else { $standardError.Trim() }
            throw "Failed to generate completion script: $message"
        }
        return $standardOutput
    }
    finally
    {
        $process.Dispose()
    }
}

function Try-EnablePowerShellCompletion
{
    param([Parameter(Mandatory)][string]$CommandPath)

    $registerCommand = Get-Command Register-ArgumentCompleter -ErrorAction SilentlyContinue
    if ($null -eq $registerCommand -or -not $registerCommand.Parameters.ContainsKey('Native'))
    {
        return [pscustomobject]@{ Status = 'Unavailable'; Message = 'PowerShell native completion is not supported by this shell.' }
    }

    try
    {
        $profilePath = Get-PowerShellCompletionProfilePath
        $scriptPath = Join-Path (Split-Path -Parent $CommandPath) 'kusto-completions.ps1'
        $scriptContent = Get-NativeCommandOutput -FilePath $CommandPath -ArgumentList @('completions', 'script', 'pwsh')
        Set-Content -Path $scriptPath -Value $scriptContent -Encoding utf8
        $profileUpdated = $false
        $profileContent = if (Test-Path $profilePath) { Get-Content -Path $profilePath -Raw } else { '' }
        if (-not $profileContent.Contains($scriptPath))
        {
            $profileDirectory = Split-Path -Parent $profilePath
            if (-not [string]::IsNullOrWhiteSpace($profileDirectory))
            {
                New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null
            }
            Add-Content -Path $profilePath -Value ''
            Add-Content -Path $profilePath -Value '# Added by the kusto installer for shell completion'
            Add-Content -Path $profilePath -Value ". '$scriptPath'"
            $profileUpdated = $true
        }

        return [pscustomobject]@{
            Status = 'Enabled'
            ProfilePath = $profilePath
            ProfileUpdated = $profileUpdated
            ScriptPath = $scriptPath
            Message = $null
        }
    }
    catch
    {
        return [pscustomobject]@{ Status = 'Failed'; Message = $_.Exception.Message }
    }
}

function Get-HttpStatusCode
{
    param([Parameter(Mandatory)][System.Exception]$Exception)

    $responseProperty = $Exception.PSObject.Properties['Response']
    if ($null -eq $responseProperty -or $null -eq $Exception.Response)
    {
        return $null
    }

    try
    {
        return [int]$Exception.Response.StatusCode
    }
    catch
    {
        return $null
    }
}

function Invoke-GitHubApi
{
    param([Parameter(Mandatory)][string]$Uri)

    $token = if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN))
    {
        $env:GH_TOKEN
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN))
    {
        $env:GITHUB_TOKEN
    }
    else
    {
        $null
    }

    $headers = @{
        Accept       = 'application/vnd.github+json'
        'User-Agent' = 'kusto-cli-install-script'
    }
    if ($token)
    {
        $headers.Authorization = "Bearer $token"
    }

    Invoke-RestMethod -Method Get -Uri $Uri -Headers $headers
}

function Invoke-GitHubAssetDownload
{
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$OutFile
    )

    $token = if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN))
    {
        $env:GH_TOKEN
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN))
    {
        $env:GITHUB_TOKEN
    }
    else
    {
        $null
    }

    $headers = @{
        Accept       = 'application/octet-stream'
        'User-Agent' = 'kusto-cli-install-script'
    }
    if ($token)
    {
        $headers.Authorization = "Bearer $token"
    }

    Invoke-WebRequest -Uri $Uri -Headers $headers -OutFile $OutFile
}

function Get-ReleaseByTag
{
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Tag
    )

    $tagUri = "https://api.github.com/repos/$Repo/releases/tags/$Tag"
    try
    {
        return Invoke-GitHubApi -Uri $tagUri
    }
    catch
    {
        if ((Get-HttpStatusCode -Exception $_.Exception) -eq 404)
        {
            return $null
        }

        throw
    }
}

function Get-ReleaseAsset
{
    param(
        [Parameter(Mandatory)]$Release,
        [Parameter(Mandatory)][string]$AssetName
    )

    return $Release.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
}

function Get-ReleaseAssetDownloadUri
{
    param([Parameter(Mandatory)]$Asset)

    if (-not [string]::IsNullOrWhiteSpace($Asset.url))
    {
        return $Asset.url
    }

    return $Asset.browser_download_url
}

function Get-WindowsArchitecture
{
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    switch ($arch)
    {
        ([System.Runtime.InteropServices.Architecture]::X64) { return 'x64' }
        ([System.Runtime.InteropServices.Architecture]::Arm64) { return 'arm64' }
        default { throw "Unsupported Windows architecture '$arch'. Only x64 and arm64 are supported." }
    }
}

function Get-CompatibleRelativePath
{
    param(
        [Parameter(Mandatory)][string]$BasePath,
        [Parameter(Mandatory)][string]$TargetPath
    )

    $baseFullPath = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $targetFullPath = [System.IO.Path]::GetFullPath($TargetPath)
    $baseUri = [System.Uri]::new($baseFullPath)
    $targetUri = [System.Uri]::new($targetFullPath)
    $relativePath = [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString())
    return $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
}

function Get-ReleaseForQuality
{
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$SelectedQuality,
        [Parameter(Mandatory)][string]$AssetName
    )

    $allReleases = @(Invoke-GitHubApi -Uri "https://api.github.com/repos/$Repo/releases?per_page=100" | ForEach-Object { $_ })
    $bestRelease = $null
    $bestVersion = $null
    $legacyDevRelease = $null

    foreach ($release in $allReleases)
    {
        if ($release.draft)
        {
            continue
        }

        if ($release.tag_name -eq 'dev')
        {
            if ($SelectedQuality -eq 'Dev' -and $null -ne (Get-ReleaseAsset -Release $release -AssetName $AssetName))
            {
                $legacyDevRelease = $release
            }
            continue
        }

        if ($release.tag_name -eq 'install-scripts' -or $release.tag_name -like 'install-scripts-v*')
        {
            continue
        }

        $asset = Get-ReleaseAsset -Release $release -AssetName $AssetName
        if ($null -eq $asset)
        {
            continue
        }

        $isDevRelease = $release.tag_name -match '(^v?.*-dev\.)|(\.dev\.)'
        $matchesQuality = switch ($SelectedQuality)
        {
            'Dev' { $release.prerelease -and $isDevRelease }
            'PreRelease' { -not $isDevRelease }
            'Stable' { -not $release.prerelease }
            default { $false }
        }
        if (-not $matchesQuality)
        {
            continue
        }

        $candidateVersion = $release.tag_name.TrimStart('v')
        try
        {
            $null = Parse-SemanticVersion -Value $candidateVersion
        }
        catch
        {
            Write-Verbose "Skipping release '$($release.tag_name)' because its tag is not SemVer."
            continue
        }

        if ($null -eq $bestRelease -or (Compare-SemanticVersion -Left $candidateVersion -Right $bestVersion) -gt 0)
        {
            $bestRelease = $release
            $bestVersion = $candidateVersion
        }
    }

    if ($null -ne $bestRelease)
    {
        return $bestRelease
    }

    if ($SelectedQuality -eq 'Dev' -and $null -ne $legacyDevRelease)
    {
        Write-Verbose "No immutable SemVer development release was found; using the legacy standing 'dev' release."
        return $legacyDevRelease
    }

    throw "No '$SelectedQuality' release containing '$AssetName' was found in '$Repo'."
}

function Get-ExpectedSha256
{
    param(
        [Parameter(Mandatory)][string]$ChecksumsPath,
        [Parameter(Mandatory)][string]$AssetName
    )

    $line = @(Get-Content -Path $ChecksumsPath | Where-Object { $_ -match "\s\*?$([regex]::Escape($AssetName))$" } | Select-Object -First 1)
    if ($line.Count -eq 0)
    {
        throw "checksums.txt did not contain an entry for '$AssetName'."
    }

    $match = [regex]::Match($line[0], '^\s*([0-9a-fA-F]{64})\s+\*?.+$')
    if (-not $match.Success)
    {
        throw "Invalid checksum line format for '$AssetName' in checksums.txt."
    }

    return $match.Groups[1].Value.ToLowerInvariant()
}

function Get-KustoInstallerTrustConfiguration
{
    return [pscustomobject]@{
        ExpectedSignerSubject = $ExpectedSignerSubject
        ExpectedSignerIssuerSha512Thumbprints = @($ExpectedSignerIssuerSha512Thumbprints)
        ExpectedSignerParentIssuerSha512Thumbprints = @($ExpectedSignerParentIssuerSha512Thumbprints)
        # Expected executable payload files inside a Windows release archive. Every
        # file listed here must be present after extraction, must be Authenticode-signed
        # by the configured signer, and is also tracked by the installer so that
        # upgrades can clean up sidecars that future versions remove. Update this list
        # alongside any change to the publish/signing pipeline (see
        # scripts/Publish-NativeAsset.ps1).
        ExpectedExecutablePayloadFiles = @(
            'kusto.exe',
            'libSkiaSharp.dll',
            'libHarfBuzzSharp.dll',
            'libsodium.dll'
        )
        # Non-executable payload files that should be carried alongside the binaries.
        # These don't need signing but the installer should still keep them in sync.
        ExpectedAuxiliaryPayloadFiles = @(
            'LICENSE',
            'THIRD-PARTY-NOTICES.md',
            'payload-manifest.json'
        )
    }
}

#region SharedProvenanceFunctions

function Get-CertificateSha512Thumbprint
{
    param([Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

    $sha512 = [System.Security.Cryptography.SHA512]::Create()
    try
    {
        $hashBytes = $sha512.ComputeHash($Certificate.RawData)
        return ([System.BitConverter]::ToString($hashBytes)).Replace('-', '').ToLowerInvariant()
    }
    finally
    {
        $sha512.Dispose()
    }
}

function Get-ChainStatusMessages
{
    param([Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Chain]$Chain)

    return @($Chain.ChainStatus |
            Where-Object { $_.Status -ne [System.Security.Cryptography.X509Certificates.X509ChainStatusFlags]::NoError } |
            ForEach-Object {
                $statusText = $_.Status.ToString()
                $infoText = $_.StatusInformation.Trim()
                if ([string]::IsNullOrWhiteSpace($infoText))
                {
                    $statusText
                }
                else
                {
                    "$statusText ($infoText)"
                }
            })
}

function Write-CertificateDetailsVerbose
{
    param(
        [Parameter(Mandatory)][string]$Description,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    if ($null -eq $Certificate)
    {
        Write-Verbose "$Description certificate: <null>"
        return
    }

    Write-Verbose ("{0} certificate: Subject='{1}', Issuer='{2}', Thumbprint='{3}', NotBefore='{4:O}', NotAfter='{5:O}'" -f `
            $Description, `
            $Certificate.Subject, `
            $Certificate.Issuer, `
            $Certificate.Thumbprint, `
            $Certificate.NotBefore, `
            $Certificate.NotAfter)
}

function Write-CertificateChainVerbose
{
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Chain]$Chain,
        [Parameter(Mandatory)][string]$Description
    )

    Write-Verbose "$Description certificate chain contains $($Chain.ChainElements.Count) element(s)."

    for ($index = 0; $index -lt $Chain.ChainElements.Count; $index++)
    {
        Write-CertificateDetailsVerbose -Description "$Description chain[$index]" -Certificate $Chain.ChainElements[$index].Certificate
    }

    $statuses = Get-ChainStatusMessages -Chain $Chain
    if ($statuses.Count -eq 0)
    {
        Write-Verbose "$Description certificate chain status: NoError."
        return
    }

    foreach ($status in $statuses)
    {
        Write-Verbose "$Description certificate chain status: $status"
    }
}

function Assert-WindowsArchiveIntegrity
{
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$AssetName,
        [Parameter(Mandatory)][string]$ChecksumsPath,
        [string]$ReleaseMetadataPath
    )

    Write-Verbose "Validating archive SHA256 for '$AssetName' using '$ChecksumsPath'."
    $expectedSha = Get-ExpectedSha256 -ChecksumsPath $ChecksumsPath -AssetName $AssetName
    $actualSha = (Get-FileHash -Path $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Verbose "checksums.txt expected SHA256 for '$AssetName': '$expectedSha'."
    Write-Verbose "Actual SHA256 for '$ArchivePath': '$actualSha'."

    if ($expectedSha -ne $actualSha)
    {
        throw "SHA256 mismatch for '$AssetName'. Expected '$expectedSha' but got '$actualSha'."
    }

    if (-not [string]::IsNullOrWhiteSpace($ReleaseMetadataPath))
    {
        Write-Verbose "Validating release metadata for '$AssetName' using '$ReleaseMetadataPath'."
        $metadata = Get-Content -Path $ReleaseMetadataPath -Raw | ConvertFrom-Json
        $metadataAsset = $metadata.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
        if ($null -eq $metadataAsset)
        {
            throw "release-metadata.json did not contain asset '$AssetName'."
        }

        $metadataSha = $metadataAsset.sha256.ToLowerInvariant()
        Write-Verbose "release-metadata.json SHA256 for '$AssetName': '$metadataSha'."
        if ($metadataSha -ne $expectedSha)
        {
            throw "release-metadata.json SHA256 for '$AssetName' did not match checksums.txt."
        }
    }

    return $actualSha
}

function Expand-WindowsReleaseArchive
{
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$DestinationPath
    )

    Write-Verbose "Expanding '$ArchivePath' to '$DestinationPath'."
    Expand-Archive -Path $ArchivePath -DestinationPath $DestinationPath -Force

    $binaryPath = Join-Path $DestinationPath 'kusto.exe'
    if (-not (Test-Path $binaryPath))
    {
        throw "Downloaded archive '$([System.IO.Path]::GetFileName($ArchivePath))' did not contain 'kusto.exe'."
    }

    Write-Verbose "Found extracted Windows binary '$binaryPath'."
    return [pscustomobject]@{
        BinaryPath = $binaryPath
        ExtractDirectory = $DestinationPath
    }
}

function Assert-ExtractedPayloadComplete
{
    param(
        [Parameter(Mandatory)][string]$ExtractDirectory,
        [Parameter(Mandatory)][string[]]$RequiredFileNames
    )

    $present = @(Get-ChildItem -Path $ExtractDirectory -File | ForEach-Object { $_.Name })
    $missing = @($RequiredFileNames | Where-Object { $_ -notin $present })
    if ($missing.Count -gt 0)
    {
        $listing = ($present | Sort-Object) -join ', '
        throw "Extracted archive at '$ExtractDirectory' is missing required file(s): $($missing -join ', '). Found: $listing."
    }
}

function Assert-PayloadManifestMatches
{
    param([Parameter(Mandatory)][string]$ExtractDirectory)

    $manifestName = 'payload-manifest.json'
    $manifestPath = Join-Path $ExtractDirectory $manifestName
    if (-not (Test-Path -LiteralPath $manifestPath))
    {
        throw "Extracted archive is missing required '$manifestName'."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest -isnot [pscustomobject] -or
        $manifest.PSObject.Properties.Name -notcontains 'files' -or
        $manifest.files -isnot [array])
    {
        throw "'$manifestName' must be a JSON object with a 'files' array."
    }
    $entries = @($manifest.files)
    if ($entries.Count -eq 0)
    {
        throw "'$manifestName' did not declare any payload files."
    }

    $root = [System.IO.Path]::GetFullPath($ExtractDirectory).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $declared = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $previousEntry = $null
    foreach ($entry in $entries)
    {
        if ($entry -isnot [string] -or [string]::IsNullOrWhiteSpace($entry) -or [System.IO.Path]::IsPathRooted($entry))
        {
            throw "'$manifestName' contains invalid install-relative path '$entry'."
        }
        if ($entry.Contains('\'))
        {
            throw "'$manifestName' path '$entry' is not slash-normalized."
        }
        if ($null -ne $previousEntry -and [System.StringComparer]::Ordinal.Compare($previousEntry, $entry) -gt 0)
        {
            throw "'$manifestName' paths must be sorted using ordinal comparison."
        }
        $previousEntry = $entry

        $normalized = $entry.Replace('/', [System.IO.Path]::DirectorySeparatorChar).Replace('\', [System.IO.Path]::DirectorySeparatorChar)
        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $ExtractDirectory $normalized))
        if (-not $fullPath.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase))
        {
            throw "'$manifestName' contains path outside the payload: '$entry'."
        }

        $relativePath = Get-CompatibleRelativePath -BasePath $ExtractDirectory -TargetPath $fullPath
        if ($entry -cne $relativePath.Replace('\', '/'))
        {
            throw "'$manifestName' path '$entry' is not normalized."
        }
        if (-not $declared.Add($relativePath))
        {
            throw "'$manifestName' contains duplicate path '$entry'."
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf))
        {
            throw "Extracted archive is missing file '$entry' declared by '$manifestName'."
        }
    }

    $actual = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    Get-ChildItem -Path $ExtractDirectory -File -Recurse |
        ForEach-Object {
            $relativePath = Get-CompatibleRelativePath -BasePath $ExtractDirectory -TargetPath $_.FullName
            if ($relativePath -ne $manifestName)
            {
                $null = $actual.Add($relativePath)
            }
        }
    if (-not $declared.SetEquals($actual))
    {
        throw "'$manifestName' does not exactly describe the extracted payload; the manifest itself must be excluded."
    }

    return @($declared | Sort-Object)
}

function Get-InstalledManifestPayloadFiles
{
    param([Parameter(Mandatory)][string]$InstallDirectory)

    $manifestPath = Join-Path $InstallDirectory 'payload-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath))
    {
        return @()
    }

    try
    {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest -isnot [pscustomobject] -or
            $manifest.PSObject.Properties.Name -notcontains 'files' -or
            $manifest.files -isnot [array])
        {
            throw 'Installed payload manifest must be a JSON object with a files array.'
        }
        $entries = @($manifest.files)
        $root = [System.IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        $validated = @(
            foreach ($entry in $entries)
            {
                if ($entry -isnot [string] -or
                    [string]::IsNullOrWhiteSpace($entry) -or
                    [System.IO.Path]::IsPathRooted($entry) -or
                    $entry.Contains('\'))
                {
                    throw "Installed payload manifest contains an invalid path."
                }
                $normalized = $entry.Replace('/', [System.IO.Path]::DirectorySeparatorChar).Replace('\', [System.IO.Path]::DirectorySeparatorChar)
                $fullPath = [System.IO.Path]::GetFullPath((Join-Path $InstallDirectory $normalized))
                if (-not $fullPath.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase))
                {
                    throw "Installed payload manifest contains a path outside the installation."
                }
                $relativePath = Get-CompatibleRelativePath -BasePath $InstallDirectory -TargetPath $fullPath
                if ($entry -cne $relativePath.Replace('\', '/'))
                {
                    throw 'Installed payload manifest contains a non-normalized path.'
                }
                $relativePath
            }
        )
        return $validated
    }
    catch
    {
        Write-Verbose "Ignoring an invalid installed payload manifest during stale-file cleanup. $($_.Exception.Message)"
        return @()
    }
}

function Install-KustoPayload
{
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$InstallDirectory,
        [string[]]$KnownPayloadFileNames = @()
    )

    $installRoot = [System.IO.Path]::GetFullPath($InstallDirectory)
    $stagingDir = $installRoot + '.new'
    $previousDir = $installRoot + '.old'
    $managedPayloadFileNames = @(
        $KnownPayloadFileNames
        Get-InstalledManifestPayloadFiles -InstallDirectory $installRoot
        'payload-manifest.json'
    ) | Sort-Object -Unique

    if (Test-Path $stagingDir)
    {
        Remove-Item $stagingDir -Recurse -Force
    }
    if (Test-Path $previousDir)
    {
        Remove-Item $previousDir -Recurse -Force
    }

    # Stage: copy the entire extracted payload into installRoot.new
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    Copy-Item -Path (Join-Path $SourceDirectory '*') -Destination $stagingDir -Recurse -Force

    New-Item -ItemType Directory -Path $installRoot, $previousDir -Force | Out-Null
    $mutationStarted = $false
    try
    {
        foreach ($relativePath in $managedPayloadFileNames)
        {
            $installedPath = Join-Path $installRoot $relativePath
            if (-not (Test-Path -LiteralPath $installedPath -PathType Leaf))
            {
                continue
            }

            $backupPath = Join-Path $previousDir $relativePath
            New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force | Out-Null
            Copy-Item -LiteralPath $installedPath -Destination $backupPath -Force
        }

        $mutationStarted = $true
        foreach ($relativePath in $managedPayloadFileNames)
        {
            Remove-Item -LiteralPath (Join-Path $installRoot $relativePath) -Force -ErrorAction SilentlyContinue
        }

        foreach ($stagedFile in Get-ChildItem -Path $stagingDir -File -Recurse)
        {
            $relativePath = Get-CompatibleRelativePath -BasePath $stagingDir -TargetPath $stagedFile.FullName
            $destinationPath = Join-Path $installRoot $relativePath
            New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
            Copy-Item -LiteralPath $stagedFile.FullName -Destination $destinationPath -Force
        }
    }
    catch
    {
        if ($mutationStarted)
        {
            foreach ($relativePath in $managedPayloadFileNames)
            {
                Remove-Item -LiteralPath (Join-Path $installRoot $relativePath) -Force -ErrorAction SilentlyContinue
            }

            foreach ($backupFile in Get-ChildItem -Path $previousDir -File -Recurse)
            {
                $relativePath = Get-CompatibleRelativePath -BasePath $previousDir -TargetPath $backupFile.FullName
                $destinationPath = Join-Path $installRoot $relativePath
                New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
                Copy-Item -LiteralPath $backupFile.FullName -Destination $destinationPath -Force
            }
        }

        throw
    }
    finally
    {
        Remove-Item $stagingDir, $previousDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-StatusStep
{
    param(
        [Parameter(Mandatory)][string]$Message,
        [Parameter(Mandatory)][scriptblock]$Action
    )

    Write-Host "$Message... " -NoNewline
    & $Action
    Write-Host 'done'
}

function Get-ReleaseStatusLabel
{
    param(
        [Parameter(Mandatory)][string]$SelectedQuality,
        [Parameter(Mandatory)]$Release
    )

    $releaseLabel =
        switch ($SelectedQuality)
        {
            'Stable' { 'latest stable release' }
            'PreRelease' { 'latest prerelease' }
            'Dev' { 'latest development build' }
            default { 'release' }
        }

    $releaseVersion = if (-not [string]::IsNullOrWhiteSpace($Release.tag_name))
    {
        $Release.tag_name
    }
    elseif (-not [string]::IsNullOrWhiteSpace($Release.name))
    {
        $Release.name
    }
    else
    {
        'unknown version'
    }

    return "$releaseLabel ($releaseVersion)"
}

function Get-ValidatedCertificateChain
{
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)][string]$Description,
        [switch]$IgnoreTimeValidity
    )

    Write-CertificateDetailsVerbose -Description $Description -Certificate $Certificate
    Write-Verbose "Building $Description certificate chain. IgnoreTimeValidity=$IgnoreTimeValidity."

    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
    $chain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::EntireChain
    $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(15)
    $chain.ChainPolicy.VerificationFlags = if ($IgnoreTimeValidity)
    {
        [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::IgnoreNotTimeValid
    }
    else
    {
        [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
    }

    $ok = $chain.Build($Certificate)
    if ($ok)
    {
        Write-CertificateChainVerbose -Chain $chain -Description $Description
        return $chain
    }

    $statuses = Get-ChainStatusMessages -Chain $chain
    Write-CertificateChainVerbose -Chain $chain -Description $Description
    $statusMessage = if ($statuses.Count -gt 0)
    {
        $statuses -join '; '
    }
    else
    {
        'unknown chain validation failure'
    }

    throw "$Description certificate chain validation failed: $statusMessage"
}

function Get-ImmediateIssuerCertificate
{
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Chain]$Chain,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Chain.ChainElements.Count -lt 2)
    {
        throw "$Description certificate chain did not include an issuing certificate."
    }

    $issuerCertificate = $Chain.ChainElements[1].Certificate
    if ($null -eq $issuerCertificate)
    {
        throw "$Description certificate chain did not provide an issuing certificate."
    }

    Write-CertificateDetailsVerbose -Description "$Description issuer" -Certificate $issuerCertificate
    return $issuerCertificate
}

function Get-ParentIntermediateIssuerCertificate
{
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Chain]$Chain,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Chain.ChainElements.Count -lt 3)
    {
        Write-Verbose "$Description certificate chain did not include a parent issuer fallback candidate."
        return $null
    }

    $parentIndex = 2
    $parentCertificate = $Chain.ChainElements[$parentIndex].Certificate
    if ($null -eq $parentCertificate)
    {
        Write-Verbose "$Description certificate chain did not provide a parent issuer fallback candidate."
        return $null
    }

    $isRootCandidate = ($parentIndex -eq ($Chain.ChainElements.Count - 1)) -or
        [string]::Equals($parentCertificate.Subject, $parentCertificate.Issuer, [System.StringComparison]::OrdinalIgnoreCase)
    if ($isRootCandidate)
    {
        Write-Verbose "$Description parent issuer fallback candidate resolved to a root certificate. Root certificates are never used for issuer fallback."
        return $null
    }

    Write-CertificateDetailsVerbose -Description "$Description parent issuer" -Certificate $parentCertificate
    return $parentCertificate
}

function Normalize-DistinguishedNameKey
{
    param([Parameter(Mandatory)][string]$Key)

    $normalized = $Key.Trim().ToUpperInvariant()
    if ($normalized -eq 'ST')
    {
        return 'S'
    }

    return $normalized
}

function Parse-DistinguishedName
{
    param([Parameter(Mandatory)][string]$DistinguishedName)

    $result = @{}
    foreach ($segment in $DistinguishedName -split ',')
    {
        $match = [regex]::Match($segment, '^\s*([^=]+?)\s*=\s*(.+?)\s*$')
        if (-not $match.Success)
        {
            throw "Could not parse distinguished name segment '$segment' from '$DistinguishedName'."
        }

        $key = Normalize-DistinguishedNameKey -Key $match.Groups[1].Value
        $value = $match.Groups[2].Value.Trim()
        if ([string]::IsNullOrWhiteSpace($value))
        {
            throw "Distinguished name key '$key' in '$DistinguishedName' has an empty value."
        }

        if ($result.ContainsKey($key))
        {
            throw "Distinguished name '$DistinguishedName' contains duplicate key '$key', which is not supported."
        }

        $result[$key] = $value
    }

    return $result
}

function Get-WindowsBinaryTrustEvidence
{
    param(
        [Parameter(Mandatory)][string]$BinaryPath
    )

    $resolvedBinaryPath = [System.IO.Path]::GetFullPath($BinaryPath)
    Write-Verbose "Inspecting Authenticode signature for '$resolvedBinaryPath'."

    $signature = Get-AuthenticodeSignature -FilePath $resolvedBinaryPath
    $statusMessage = if ([string]::IsNullOrWhiteSpace($signature.StatusMessage)) { 'No additional details were provided.' } else { $signature.StatusMessage }
    Write-Verbose "Authenticode signature status for '$resolvedBinaryPath': $($signature.Status) - $statusMessage"

    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid)
    {
        throw "Authenticode signature validation failed for '$resolvedBinaryPath': $($signature.Status) - $statusMessage"
    }

    if ($null -eq $signature.SignerCertificate)
    {
        throw "Authenticode signature on '$resolvedBinaryPath' did not include a signer certificate."
    }

    $signerChain = Get-ValidatedCertificateChain -Certificate $signature.SignerCertificate -Description 'Signer' -IgnoreTimeValidity
    $issuerCertificate = Get-ImmediateIssuerCertificate -Chain $signerChain -Description 'Signer'
    $issuerThumbprint = Get-CertificateSha512Thumbprint -Certificate $issuerCertificate
    Write-Verbose "Signer issuer SHA512 thumbprint for '$resolvedBinaryPath': '$issuerThumbprint'."
    $parentIssuerCertificate = Get-ParentIntermediateIssuerCertificate -Chain $signerChain -Description 'Signer'
    $parentIssuerThumbprint = $null
    if ($null -ne $parentIssuerCertificate)
    {
        $parentIssuerThumbprint = Get-CertificateSha512Thumbprint -Certificate $parentIssuerCertificate
        Write-Verbose "Signer parent issuer SHA512 thumbprint for '$resolvedBinaryPath': '$parentIssuerThumbprint'."
    }

    if ($null -eq $signature.TimeStamperCertificate)
    {
        throw "Expected a timestamped signature for '$resolvedBinaryPath', but no timestamp certificate was present."
    }

    $timestampChain = Get-ValidatedCertificateChain -Certificate $signature.TimeStamperCertificate -Description 'Timestamp'

    return [pscustomobject]@{
        BinaryPath = $resolvedBinaryPath
        Signature = $signature
        SignerCertificate = $signature.SignerCertificate
        SignerSubject = $signature.SignerCertificate.Subject
        SignerChain = $signerChain
        SignerIssuerCertificate = $issuerCertificate
        SignerIssuerSha512Thumbprint = $issuerThumbprint
        SignerParentIssuerCertificate = $parentIssuerCertificate
        SignerParentIssuerSha512Thumbprint = $parentIssuerThumbprint
        TimeStamperCertificate = $signature.TimeStamperCertificate
        TimeStamperChain = $timestampChain
    }
}

function Get-NormalizedSha512ThumbprintSet
{
    param(
        [Parameter(Mandatory)][string[]]$Thumbprints,
        [Parameter(Mandatory)][string]$Description
    )

    $normalizedThumbprintSet = @($Thumbprints |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Trim().ToLowerInvariant() })
    if ($normalizedThumbprintSet.Count -eq 0)
    {
        throw "At least one expected $Description SHA512 thumbprint is required."
    }

    return $normalizedThumbprintSet
}

function Format-Sha512ThumbprintSet
{
    param([Parameter(Mandatory)][string[]]$Thumbprints)

    return ($Thumbprints | ForEach-Object { "'$_'" }) -join ', '
}

function Test-Sha512ThumbprintMatch
{
    param(
        [Parameter(Mandatory)][string]$ActualThumbprint,
        [Parameter(Mandatory)][string[]]$ExpectedThumbprints
    )

    $match = $ExpectedThumbprints |
        Where-Object { [string]::Equals($_, $ActualThumbprint, [System.StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    return ($null -ne $match)
}

function Assert-SignerIssuerTrust
{
    param(
        [Parameter(Mandatory)]$Evidence,
        [Parameter(Mandatory)][string[]]$ExpectedIssuerThumbprints,
        [string[]]$ExpectedParentIssuerThumbprints = @()
    )

    $expectedIssuerThumbprintSet = Get-NormalizedSha512ThumbprintSet -Thumbprints $ExpectedIssuerThumbprints -Description 'signer issuer'
    $formattedExpectedIssuerThumbprints = Format-Sha512ThumbprintSet -Thumbprints $expectedIssuerThumbprintSet
    Write-Verbose "Validating signer immediate issuer SHA512 thumbprint for '$($Evidence.BinaryPath)'. Expected one of $formattedExpectedIssuerThumbprints, actual '$($Evidence.SignerIssuerSha512Thumbprint)'."

    if (Test-Sha512ThumbprintMatch -ActualThumbprint $Evidence.SignerIssuerSha512Thumbprint -ExpectedThumbprints $expectedIssuerThumbprintSet)
    {
        Write-Verbose "Signer immediate issuer matched configured issuer SHA512 thumbprints for '$($Evidence.BinaryPath)'."
        return [pscustomobject]@{
            MatchSource = 'ImmediateIssuer'
            Certificate = $Evidence.SignerIssuerCertificate
            Sha512Thumbprint = $Evidence.SignerIssuerSha512Thumbprint
            UsedFallback = $false
        }
    }

    Write-Verbose "Signer immediate issuer thumbprint for '$($Evidence.BinaryPath)' did not match configured issuer SHA512 thumbprints. Evaluating parent issuer fallback."
    $expectedParentIssuerThumbprintSet = @($ExpectedParentIssuerThumbprints |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Trim().ToLowerInvariant() })
    if ($expectedParentIssuerThumbprintSet.Count -eq 0)
    {
        throw "Signer issuer certificate '$($Evidence.SignerIssuerCertificate.Subject)' has SHA512 thumbprint '$($Evidence.SignerIssuerSha512Thumbprint)', expected one of: $formattedExpectedIssuerThumbprints. Parent issuer fallback is not configured."
    }

    $formattedExpectedParentIssuerThumbprints = Format-Sha512ThumbprintSet -Thumbprints $expectedParentIssuerThumbprintSet

    if ($null -eq $Evidence.SignerParentIssuerCertificate -or [string]::IsNullOrWhiteSpace($Evidence.SignerParentIssuerSha512Thumbprint))
    {
        throw "Signer issuer certificate '$($Evidence.SignerIssuerCertificate.Subject)' has SHA512 thumbprint '$($Evidence.SignerIssuerSha512Thumbprint)', expected one of: $formattedExpectedIssuerThumbprints. Parent issuer fallback expected one of: $formattedExpectedParentIssuerThumbprints, but no parent intermediate issuer was available."
    }

    Write-Verbose "Falling back to signer parent issuer SHA512 thumbprint for '$($Evidence.BinaryPath)'. Expected one of $formattedExpectedParentIssuerThumbprints, actual '$($Evidence.SignerParentIssuerSha512Thumbprint)'."
    if (Test-Sha512ThumbprintMatch -ActualThumbprint $Evidence.SignerParentIssuerSha512Thumbprint -ExpectedThumbprints $expectedParentIssuerThumbprintSet)
    {
        Write-Verbose "Signer parent issuer fallback matched configured parent issuer SHA512 thumbprints for '$($Evidence.BinaryPath)'."
        return [pscustomobject]@{
            MatchSource = 'ParentIssuer'
            Certificate = $Evidence.SignerParentIssuerCertificate
            Sha512Thumbprint = $Evidence.SignerParentIssuerSha512Thumbprint
            UsedFallback = $true
        }
    }

    throw "Signer issuer certificate '$($Evidence.SignerIssuerCertificate.Subject)' has SHA512 thumbprint '$($Evidence.SignerIssuerSha512Thumbprint)', expected one of: $formattedExpectedIssuerThumbprints. Fallback parent issuer certificate '$($Evidence.SignerParentIssuerCertificate.Subject)' has SHA512 thumbprint '$($Evidence.SignerParentIssuerSha512Thumbprint)', expected one of: $formattedExpectedParentIssuerThumbprints."
}

function Assert-WindowsBinaryTrust
{
    param(
        [Parameter(Mandatory)][string]$BinaryPath,
        [Parameter(Mandatory)][string]$ExpectedSubject,
        [Parameter(Mandatory)][string[]]$ExpectedIssuerSha512Thumbprints,
        [string[]]$ExpectedParentIssuerSha512Thumbprints = @()
    )

    $evidence = Get-WindowsBinaryTrustEvidence -BinaryPath $BinaryPath
    Write-Verbose "Validating signer subject for '$($evidence.BinaryPath)'."

    $actualSubject = $evidence.SignerSubject
    $expectedSubjectParts = Parse-DistinguishedName -DistinguishedName $ExpectedSubject
    $actualSubjectParts = Parse-DistinguishedName -DistinguishedName $actualSubject
    foreach ($key in $expectedSubjectParts.Keys)
    {
        if (-not $actualSubjectParts.ContainsKey($key))
        {
            throw "Signer subject '$actualSubject' is missing required field '$key' from expected subject '$ExpectedSubject'."
        }

        $actualValue = $actualSubjectParts[$key]
        $expectedValue = $expectedSubjectParts[$key]
        if (-not [string]::Equals($actualValue, $expectedValue, [System.StringComparison]::OrdinalIgnoreCase))
        {
            throw "Signer subject '$actualSubject' has '$key=$actualValue', expected '$key=$expectedValue'."
        }
    }

    $issuerTrustMatch = Assert-SignerIssuerTrust `
        -Evidence $evidence `
        -ExpectedIssuerThumbprints $ExpectedIssuerSha512Thumbprints `
        -ExpectedParentIssuerThumbprints $ExpectedParentIssuerSha512Thumbprints

    Add-Member -InputObject $evidence -NotePropertyName SignerIssuerTrustMatch -NotePropertyValue $issuerTrustMatch -Force
    Write-Verbose "Windows binary trust verification succeeded for '$($evidence.BinaryPath)'."
    return $evidence
}

#endregion SharedProvenanceFunctions

function Get-KustoVersionString
{
    param([Parameter(Mandatory)][string]$BinaryPath)

    try
    {
        $output = & $BinaryPath --version 2>$null
        if ($LASTEXITCODE -eq 0 -and $output)
        {
            $firstLine = @($output | Select-Object -First 1)[0]
            $match = [regex]::Match($firstLine, '\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z\.-]+)?')
            if ($match.Success)
            {
                return $match.Value
            }
        }
    }
    catch
    {
    }

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($BinaryPath)
    $candidate = if (-not [string]::IsNullOrWhiteSpace($versionInfo.ProductVersion))
    {
        $versionInfo.ProductVersion
    }
    else
    {
        $versionInfo.FileVersion
    }

    if ([string]::IsNullOrWhiteSpace($candidate))
    {
        throw "Could not determine version for '$BinaryPath'."
    }

    $metadataMatch = [regex]::Match($candidate, '\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z\.-]+)?')
    if (-not $metadataMatch.Success)
    {
        throw "Version '$candidate' for '$BinaryPath' was not in an expected format."
    }

    return $metadataMatch.Value
}

function Parse-SemanticVersion
{
    param([Parameter(Mandatory)][string]$Value)

    $match = [regex]::Match($Value, '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:\.\d+)?(?:-(?<prerelease>[0-9A-Za-z\.-]+))?$')
    if (-not $match.Success)
    {
        throw "Version '$Value' is not a supported semantic version format."
    }

    return [ordered]@{
        Major = [int]$match.Groups['major'].Value
        Minor = [int]$match.Groups['minor'].Value
        Patch = [int]$match.Groups['patch'].Value
        PreRelease = $match.Groups['prerelease'].Value
    }
}

function Compare-SemanticVersion
{
    param(
        [Parameter(Mandatory)][string]$Left,
        [Parameter(Mandatory)][string]$Right
    )

    $leftVersion = Parse-SemanticVersion -Value $Left
    $rightVersion = Parse-SemanticVersion -Value $Right

    foreach ($part in @('Major', 'Minor', 'Patch'))
    {
        if ($leftVersion[$part] -gt $rightVersion[$part])
        {
            return 1
        }

        if ($leftVersion[$part] -lt $rightVersion[$part])
        {
            return -1
        }
    }

    $leftPre = $leftVersion.PreRelease
    $rightPre = $rightVersion.PreRelease
    if ([string]::IsNullOrWhiteSpace($leftPre) -and [string]::IsNullOrWhiteSpace($rightPre))
    {
        return 0
    }

    if ([string]::IsNullOrWhiteSpace($leftPre))
    {
        return 1
    }

    if ([string]::IsNullOrWhiteSpace($rightPre))
    {
        return -1
    }

    $leftIdentifiers = $leftPre.Split('.', [System.StringSplitOptions]::RemoveEmptyEntries)
    $rightIdentifiers = $rightPre.Split('.', [System.StringSplitOptions]::RemoveEmptyEntries)
    $maxLength = [Math]::Max($leftIdentifiers.Length, $rightIdentifiers.Length)

    for ($index = 0; $index -lt $maxLength; $index++)
    {
        if ($index -ge $leftIdentifiers.Length)
        {
            return -1
        }

        if ($index -ge $rightIdentifiers.Length)
        {
            return 1
        }

        $leftIdentifier = $leftIdentifiers[$index]
        $rightIdentifier = $rightIdentifiers[$index]
        $leftIsNumeric = $leftIdentifier -match '^\d+$'
        $rightIsNumeric = $rightIdentifier -match '^\d+$'

        if ($leftIsNumeric -and $rightIsNumeric)
        {
            $leftNumeric = [System.Numerics.BigInteger]::Parse($leftIdentifier)
            $rightNumeric = [System.Numerics.BigInteger]::Parse($rightIdentifier)
            if ($leftNumeric -gt $rightNumeric)
            {
                return 1
            }

            if ($leftNumeric -lt $rightNumeric)
            {
                return -1
            }

            continue
        }

        if ($leftIsNumeric -and -not $rightIsNumeric)
        {
            return -1
        }

        if (-not $leftIsNumeric -and $rightIsNumeric)
        {
            return 1
        }

        $identifierComparison = [string]::CompareOrdinal($leftIdentifier, $rightIdentifier)
        if ($identifierComparison -gt 0)
        {
            return 1
        }

        if ($identifierComparison -lt 0)
        {
            return -1
        }
    }

    return 0
}

function Invoke-KustoCliInstall
{
    $architecture = Get-WindowsArchitecture
    $assetName = "kusto-win-$architecture.zip"
    Write-Verbose "Selecting release asset '$assetName' for quality '$Quality' from '$Repository'."

    $release = Get-ReleaseForQuality -Repo $Repository -SelectedQuality $Quality -AssetName $assetName
    Write-Verbose "Selected release '$($release.name)' ($($release.tag_name))."
    $releaseStatusLabel = Get-ReleaseStatusLabel -SelectedQuality $Quality -Release $release

    $asset = Get-ReleaseAsset -Release $release -AssetName $assetName
    if ($null -eq $asset)
    {
        throw "Release '$($release.name)' does not contain expected asset '$assetName'."
    }

    if ($Quality -eq 'Dev' -and -not $Force)
    {
        $confirmation = Read-Host "Dev quality skips Authenticode provenance verification but still enforces checksums. Type YES to continue"
        if ($confirmation -cne 'YES')
        {
            throw 'Installation canceled by user.'
        }
    }

    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("kusto-install-" + [guid]::NewGuid().ToString('N'))
    $downloadPath = Join-Path $tempRoot $assetName
    $extractPath = Join-Path $tempRoot 'extract'

    Write-Verbose "Using temporary workspace '$tempRoot'."

    try
    {
        New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

        Invoke-StatusStep -Message "Downloading $releaseStatusLabel" -Action {
            $assetDownloadUri = Get-ReleaseAssetDownloadUri -Asset $asset
            Write-Verbose "Downloading release asset from '$assetDownloadUri' to '$downloadPath'."
            Invoke-GitHubAssetDownload -Uri $assetDownloadUri -OutFile $downloadPath
        }

        $checksumsAsset = Get-ReleaseAsset -Release $release -AssetName 'checksums.txt'
        if ($null -eq $checksumsAsset)
        {
            throw "Release '$($release.name)' did not include checksums.txt."
        }

        $checksumsPath = Join-Path $tempRoot 'checksums.txt'
        $checksumsDownloadUri = Get-ReleaseAssetDownloadUri -Asset $checksumsAsset
        Write-Verbose "Downloading checksums from '$checksumsDownloadUri' to '$checksumsPath'."
        Invoke-GitHubAssetDownload -Uri $checksumsDownloadUri -OutFile $checksumsPath

        $releaseMetadataPath = $null
        $releaseMetadataAsset = Get-ReleaseAsset -Release $release -AssetName 'release-metadata.json'
        if ($null -ne $releaseMetadataAsset)
        {
            $releaseMetadataPath = Join-Path $tempRoot 'release-metadata.json'
            $releaseMetadataDownloadUri = Get-ReleaseAssetDownloadUri -Asset $releaseMetadataAsset
            Write-Verbose "Downloading release metadata from '$releaseMetadataDownloadUri' to '$releaseMetadataPath'."
            Invoke-GitHubAssetDownload -Uri $releaseMetadataDownloadUri -OutFile $releaseMetadataPath
        }

        Invoke-StatusStep -Message 'Verifying asset checksums' -Action {
            $null = Assert-WindowsArchiveIntegrity -ArchivePath $downloadPath -AssetName $assetName -ChecksumsPath $checksumsPath -ReleaseMetadataPath $releaseMetadataPath
        }

        $extractedPayload = Expand-WindowsReleaseArchive -ArchivePath $downloadPath -DestinationPath $extractPath
        $downloadedBinaryPath = $extractedPayload.BinaryPath
        $extractDirectory = $extractedPayload.ExtractDirectory

        $trustConfig = Get-KustoInstallerTrustConfiguration
        $expectedExecutablePayload = @($trustConfig.ExpectedExecutablePayloadFiles)
        $expectedAuxiliaryPayload = @($trustConfig.ExpectedAuxiliaryPayloadFiles)
        $allExpectedPayload = @($expectedExecutablePayload + $expectedAuxiliaryPayload)

        Assert-ExtractedPayloadComplete -ExtractDirectory $extractDirectory -RequiredFileNames $allExpectedPayload
        $manifestPayloadFiles = @(Assert-PayloadManifestMatches -ExtractDirectory $extractDirectory)
        $allowedPayloadFiles = @($allExpectedPayload | Where-Object { $_ -ne 'payload-manifest.json' })
        $unexpectedPayloadFiles = @($manifestPayloadFiles | Where-Object { $_ -notin $allowedPayloadFiles })
        if ($unexpectedPayloadFiles.Count -gt 0)
        {
            throw "Downloaded archive contains unsupported payload file(s): $($unexpectedPayloadFiles -join ', ')."
        }

        if ($Quality -ne 'Dev')
        {
            Invoke-StatusStep -Message 'Verifying asset provenance' -Action {
                # Verify Authenticode trust on every executable payload file (kusto.exe + native sidecars).
                # Treating the .exe alone as the trust signal would let an unsigned/swapped libSkiaSharp.dll
                # be loaded by a signed kusto.exe at first chart render.
                foreach ($payloadName in $expectedExecutablePayload)
                {
                    $payloadPath = Join-Path $extractDirectory $payloadName
                    $null = Assert-WindowsBinaryTrust -BinaryPath $payloadPath -ExpectedSubject $ExpectedSignerSubject -ExpectedIssuerSha512Thumbprints $ExpectedSignerIssuerSha512Thumbprints -ExpectedParentIssuerSha512Thumbprints $ExpectedSignerParentIssuerSha512Thumbprints
                }
            }
        }
        else
        {
            Write-Host 'Checksums verified. Skipping Authenticode provenance verification for the unsigned development build.'
        }

        Invoke-StatusStep -Message 'Validating packaged runtime' -Action {
            $smokePath = Join-Path $tempRoot 'chart-self-test.png'
            & $downloadedBinaryPath '_diag' 'chart-self-test' '--output' $smokePath | Out-Null
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $smokePath))
            {
                throw 'The downloaded Kusto CLI failed its packaged chart check.'
            }
        }

        $installDirectory = [System.IO.Path]::GetFullPath($TargetPath)
        Write-Verbose "Installing to '$installDirectory'."
        New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null

        $destinationPath = Join-Path $installDirectory 'kusto.exe'
        $downloadedVersion = Get-KustoVersionString -BinaryPath $downloadedBinaryPath
        $installedVersion = $downloadedVersion
        Write-Verbose "Downloaded kusto.exe version: '$downloadedVersion'."

        $shouldInstall = $true
        if (Test-Path $destinationPath)
        {
            $existingVersion = Get-KustoVersionString -BinaryPath $destinationPath
            $comparison = Compare-SemanticVersion -Left $downloadedVersion -Right $existingVersion
            Write-Verbose "Existing kusto.exe version at '$destinationPath': '$existingVersion'. Comparison result: $comparison."
            if ($comparison -le 0)
            {
                $shouldInstall = $false
                $installedVersion = $existingVersion
                Write-Host "Existing kusto.exe version '$existingVersion' is newer than or equal to downloaded version '$downloadedVersion'; skipping overwrite."
            }
        }

        if ($shouldInstall)
        {
            Invoke-StatusStep -Message "Installing $downloadedVersion to '$installDirectory'" -Action {
                Install-KustoPayload `
                    -SourceDirectory $extractDirectory `
                    -InstallDirectory $installDirectory `
                    -KnownPayloadFileNames @($allExpectedPayload + $manifestPayloadFiles)
            }
        }

        if ($UpdatePath)
        {
            $sessionPathUpdated = Ensure-PathContains -Entry $installDirectory -Target Process
            $userPathUpdated = Ensure-PathContains -Entry $installDirectory -Target User

            if ($sessionPathUpdated)
            {
                Write-Host "Added '$installDirectory' to current session PATH."
            }
            else
            {
                Write-Host "Current session PATH already contains '$installDirectory'."
            }

            if ($userPathUpdated)
            {
                Write-Host "Added '$installDirectory' to user PATH (will take effect in new terminal sessions)."
            }
            else
            {
                Write-Host "User PATH already contains '$installDirectory'."
            }
        }
        else
        {
            Write-Host "Skipped PATH updates because -UpdatePath was set to false."
        }

        $completionResult = Try-EnablePowerShellCompletion -CommandPath $destinationPath
        switch ($completionResult.Status)
        {
            'Enabled'
            {
                if ($completionResult.ProfileUpdated)
                {
                    Write-Host "Enabled PowerShell tab completion in '$($completionResult.ProfilePath)'. Restart PowerShell to load it."
                }
                else
                {
                    Write-Host "Updated PowerShell tab completion script at '$($completionResult.ScriptPath)'."
                }
            }
            'Unavailable' { Write-Host $completionResult.Message }
            'Failed' { Write-Warning "kusto was installed, but PowerShell tab completion could not be enabled automatically: $($completionResult.Message)" }
        }

        Write-Host "kusto $installedVersion is ready to use from '$installDirectory'." -ForegroundColor Green
    }
    finally
    {
        if (Test-Path $tempRoot)
        {
            Write-Verbose "Cleaning up temporary workspace '$tempRoot'."
            Remove-Item -Path $tempRoot -Recurse -Force
        }
    }
}

if (-not $NoExecute)
{
    Invoke-KustoCliInstall
}

# SIG # Begin signature block
# MII9JwYJKoZIhvcNAQcCoII9GDCCPRQCAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCDZY/itqJvnzoDs
# P2fe5jCvVtdAe2pzr6v57Hf+fGyy0qCCIewwggXMMIIDtKADAgECAhBUmNLR1FsZ
# lUgTecgRwIeZMA0GCSqGSIb3DQEBDAUAMHcxCzAJBgNVBAYTAlVTMR4wHAYDVQQK
# ExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xSDBGBgNVBAMTP01pY3Jvc29mdCBJZGVu
# dGl0eSBWZXJpZmljYXRpb24gUm9vdCBDZXJ0aWZpY2F0ZSBBdXRob3JpdHkgMjAy
# MDAeFw0yMDA0MTYxODM2MTZaFw00NTA0MTYxODQ0NDBaMHcxCzAJBgNVBAYTAlVT
# MR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xSDBGBgNVBAMTP01pY3Jv
# c29mdCBJZGVudGl0eSBWZXJpZmljYXRpb24gUm9vdCBDZXJ0aWZpY2F0ZSBBdXRo
# b3JpdHkgMjAyMDCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBALORKgeD
# Bmf9np3gx8C3pOZCBH8Ppttf+9Va10Wg+3cL8IDzpm1aTXlT2KCGhFdFIMeiVPvH
# or+Kx24186IVxC9O40qFlkkN/76Z2BT2vCcH7kKbK/ULkgbk/WkTZaiRcvKYhOuD
# PQ7k13ESSCHLDe32R0m3m/nJxxe2hE//uKya13NnSYXjhr03QNAlhtTetcJtYmrV
# qXi8LW9J+eVsFBT9FMfTZRY33stuvF4pjf1imxUs1gXmuYkyM6Nix9fWUmcIxC70
# ViueC4fM7Ke0pqrrBc0ZV6U6CwQnHJFnni1iLS8evtrAIMsEGcoz+4m+mOJyoHI1
# vnnhnINv5G0Xb5DzPQCGdTiO0OBJmrvb0/gwytVXiGhNctO/bX9x2P29Da6SZEi3
# W295JrXNm5UhhNHvDzI9e1eM80UHTHzgXhgONXaLbZ7LNnSrBfjgc10yVpRnlyUK
# xjU9lJfnwUSLgP3B+PR0GeUw9gb7IVc+BhyLaxWGJ0l7gpPKWeh1R+g/OPTHU3mg
# trTiXFHvvV84wRPmeAyVWi7FQFkozA8kwOy6CXcjmTimthzax7ogttc32H83rwjj
# O3HbbnMbfZlysOSGM1l0tRYAe1BtxoYT2v3EOYI9JACaYNq6lMAFUSw0rFCZE4e7
# swWAsk0wAly4JoNdtGNz764jlU9gKL431VulAgMBAAGjVDBSMA4GA1UdDwEB/wQE
# AwIBhjAPBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQWBBTIftJqhSobyhmYBAcnz1AQ
# T2ioojAQBgkrBgEEAYI3FQEEAwIBADANBgkqhkiG9w0BAQwFAAOCAgEAr2rd5hnn
# LZRDGU7L6VCVZKUDkQKL4jaAOxWiUsIWGbZqWl10QzD0m/9gdAmxIR6QFm3FJI9c
# Zohj9E/MffISTEAQiwGf2qnIrvKVG8+dBetJPnSgaFvlVixlHIJ+U9pW2UYXeZJF
# xBA2CFIpF8svpvJ+1Gkkih6PsHMNzBxKq7Kq7aeRYwFkIqgyuH4yKLNncy2RtNwx
# AQv3Rwqm8ddK7VZgxCwIo3tAsLx0J1KH1r6I3TeKiW5niB31yV2g/rarOoDXGpc8
# FzYiQR6sTdWD5jw4vU8w6VSp07YEwzJ2YbuwGMUrGLPAgNW3lbBeUU0i/OxYqujY
# lLSlLu2S3ucYfCFX3VVj979tzR/SpncocMfiWzpbCNJbTsgAlrPhgzavhgplXHT2
# 6ux6anSg8Evu75SjrFDyh+3XOjCDyft9V77l4/hByuVkrrOj7FjshZrM77nq81YY
# uVxzmq/FdxeDWds3GhhyVKVB0rYjdaNDmuV3fJZ5t0GNv+zcgKCf0Xd1WF81E+Al
# GmcLfc4l+gcK5GEh2NQc5QfGNpn0ltDGFf5Ozdeui53bFv0ExpK91IjmqaOqu/dk
# ODtfzAzQNb50GQOmxapMomE2gj4d8yu8l13bS3g7LfU772Aj6PXsCyM2la+YZr9T
# 03u4aUoqlmZpxJTG9F9urJh4iIAGXKKy7aIwggajMIIEi6ADAgECAhMzAAWVxhh5
# qtBHy2HnAAAABZXGMA0GCSqGSIb3DQEBDAUAMFoxCzAJBgNVBAYTAlVTMR4wHAYD
# VQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKzApBgNVBAMTIk1pY3Jvc29mdCBJ
# RCBWZXJpZmllZCBDUyBBT0MgQ0EgMDMwHhcNMjYwODMwMDg0MTI1WhcNMjYwOTAy
# MDg0MTI1WjBnMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjERMA8G
# A1UEBxMISXNzYXF1YWgxFzAVBgNVBAoTDkRhbWlhbiBFZHdhcmRzMRcwFQYDVQQD
# Ew5EYW1pYW4gRWR3YXJkczCCAaIwDQYJKoZIhvcNAQEBBQADggGPADCCAYoCggGB
# AJgwP7NgHf2dmQteLUit748E3ImNWivgdqiXAzexieRasD6DfXqBBfwWIHsodP9W
# fyPlrpleVrU/PyAjSjQf5LYWYmHvPpdrdmVZaMibcNtNoeYozUTOLzYuoiPHNAwX
# bVSTBj3cFwGoygdkqZwNE8Hz5uYEOJ30UD9A2wLa+xYBdpBQYV7joc05sD55pNH0
# gMJiAGpYGzv8CL/GoUIBkMQXzMopCMVyKP3skWLsPDxTynaN9VzguyFgz8FYMSgP
# /Gtx5crnBN+6MGSnu+3Hsv3degPwPALEFj1OzjElHtq8oWAUJ0nECdgDzHupB00T
# /Cf/16P8HlioGKCNGjeQr5vTv+rPiOKJVUAKZc2A+uOtwWU/4myFFv3GLem/j1M9
# pXkjm1PQC9DdDlkffnV9pPA0PWZDYdotwG8H/zJoMtEdMxE6lgxN1tlcTycYZYtm
# usXgv7AG0TuTvvQSf7X7arZoX1e+qfLgUe1KvxY1B94P1LeFytusM9DnrX4Sx94G
# IwIDAQABo4IB0zCCAc8wDAYDVR0TAQH/BAIwADAOBgNVHQ8BAf8EBAMCB4AwOgYD
# VR0lBDMwMQYKKwYBBAGCN2EBAAYIKwYBBQUHAwMGGSsGAQQBgjdh8PiHLKGwwBPl
# htEVg5ShoiEwHQYDVR0OBBYEFJkb9quW4qaV/7RA5xdMwEIWK8/TMB8GA1UdIwQY
# MBaAFKRDDH92WqWF5z6NKA8MF6JFaXDGMGcGA1UdHwRgMF4wXKBaoFiGVmh0dHA6
# Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY3JsL01pY3Jvc29mdCUyMElEJTIw
# VmVyaWZpZWQlMjBDUyUyMEFPQyUyMENBJTIwMDMuY3JsMHQGCCsGAQUFBwEBBGgw
# ZjBkBggrBgEFBQcwAoZYaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9j
# ZXJ0cy9NaWNyb3NvZnQlMjBJRCUyMFZlcmlmaWVkJTIwQ1MlMjBBT0MlMjBDQSUy
# MDAzLmNydDBUBgNVHSAETTBLMEkGBFUdIAAwQTA/BggrBgEFBQcCARYzaHR0cDov
# L3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9Eb2NzL1JlcG9zaXRvcnkuaHRtMA0G
# CSqGSIb3DQEBDAUAA4ICAQBcLpsNlTDFYQtm3+53OGs64OaI74wdaLb8Qln/8we4
# zKr7fvv917Q72/4fOAmcHcTZ0Ug9J7ovVbw1IFhdHI5zGSz6CsLhoSeWvnQalBqV
# hglj09ScAv4WrN1cudtdLIujDtmzedaA1y41nxvhHuHJnrzz4J+crJz9+q75X0J1
# 3mZRRaQEIZN3qLpc4+4cMB5FBJTje3qFpIgwlFCXa+wXZGhBo48r1pWAxivn4Nqa
# zO8ljGvqIcRKsHI2notXIl2IGMzl4JPuvfJbKXsX+e97wZLSEY5y1/MvBWIJccAg
# L0w0jB+oMxlTeCPdt0tr9Oo9akq/nDuTtnTebEd/ATsTKyZAhMM4DIjrjQewYt95
# 2iE66Ai2pUEqREAVw+Y4hlaYGVa/eQ7zQsneT6ydN0Qk4W/E2pkFZkEf8jKjv7xj
# r7GYZsvLkq77vIr/usyGYAvzwgRYz1IE1PSyKg0xeQYWVzpZKIYJgOxGy+QCOonB
# CfAvp+ZgfjyGqppDcCMrx1U48zWWdBQVSVnjq4qzQLCeiF8o4UEkzU59/njEYuLm
# 7RCgRv3RbesmrMBn1z3gFISvm+7cddi5jOqDiwdySRM3kFDMQ0EvsHpjCucOU5dd
# Ovs+qeIJKEgAgu/en8/hEauUdL6v0VlGnY9lCn2As8VZkTGvWTZzD902M+BY7j+4
# jzCCBqMwggSLoAMCAQICEzMABZXGGHmq0EfLYecAAAAFlcYwDQYJKoZIhvcNAQEM
# BQAwWjELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlv
# bjErMCkGA1UEAxMiTWljcm9zb2Z0IElEIFZlcmlmaWVkIENTIEFPQyBDQSAwMzAe
# Fw0yNjA4MzAwODQxMjVaFw0yNjA5MDIwODQxMjVaMGcxCzAJBgNVBAYTAlVTMRMw
# EQYDVQQIEwpXYXNoaW5ndG9uMREwDwYDVQQHEwhJc3NhcXVhaDEXMBUGA1UEChMO
# RGFtaWFuIEVkd2FyZHMxFzAVBgNVBAMTDkRhbWlhbiBFZHdhcmRzMIIBojANBgkq
# hkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAmDA/s2Ad/Z2ZC14tSK3vjwTciY1aK+B2
# qJcDN7GJ5FqwPoN9eoEF/BYgeyh0/1Z/I+WumV5WtT8/ICNKNB/kthZiYe8+l2t2
# ZVloyJtw202h5ijNRM4vNi6iI8c0DBdtVJMGPdwXAajKB2SpnA0TwfPm5gQ4nfRQ
# P0DbAtr7FgF2kFBhXuOhzTmwPnmk0fSAwmIAalgbO/wIv8ahQgGQxBfMyikIxXIo
# /eyRYuw8PFPKdo31XOC7IWDPwVgxKA/8a3HlyucE37owZKe77cey/d16A/A8AsQW
# PU7OMSUe2ryhYBQnScQJ2APMe6kHTRP8J//Xo/weWKgYoI0aN5Cvm9O/6s+I4olV
# QAplzYD6463BZT/ibIUW/cYt6b+PUz2leSObU9AL0N0OWR9+dX2k8DQ9ZkNh2i3A
# bwf/Mmgy0R0zETqWDE3W2VxPJxhli2a6xeC/sAbRO5O+9BJ/tftqtmhfV76p8uBR
# 7Uq/FjUH3g/Ut4XK26wz0OetfhLH3gYjAgMBAAGjggHTMIIBzzAMBgNVHRMBAf8E
# AjAAMA4GA1UdDwEB/wQEAwIHgDA6BgNVHSUEMzAxBgorBgEEAYI3YQEABggrBgEF
# BQcDAwYZKwYBBAGCN2Hw+IcsobDAE+WG0RWDlKGiITAdBgNVHQ4EFgQUmRv2q5bi
# ppX/tEDnF0zAQhYrz9MwHwYDVR0jBBgwFoAUpEMMf3ZapYXnPo0oDwwXokVpcMYw
# ZwYDVR0fBGAwXjBcoFqgWIZWaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9w
# cy9jcmwvTWljcm9zb2Z0JTIwSUQlMjBWZXJpZmllZCUyMENTJTIwQU9DJTIwQ0El
# MjAwMy5jcmwwdAYIKwYBBQUHAQEEaDBmMGQGCCsGAQUFBzAChlhodHRwOi8vd3d3
# Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NlcnRzL01pY3Jvc29mdCUyMElEJTIwVmVy
# aWZpZWQlMjBDUyUyMEFPQyUyMENBJTIwMDMuY3J0MFQGA1UdIARNMEswSQYEVR0g
# ADBBMD8GCCsGAQUFBwIBFjNodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3Bz
# L0RvY3MvUmVwb3NpdG9yeS5odG0wDQYJKoZIhvcNAQEMBQADggIBAFwumw2VMMVh
# C2bf7nc4azrg5ojvjB1otvxCWf/zB7jMqvt++/3XtDvb/h84CZwdxNnRSD0nui9V
# vDUgWF0cjnMZLPoKwuGhJ5a+dBqUGpWGCWPT1JwC/has3Vy5210si6MO2bN51oDX
# LjWfG+Ee4cmevPPgn5ysnP36rvlfQnXeZlFFpAQhk3eoulzj7hwwHkUElON7eoWk
# iDCUUJdr7BdkaEGjjyvWlYDGK+fg2prM7yWMa+ohxEqwcjaei1ciXYgYzOXgk+69
# 8lspexf573vBktIRjnLX8y8FYglxwCAvTDSMH6gzGVN4I923S2v06j1qSr+cO5O2
# dN5sR38BOxMrJkCEwzgMiOuNB7Bi33naITroCLalQSpEQBXD5jiGVpgZVr95DvNC
# yd5PrJ03RCThb8TamQVmQR/yMqO/vGOvsZhmy8uSrvu8iv+6zIZgC/PCBFjPUgTU
# 9LIqDTF5BhZXOlkohgmA7EbL5AI6icEJ8C+n5mB+PIaqmkNwIyvHVTjzNZZ0FBVJ
# WeOrirNAsJ6IXyjhQSTNTn3+eMRi4ubtEKBG/dFt6yaswGfXPeAUhK+b7tx12LmM
# 6oOLB3JJEzeQUMxDQS+wemMK5w5Tl106+z6p4gkoSACC796fz+ERq5R0vq/RWUad
# j2UKfYCzxVmRMa9ZNnMP3TYz4FjuP7iPMIIHKDCCBRCgAwIBAgITMwAAABgN65FV
# qYoAmAAAAAAAGDANBgkqhkiG9w0BAQwFADBjMQswCQYDVQQGEwJVUzEeMBwGA1UE
# ChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMTQwMgYDVQQDEytNaWNyb3NvZnQgSUQg
# VmVyaWZpZWQgQ29kZSBTaWduaW5nIFBDQSAyMDIxMB4XDTI2MDMyNjE4MTEzMloX
# DTMxMDMyNjE4MTEzMlowWjELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29m
# dCBDb3Jwb3JhdGlvbjErMCkGA1UEAxMiTWljcm9zb2Z0IElEIFZlcmlmaWVkIENT
# IEFPQyBDQSAwMzCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAMiA2mA0
# VqKJ/ZVZ5Y/kjo+cVfEn+UHft8lHnkYK9HsYtkEyQGKNuIpXCCkEjfEzmd/jzjOc
# f+qdwn44KrrLeOCdBb5Hxl3tT7suOWuZyyRqXNJDSCEzESmcFbz8cezZXxknNCTo
# c/5IOxu+wvst2Uf947aXiaSeEMHCvRn9D3rpO8S2HlvyQLGPW+qJXhg22EsZGplH
# 27Z8r/IExa7zeno7i6jYR2D76AR7Dkgvu+eecoWqZKH9H288nLdYXVhxl7ABTHyx
# dk1SfHdmFWDn2XYumK0+LDMToUyoiypoS9V7czO4V3Zr+5YNkfpVsPJSJErvyYiD
# UNBgD3MMTLIEVw0j6fFVLOCW8vq7s9G42qBxXex/oQvHDz3KxAz9nhHWFEVZdGnI
# 5YooAq18EdOTRSc2I9zGYswxizyN5SM6J19U+NMivL9RXCfDF2WQrzlxl8EQxhn8
# ME07B2iY/jn1jWfyLMqRuGxr6niXD5xBXEBMEXH2CBHv0eGvJPscOak8u+Qm8Fnj
# BbgJbfZRPZIzIN7bycg5Teb6F8eVV4pwsFBzKblWhEOMhwJUju6qAZbY80wTRx96
# LzMLALLocKyywlYVLt6D9hsWGcBMlzJZ8yuQ24Bsx8w3w2mDxytLqNVWjDIPQYbn
# N2CL65BVxIr/rfyYDXERgremcihCA7T264MHAgMBAAGjggHcMIIB2DAOBgNVHQ8B
# Af8EBAMCAYYwEAYJKwYBBAGCNxUBBAMCAQAwHQYDVR0OBBYEFKRDDH92WqWF5z6N
# KA8MF6JFaXDGMFQGA1UdIARNMEswSQYEVR0gADBBMD8GCCsGAQUFBwIBFjNodHRw
# Oi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL0RvY3MvUmVwb3NpdG9yeS5odG0w
# GQYJKwYBBAGCNxQCBAweCgBTAHUAYgBDAEEwEgYDVR0TAQH/BAgwBgEB/wIBADAf
# BgNVHSMEGDAWgBTZQSmwDw9jbO9p1/XNKZ6kSGow5jBwBgNVHR8EaTBnMGWgY6Bh
# hl9odHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NybC9NaWNyb3NvZnQl
# MjBJRCUyMFZlcmlmaWVkJTIwQ29kZSUyMFNpZ25pbmclMjBQQ0ElMjAyMDIxLmNy
# bDB9BggrBgEFBQcBAQRxMG8wbQYIKwYBBQUHMAKGYWh0dHA6Ly93d3cubWljcm9z
# b2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0JTIwSUQlMjBWZXJpZmllZCUy
# MENvZGUlMjBTaWduaW5nJTIwUENBJTIwMjAyMS5jcnQwDQYJKoZIhvcNAQEMBQAD
# ggIBAHHHIFb5fqaF1GJLAP08wxZwZQCfHn9BTCc29l0UYRf7gNEMiv1YKHgLzvAe
# 3D6WDUPe7MrXQOy09fQsUsEUALe9YxhfgiZfCguGhHTGU3yZR2isnduCekIla4jX
# nfVnWsLA+5StKQHF84gYOTenYQJvcej/EeLk9FJH85Sta5AfBeJpcxO5e7chEt7P
# BWRmkWY3BhEPntH03HYX/Izu3M5jQeHSEYJpgQrfz/oWtLRJdp1dbINQJ+flc4YA
# JGNQKcfH4lBQbR/hIcP6JuWkAjSCX5kedWZ1dfEdNl5NrQJgIiEXEo/b3bazSDrM
# uZ6JXXctZSa239QXtOtZekyLb/RQ2eJoOgfuuc8ZFXnFVfy5fLixmKLhqzDOo8zt
# jv6bNytqepnwSNmTmCMuFDcDaxlqmuU67wJpGbJ9wiJUfvNV+AC+bzUxZcXOIB/u
# bLtA6+fIQU8Z12rwxJ8+19HLD9Sre4foqmhok0h89gfp9x5lKLndFq3UD2CsTGrd
# E6OGFKlNxyG4Ei0Aw1U/Ggo1tSb6JH9fdeQv71ZCCKePId76FctyVjy8AZcUPWnj
# Q+owikBiyYQkEUpb11/j//U3mhAOv8Vj0gEmX+hJL3v2Lmu1Ps1nP0q9itoI9EEa
# zRALL6xa+BBrRygzvRAlUt5XCZLFQ7/Sh3TD1CvLttIuvEagMIIHnjCCBYagAwIB
# AgITMwAAAAeHozSje6WOHAAAAAAABzANBgkqhkiG9w0BAQwFADB3MQswCQYDVQQG
# EwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMUgwRgYDVQQDEz9N
# aWNyb3NvZnQgSWRlbnRpdHkgVmVyaWZpY2F0aW9uIFJvb3QgQ2VydGlmaWNhdGUg
# QXV0aG9yaXR5IDIwMjAwHhcNMjEwNDAxMjAwNTIwWhcNMzYwNDAxMjAxNTIwWjBj
# MQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMTQw
# MgYDVQQDEytNaWNyb3NvZnQgSUQgVmVyaWZpZWQgQ29kZSBTaWduaW5nIFBDQSAy
# MDIxMIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAsvDArxmIKOLdVHpM
# SWxpCFUJtFL/ekr4weslKPdnF3cpTeuV8veqtmKVgok2rO0D05BpyvUDCg1wdsoE
# tuxACEGcgHfjPF/nZsOkg7c0mV8hpMT/GvB4uhDvWXMIeQPsDgCzUGzTvoi76YDp
# xDOxhgf8JuXWJzBDoLrmtThX01CE1TCCvH2sZD/+Hz3RDwl2MsvDSdX5rJDYVuR3
# bjaj2QfzZFmwfccTKqMAHlrz4B7ac8g9zyxlTpkTuJGtFnLBGasoOnn5NyYlf0xF
# 9/bjVRo4Gzg2Yc7KR7yhTVNiuTGH5h4eB9ajm1OCShIyhrKqgOkc4smz6obxO+Hx
# KeJ9bYmPf6KLXVNLz8UaeARo0BatvJ82sLr2gqlFBdj1sYfqOf00Qm/3B4XGFPDK
# /H04kteZEZsBRc3VT2d/iVd7OTLpSH9yCORV3oIZQB/Qr4nD4YT/lWkhVtw2v2s0
# TnRJubL/hFMIQa86rcaGMhNsJrhysLNNMeBhiMezU1s5zpusf54qlYu2v5sZ5zL0
# KvBDLHtL8F9gn6jOy3v7Jm0bbBHjrW5yQW7S36ALAt03QDpwW1JG1Hxu/FUXJbBO
# 2AwwVG4Fre+ZQ5Od8ouwt59FpBxVOBGfN4vN2m3fZx1gqn52GvaiBz6ozorgIEjn
# +PhUXILhAV5Q/ZgCJ0u2+ldFGjcCAwEAAaOCAjUwggIxMA4GA1UdDwEB/wQEAwIB
# hjAQBgkrBgEEAYI3FQEEAwIBADAdBgNVHQ4EFgQU2UEpsA8PY2zvadf1zSmepEhq
# MOYwVAYDVR0gBE0wSzBJBgRVHSAAMEEwPwYIKwYBBQUHAgEWM2h0dHA6Ly93d3cu
# bWljcm9zb2Z0LmNvbS9wa2lvcHMvRG9jcy9SZXBvc2l0b3J5Lmh0bTAZBgkrBgEE
# AYI3FAIEDB4KAFMAdQBiAEMAQTAPBgNVHRMBAf8EBTADAQH/MB8GA1UdIwQYMBaA
# FMh+0mqFKhvKGZgEByfPUBBPaKiiMIGEBgNVHR8EfTB7MHmgd6B1hnNodHRwOi8v
# d3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NybC9NaWNyb3NvZnQlMjBJZGVudGl0
# eSUyMFZlcmlmaWNhdGlvbiUyMFJvb3QlMjBDZXJ0aWZpY2F0ZSUyMEF1dGhvcml0
# eSUyMDIwMjAuY3JsMIHDBggrBgEFBQcBAQSBtjCBszCBgQYIKwYBBQUHMAKGdWh0
# dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0JTIw
# SWRlbnRpdHklMjBWZXJpZmljYXRpb24lMjBSb290JTIwQ2VydGlmaWNhdGUlMjBB
# dXRob3JpdHklMjAyMDIwLmNydDAtBggrBgEFBQcwAYYhaHR0cDovL29uZW9jc3Au
# bWljcm9zb2Z0LmNvbS9vY3NwMA0GCSqGSIb3DQEBDAUAA4ICAQB/JSqe/tSr6t1m
# CttXI0y6XmyQ41uGWzl9xw+WYhvOL47BV09Dgfnm/tU4ieeZ7NAR5bguorTCNr58
# HOcA1tcsHQqt0wJsdClsu8bpQD9e/al+lUgTUJEV80Xhco7xdgRrehbyhUf4pkeA
# hBEjABvIUpD2LKPho5Z4DPCT5/0TlK02nlPwUbv9URREhVYCtsDM+31OFU3fDV8B
# mQXv5hT2RurVsJHZgP4y26dJDVF+3pcbtvh7R6NEDuYHYihfmE2HdQRq5jRvLE1E
# b59PYwISFCX2DaLZ+zpU4bX0I16ntKq4poGOFaaKtjIA1vRElItaOKcwtc04CBrX
# SfyL2Op6mvNIxTk4OaswIkTXbFL81ZKGD+24uMCwo/pLNhn7VHLfnxlMVzHQVL+b
# Ha9KhTyzwdG/L6uderJQn0cGpLQMStUuNDArxW2wF16QGZ1NtBWgKA8Kqv48M8Hf
# FqNifN6+zt6J0GwzvU8g0rYGgTZR8zDEIJfeZxwWDHpSxB5FJ1VVU1LIAtB7o9PX
# bjXzGifaIMYTzU4YKt4vMNwwBmetQDHhdAtTPplOXrnI9SI6HeTtjDD3iUN/7ygb
# ahmYOHk7VB7fwT4ze+ErCbMh6gHV1UuXPiLciloNxH6K4aMfZN1oLVk6YFeIJEok
# uPgNPa6EnTiOL60cPqfny+Fq8UiuZzGCGpEwghqNAgEBMHEwWjELMAkGA1UEBhMC
# VVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjErMCkGA1UEAxMiTWlj
# cm9zb2Z0IElEIFZlcmlmaWVkIENTIEFPQyBDQSAwMwITMwAFlcYYearQR8th5wAA
# AAWVxjANBglghkgBZQMEAgEFAKBeMBAGCisGAQQBgjcCAQwxAjAAMBkGCSqGSIb3
# DQEJAzEMBgorBgEEAYI3AgEEMC8GCSqGSIb3DQEJBDEiBCCrB43a0KX/jXzli/42
# Oi2LkQOmzblwtI4KLN0fXfbfkjANBgkqhkiG9w0BAQEFAASCAYBbGXIlBL6jubov
# jXFV4h53a3CnCTomT2dbZUnWN4c5Y4QAdmfkREHkPgfT6kzHSiynrmXjnS/Y9AOC
# vPg6ogK4UhHubS0udbJLO8BbGZJpxe6ATty+4fziOo1smA4O1ieY7Nbd92pQqELr
# CfgXvjA0oc/5qgr6IU7CuyPpj/lGfu1IsxwnbZ5h+4O/U6DlIV3d49DhKT4e0v6A
# sMtGPRRfKTWDMh865NOllQtDFv7d8Tmka9m/1c/Nut5OfbOhhMc1fxxSvIVMcRCk
# mPHX9adfz/5kw3zXYNsnxdpC7vTj7KkTkxWjbajBLlNcHXv9LikQt08gi3yI2QRT
# 22YhHegwtzDCtxlZySQqyBtQOYyPCooSGsAlgiTnD7s3Taf0iqKEXTOxEhCcpGA/
# sgVmXScBaR0nCBt6pKMQQL3XQm+mfOwfRXJw9ct02EV0qVZ7h2/w8engOAxez8WM
# QgW1/IAzOeq38QyG8IOcS9V2C8Hb7GERzjS5741P3FYJO9DqqhahghgRMIIYDQYK
# KwYBBAGCNwMDATGCF/0wghf5BgkqhkiG9w0BBwKgghfqMIIX5gIBAzEPMA0GCWCG
# SAFlAwQCAQUAMIIBYgYLKoZIhvcNAQkQAQSgggFRBIIBTTCCAUkCAQEGCisGAQQB
# hFkKAwEwMTANBglghkgBZQMEAgEFAAQgSsyo8k8mG/ODGV8AN3olUOTjvJtksWKc
# JOzSmAPSsigCBmqEjSmI6RgTMjAyNjA5MDEwNDIzMTUuMTgzWjAEgAIB9KCB4aSB
# 3jCB2zELMAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcT
# B1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjElMCMGA1UE
# CxMcTWljcm9zb2Z0IEFtZXJpY2EgT3BlcmF0aW9uczEnMCUGA1UECxMeblNoaWVs
# ZCBUU1MgRVNOOjc4MDAtMDVFMC1EOTQ3MTUwMwYDVQQDEyxNaWNyb3NvZnQgUHVi
# bGljIFJTQSBUaW1lIFN0YW1waW5nIEF1dGhvcml0eaCCDyEwggeCMIIFaqADAgEC
# AhMzAAAABeXPD/9mLsmHAAAAAAAFMA0GCSqGSIb3DQEBDAUAMHcxCzAJBgNVBAYT
# AlVTMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xSDBGBgNVBAMTP01p
# Y3Jvc29mdCBJZGVudGl0eSBWZXJpZmljYXRpb24gUm9vdCBDZXJ0aWZpY2F0ZSBB
# dXRob3JpdHkgMjAyMDAeFw0yMDExMTkyMDMyMzFaFw0zNTExMTkyMDQyMzFaMGEx
# CzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAw
# BgNVBAMTKU1pY3Jvc29mdCBQdWJsaWMgUlNBIFRpbWVzdGFtcGluZyBDQSAyMDIw
# MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAnnznUmP94MWfBX1jtQYi
# oxwe1+eXM9ETBb1lRkd3kcFdcG9/sqtDlwxKoVIcaqDb+omFio5DHC4RBcbyQHjX
# CwMk/l3TOYtgoBjxnG/eViS4sOx8y4gSq8Zg49REAf5huXhIkQRKe3Qxs8Sgp02K
# HAznEa/Ssah8nWo5hJM1xznkRsFPu6rfDHeZeG1Wa1wISvlkpOQooTULFm809Z0Z
# YlQ8Lp7i5F9YciFlyAKwn6yjN/kR4fkquUWfGmMopNq/B8U/pdoZkZZQbxNlqJOi
# BGgCWpx69uKqKhTPVi3gVErnc/qi+dR8A2MiAz0kN0nh7SqINGbmw5OIRC0EsZ31
# WF3Uxp3GgZwetEKxLms73KG/Z+MkeuaVDQQheangOEMGJ4pQZH55ngI0Tdy1bi69
# INBV5Kn2HVJo9XxRYR/JPGAaM6xGl57Ei95HUw9NV/uC3yFjrhc087qLJQawSC3x
# zY/EXzsT4I7sDbxOmM2rl4uKK6eEpurRduOQ2hTkmG1hSuWYBunFGNv21Kt4N20A
# KmbeuSnGnsBCd2cjRKG79+TX+sTehawOoxfeOO/jR7wo3liwkGdzPJYHgnJ54Uxb
# ckF914AqHOiEV7xTnD1a69w/UTxwjEugpIPMIIE67SFZ2PMo27xjlLAHWW3l1CEA
# FjLNHd3EQ79PUr8FUXetXr0CAwEAAaOCAhswggIXMA4GA1UdDwEB/wQEAwIBhjAQ
# BgkrBgEEAYI3FQEEAwIBADAdBgNVHQ4EFgQUa2koOjUvSGNAz3vYr0npPtk92yEw
# VAYDVR0gBE0wSzBJBgRVHSAAMEEwPwYIKwYBBQUHAgEWM2h0dHA6Ly93d3cubWlj
# cm9zb2Z0LmNvbS9wa2lvcHMvRG9jcy9SZXBvc2l0b3J5Lmh0bTATBgNVHSUEDDAK
# BggrBgEFBQcDCDAZBgkrBgEEAYI3FAIEDB4KAFMAdQBiAEMAQTAPBgNVHRMBAf8E
# BTADAQH/MB8GA1UdIwQYMBaAFMh+0mqFKhvKGZgEByfPUBBPaKiiMIGEBgNVHR8E
# fTB7MHmgd6B1hnNodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NybC9N
# aWNyb3NvZnQlMjBJZGVudGl0eSUyMFZlcmlmaWNhdGlvbiUyMFJvb3QlMjBDZXJ0
# aWZpY2F0ZSUyMEF1dGhvcml0eSUyMDIwMjAuY3JsMIGUBggrBgEFBQcBAQSBhzCB
# hDCBgQYIKwYBBQUHMAKGdWh0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMv
# Y2VydHMvTWljcm9zb2Z0JTIwSWRlbnRpdHklMjBWZXJpZmljYXRpb24lMjBSb290
# JTIwQ2VydGlmaWNhdGUlMjBBdXRob3JpdHklMjAyMDIwLmNydDANBgkqhkiG9w0B
# AQwFAAOCAgEAX4h2x35ttVoVdedMeGj6TuHYRJklFaW4sTQ5r+k77iB79cSLNe+G
# zRjv4pVjJviceW6AF6ycWoEYR0LYhaa0ozJLU5Yi+LCmcrdovkl53DNt4EXs87KD
# ogYb9eGEndSpZ5ZM74LNvVzY0/nPISHz0Xva71QjD4h+8z2XMOZzY7YQ0Psw+ety
# NZ1CesufU211rLslLKsO8F2aBs2cIo1k+aHOhrw9xw6JCWONNboZ497mwYW5EfN0
# W3zL5s3ad4Xtm7yFM7Ujrhc0aqy3xL7D5FR2J7x9cLWMq7eb0oYioXhqV2tgFqbK
# HeDick+P8tHYIFovIP7YG4ZkJWag1H91KlELGWi3SLv10o4KGag42pswjybTi4to
# QcC/irAodDW8HNtX+cbz0sMptFJK+KObAnDFHEsukxD+7jFfEV9Hh/+CSxKRsmnu
# iovCWIOb+H7DRon9TlxydiFhvu88o0w35JkNbJxTk4MhF/KgaXn0GxdH8elEa2Im
# q45gaa8D+mTm8LWVydt4ytxYP/bqjN49D9NZ81coE6aQWm88TwIf4R4YZbOpMKN0
# CyejaPNN41LGXHeCUMYmBx3PkP8ADHD1J2Cr/6tjuOOCztfp+o9Nc+ZoIAkpUcA/
# X2gSMkgHAPUvIdtoSAHEUKiBhI6JQivRepyvWcl+JYbYbBh7pmgAXVswggeXMIIF
# f6ADAgECAhMzAAAAVyTTleCi6ckxAAAAAABXMA0GCSqGSIb3DQEBDAUAMGExCzAJ
# BgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNV
# BAMTKU1pY3Jvc29mdCBQdWJsaWMgUlNBIFRpbWVzdGFtcGluZyBDQSAyMDIwMB4X
# DTI1MTAyMzIwNDY1M1oXDTI2MTAyMjIwNDY1M1owgdsxCzAJBgNVBAYTAlVTMRMw
# EQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVN
# aWNyb3NvZnQgQ29ycG9yYXRpb24xJTAjBgNVBAsTHE1pY3Jvc29mdCBBbWVyaWNh
# IE9wZXJhdGlvbnMxJzAlBgNVBAsTHm5TaGllbGQgVFNTIEVTTjo3ODAwLTA1RTAt
# RDk0NzE1MDMGA1UEAxMsTWljcm9zb2Z0IFB1YmxpYyBSU0EgVGltZSBTdGFtcGlu
# ZyBBdXRob3JpdHkwggIiMA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQCxbKUK
# kwh9uLMktjWQ9c7ZyZfYdFa9FsCZ4pJnl7Hv+MLKZ1XsRqn4hzaKpG1YOQop7mAv
# olXzTC2fkLaocks/FRgUo0bdSQeQAjbUygI35haeFPwr9i4+Jvr7r3vSN1t4UoiJ
# kxbB3mGelf0neN6164R1dun8N8UErXkm4Pck7Na4Xay5AI+CpiNA+T+Cmr7coIq1
# clFtdIJIn1i0hNTYgfCZ90TuXY99nXnjDTjWmj58N5OPSAk7NxX8m/npDQz7DX2M
# Aqj8jk8TOstXUg9CeY/iivVfhFsleTw41fI459c7ErZUuk3GCSUrXIB7NsU/a7Oq
# KFpeRbWH0ZAsYQ0oRKd7PCB1Fos01pi2bwBP+lkdgnfmZlWqRl0whySlAcmT8XV9
# IvIMp4q0fhMLhxzcRIpQyAi2rTtlmbvgkKx+GatDWKNU0OLVKWf5AFqaALta+Jlu
# RCdx5BGr0Nj7qEA3A6tqwBlSJWvaQ+6PWMcM5fNQbg71BMrvQ/+hdKpkA3WhO/dR
# 8XwlMaYDGD6XVk87PnQxj3ocEPD/dsj/AEY28uTp8tWevEY3kHm6cX+Vi+ONZshR
# 3IE9VCc84pe7TxJEdtjX0zUehZfo81m/6/NJ6pV5ZYcp0qMLcaNWNtsamL4ktuLJ
# opFLASqjj20ku+7r1xDt1axuSxqLhNRGdWPaYwIDAQABo4IByzCCAccwHQYDVR0O
# BBYEFI6DyV4tNQ4CCUhn5uNemIPtEpKnMB8GA1UdIwQYMBaAFGtpKDo1L0hjQM97
# 2K9J6T7ZPdshMGwGA1UdHwRlMGMwYaBfoF2GW2h0dHA6Ly93d3cubWljcm9zb2Z0
# LmNvbS9wa2lvcHMvY3JsL01pY3Jvc29mdCUyMFB1YmxpYyUyMFJTQSUyMFRpbWVz
# dGFtcGluZyUyMENBJTIwMjAyMC5jcmwweQYIKwYBBQUHAQEEbTBrMGkGCCsGAQUF
# BzAChl1odHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NlcnRzL01pY3Jv
# c29mdCUyMFB1YmxpYyUyMFJTQSUyMFRpbWVzdGFtcGluZyUyMENBJTIwMjAyMC5j
# cnQwDAYDVR0TAQH/BAIwADAWBgNVHSUBAf8EDDAKBggrBgEFBQcDCDAOBgNVHQ8B
# Af8EBAMCB4AwZgYDVR0gBF8wXTBRBgwrBgEEAYI3TIN9AQEwQTA/BggrBgEFBQcC
# ARYzaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9Eb2NzL1JlcG9zaXRv
# cnkuaHRtMAgGBmeBDAEEAjANBgkqhkiG9w0BAQwFAAOCAgEAcnXAdjzpmTlJQEM9
# jbl3+71glVpo1rvW7GNfhzI79cni48Q0JI7CRFOc2iA8vFMPQWDfPhMV//ZP/QgV
# LF21ZW1OOHOuf5YsifN5FrBSFMIVWs8EkoRZWyGb4iDv+cHslsk3zz6W0iYFsvmR
# PVK0Et8bpSSwBwNs1JDDD3QJReEa54HGWdK+OQBfWiGI3XrLVsHazSu9DHwKx6mX
# YK4F59N8OswbNb+3M3HlhorYPw5bB6pNZlwaUk7hiNk0jzdxOtCCF8eX/wBc4ePx
# xYvfAQWW1BCzbF5FgBvcp2eXughYopdZoFgljk/dA+yIL4NMynt6N1gpOtvf3p/e
# Cv7Av8yzn9ne8hZk8km/Xyo3DjR9Q295GfDMxCfHx0zZsa5ddBnnLs/xpdPgckyj
# fj2pm2fhdDCJQT8MOn74xQvSSCO938N6jtevfvU8U89hvhNuhmGNXXH37AIcOg6k
# 0IG35W5dTvzK0l0rNDUm/ZwQ/UX0f3/BIuwwNS9YwTu72YYSU48Nk8xWvwC4ES4t
# 1tNIR1ovCxkGmXPEsFyDGFn8KzfTIGG4TdCGpPVgNnalrnpF7E8DZJqw9xOhPqAm
# AnoTToGZnbNBM29Y6OzldCodti5dyh4NzB7ZRoLsQM4YPwaYsT0uKq1Cy5AIzu/s
# jbFH6w9lPYDH/zkeMiQz7czNMrUxggdDMIIHPwIBATB4MGExCzAJBgNVBAYTAlVT
# MR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jv
# c29mdCBQdWJsaWMgUlNBIFRpbWVzdGFtcGluZyBDQSAyMDIwAhMzAAAAVyTTleCi
# 6ckxAAAAAABXMA0GCWCGSAFlAwQCAQUAoIIEnDARBgsqhkiG9w0BCRACDzECBQAw
# GgYJKoZIhvcNAQkDMQ0GCyqGSIb3DQEJEAEEMBwGCSqGSIb3DQEJBTEPFw0yNjA5
# MDEwNDIzMTVaMC8GCSqGSIb3DQEJBDEiBCA5DplRdITJHCUW7DfFzfml94ip04D2
# /uYvPmexB3WH/zCBuQYLKoZIhvcNAQkQAi8xgakwgaYwgaMwgaAEIPU8n2S1BW5M
# ZYhsos7h/VVQ6VRTb0BEISkNmYVMeNtSMHwwZaRjMGExCzAJBgNVBAYTAlVTMR4w
# HAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29m
# dCBQdWJsaWMgUlNBIFRpbWVzdGFtcGluZyBDQSAyMDIwAhMzAAAAVyTTleCi6ckx
# AAAAAABXMIIDXgYLKoZIhvcNAQkQAhIxggNNMIIDSaGCA0UwggNBMIICKQIBATCC
# AQmhgeGkgd4wgdsxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAw
# DgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24x
# JTAjBgNVBAsTHE1pY3Jvc29mdCBBbWVyaWNhIE9wZXJhdGlvbnMxJzAlBgNVBAsT
# Hm5TaGllbGQgVFNTIEVTTjo3ODAwLTA1RTAtRDk0NzE1MDMGA1UEAxMsTWljcm9z
# b2Z0IFB1YmxpYyBSU0EgVGltZSBTdGFtcGluZyBBdXRob3JpdHmiIwoBATAHBgUr
# DgMCGgMVAP0vMTmcQlEBQTZKzfFooo9cecvDoGcwZaRjMGExCzAJBgNVBAYTAlVT
# MR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jv
# c29mdCBQdWJsaWMgUlNBIFRpbWVzdGFtcGluZyBDQSAyMDIwMA0GCSqGSIb3DQEB
# CwUAAgUA7kAvEzAiGA8yMDI2MDgzMTE2NDkyM1oYDzIwMjYwOTAxMTY0OTIzWjB0
# MDoGCisGAQQBhFkKBAExLDAqMAoCBQDuQC8TAgEAMAcCAQACAguYMAcCAQACAhMs
# MAoCBQDuQYCTAgEAMDYGCisGAQQBhFkKBAIxKDAmMAwGCisGAQQBhFkKAwKgCjAI
# AgEAAgMHoSChCjAIAgEAAgMBhqAwDQYJKoZIhvcNAQELBQADggEBALiJOPbFrPID
# ggXR2KZQ4iRGbe21MCi1fe1VagCDuhCTWt8X/NCgHG9/RPLVS5wPhsiOomelsq7V
# 24xOtCF2S0yLFv4JKTVxFPo7Q801RoQl9dkSDFZuPFD/noxigZVxMWYCtU86paO1
# gi+WldLohrAg6uddw+xDtP28/f52t8EnxOuacmaQLurvPslJ3/a0hDwXBnieEWqS
# WGvdIYFfMrK35ZHoMHMFfYl07WBrNYhfwo8qydXQkexz+aPnMychpLl4UeqvNrmb
# bNwqVcz8OIYDi7IxhJ7O91gkaolIE7XUxDUhCd3/PIQiYe9Aavz37L08h1OEqshB
# SvN/ucnE3PQwDQYJKoZIhvcNAQEBBQAEggIAhMgsA5PbDPhsEfE8MwED1hnDaBb5
# TVltE9XbqinHl/SdoJI0o8zvKBhY8YJJAdKTBOzQkMiG03WSG/CPzvn/PoCvJWHP
# E2PHKEpdaTQIqZXP5YBHj1AWY+FdGLdsQTozbqaL/8lNt0r8yv8rF37v5sQQJMmQ
# NmsSHrEs5Zt9mD4ugtJwz5akYjNXqVNHuyRI2RTcieo8lsOY5+cCpXwiQ4pLT/tJ
# hdOFlIROwXhH7qLvZkeAUFfkQMc10WcQgzVRnCzOlYJneOfL6Le7D6zDN+4PfvcN
# yE5U2ONJ6q8omZi2WQTyXXDn/RO/oy2fRnjEe9EWv5WHxXonvGenYsWmKdiVHGjb
# iKKwAC020RIAB/rLYO9aa//A5uVMq9I1gWS13kqDMcq9iE9HzW4eBp3ev/mMAjEg
# wL+bWPuM6/3HUNakg9IPIDEcTZMTuThy+1S/BzWPLm9NSz4p7ffiWMobs87Wji5F
# jZzubt6ANgEVxm48BIu9azWHZ3jl+8vxCIUquhIYvNJm4DbFTzyKwerybnrREdGs
# c3FvqPXp+IabXjejpIWBzkgvr2lol6tp8vqScizvpHDLySFlcpmJ6pmqWx2pfO5U
# e/TqavX1j4YGvQ44V0UmgFT0PKKV5GpEK5r9n7AumBJIpD74FwFLNZTzuKG7wjBQ
# nCnNkTghKnsVYNg=
# SIG # End signature block
