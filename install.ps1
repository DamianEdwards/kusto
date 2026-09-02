[CmdletBinding()]
param(
    [ValidateSet('Dev', 'PreRelease', 'Stable')]
    [string]$Quality = 'Stable',

    [switch]$Force,

    [string]$TargetPath = (Join-Path $HOME '.kusto\bin'),

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

if (-not $runningOnWindows -and -not $NoExecute)
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
    }
}

function Get-WindowsExecutablePayloadFiles
{
    param([Parameter(Mandatory)][string[]]$PayloadFiles)

    return @(
        $PayloadFiles |
            Where-Object {
                $extension = [System.IO.Path]::GetExtension($_)
                $extension -ieq '.exe' -or $extension -ieq '.dll'
            } |
            Sort-Object -Unique
    )
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

        Assert-ExtractedPayloadComplete -ExtractDirectory $extractDirectory -RequiredFileNames @('kusto.exe', 'payload-manifest.json')
        $manifestPayloadFiles = @(Assert-PayloadManifestMatches -ExtractDirectory $extractDirectory)
        $executablePayloadFiles = @(Get-WindowsExecutablePayloadFiles -PayloadFiles $manifestPayloadFiles)

        if ($Quality -ne 'Dev')
        {
            Invoke-StatusStep -Message 'Verifying asset provenance' -Action {
                foreach ($payloadName in $executablePayloadFiles)
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
                    -KnownPayloadFileNames $manifestPayloadFiles
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
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCDxAN/h2iyohU+S
# AuegeGUIGhyRgJ/qEDtnZAfCym+HpaCCIewwggXMMIIDtKADAgECAhBUmNLR1FsZ
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
# 03u4aUoqlmZpxJTG9F9urJh4iIAGXKKy7aIwggajMIIEi6ADAgECAhMzAAVIO1uw
# 3ZRlCXmYAAAABUg7MA0GCSqGSIb3DQEBDAUAMFoxCzAJBgNVBAYTAlVTMR4wHAYD
# VQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKzApBgNVBAMTIk1pY3Jvc29mdCBJ
# RCBWZXJpZmllZCBDUyBFT0MgQ0EgMDQwHhcNMjYwODMxMDgzNzE0WhcNMjYwOTAz
# MDgzNzE0WjBnMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjERMA8G
# A1UEBxMISXNzYXF1YWgxFzAVBgNVBAoTDkRhbWlhbiBFZHdhcmRzMRcwFQYDVQQD
# Ew5EYW1pYW4gRWR3YXJkczCCAaIwDQYJKoZIhvcNAQEBBQADggGPADCCAYoCggGB
# AK9pm+mIlFMYfmYqeUNjIrZD7Ney7BAGK26XGlUpU7mWvC2s9ZubvwGkCmjNI0Ju
# y8ViHbZush2bm8/ME9Ss4y4QDYrpNNaht4e6FieqpwFERqN4tM3vvOSWiIDjbuX7
# BetF8pCwTkehOSY+OiM/BoUGcF0rMJ5dWhWa52kWxjQeDataktWfT0W9VT3Yu2qo
# DySl5oIJtZFkWOidZ3oLiPU1X+QRe3Z0/eXN0NxqS1OtTPOMvqlbYdvQ7XxYDdkv
# QzQ5VdUAxezxlEZCHw53L57vK+ZOYpQQFJ7aW56PfJ53wSTxC9C2udhcHKfx5YTZ
# raF2aCpdb7mWsYBd47gIms1TmNQjolDflrKGRO1dYLTAffsatVX+ZoM9ifTRUG/0
# 1pcPxczG4P4CURWtMOUJ0hqAgH0KP/eZ4kcZ9sABCdKBccKRlHsjpy2UAJGi/zvA
# Wug5fvsFI1ncD3gVYh3aqwayET3JVWVgVDNdp7y0MVvqbkwSWEXK157vSACkPoog
# iQIDAQABo4IB0zCCAc8wDAYDVR0TAQH/BAIwADAOBgNVHQ8BAf8EBAMCB4AwOgYD
# VR0lBDMwMQYKKwYBBAGCN2EBAAYIKwYBBQUHAwMGGSsGAQQBgjdh8PiHLKGwwBPl
# htEVg5ShoiEwHQYDVR0OBBYEFOHRYuqfjmOB52i87AvF2f3E91K9MB8GA1UdIwQY
# MBaAFJrxVHd1DIcWN0agrN55+fR/wXjpMGcGA1UdHwRgMF4wXKBaoFiGVmh0dHA6
# Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY3JsL01pY3Jvc29mdCUyMElEJTIw
# VmVyaWZpZWQlMjBDUyUyMEVPQyUyMENBJTIwMDQuY3JsMHQGCCsGAQUFBwEBBGgw
# ZjBkBggrBgEFBQcwAoZYaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9j
# ZXJ0cy9NaWNyb3NvZnQlMjBJRCUyMFZlcmlmaWVkJTIwQ1MlMjBFT0MlMjBDQSUy
# MDA0LmNydDBUBgNVHSAETTBLMEkGBFUdIAAwQTA/BggrBgEFBQcCARYzaHR0cDov
# L3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9Eb2NzL1JlcG9zaXRvcnkuaHRtMA0G
# CSqGSIb3DQEBDAUAA4ICAQB85RWEkNHpLFK0xJrWNRzvSHzPNu4uqbz9WfG/Y64+
# C7ArsrlP80RJ41ByOwAXc+63JugpR3Fzq8bDoEcqReZSRlZ76XkDnK0aJorJaTMR
# KS2pOLZguDgMFOv6uMx4c0H/8yugxR9d/uWXgjZDIGmuvXkhhEFjHPRDVAneX3be
# 8thXb0SYiVSY94z0n+wc3xG7jKTzXwVVu/KIEsY5xQ89JysVzpAPwMzsENc2dxbZ
# aHtfEYUDz73eeOdorg/z0jBObJXcAOgfKe5XUHnymKpGWnjwpORJ3hHD44FDWGeM
# 90Tmugjb4qbdI0I6Nym54bRKY7jK5QicV9lAQ80dSzA04d2hFs9/Wpynp5Iddvfj
# wTCZZhqQUsvgNNzqj5m9LcCSIGNd5Gi4vIWl8OmaSW+w/upL70caxXPuRvF4BCOt
# F/zlU11q4DeuXpUyALB18uxyJbARDHL9dnQRi3TN6iLCj4ec5DnmMhbCr0xoBLKs
# /dKD9CZfXTDXF9onMwDc0nu1mEu52hf6u9WaoqOVLi2027tvIEr3xJkCMMUpYETJ
# 4OIM9cL0hwt/oGhZcXGnCvLhV+gEjDVJJe8pnFTHd5GU4jiFqfiAER6tCDr2oom0
# 7N3zYXSbPpuaUSaqsftAl6r6s/Fhw9vhaq0d3rNuC9RZXSg3nu7OJwx86Kx2fr1u
# wzCCBqMwggSLoAMCAQICEzMABUg7W7DdlGUJeZgAAAAFSDswDQYJKoZIhvcNAQEM
# BQAwWjELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlv
# bjErMCkGA1UEAxMiTWljcm9zb2Z0IElEIFZlcmlmaWVkIENTIEVPQyBDQSAwNDAe
# Fw0yNjA4MzEwODM3MTRaFw0yNjA5MDMwODM3MTRaMGcxCzAJBgNVBAYTAlVTMRMw
# EQYDVQQIEwpXYXNoaW5ndG9uMREwDwYDVQQHEwhJc3NhcXVhaDEXMBUGA1UEChMO
# RGFtaWFuIEVkd2FyZHMxFzAVBgNVBAMTDkRhbWlhbiBFZHdhcmRzMIIBojANBgkq
# hkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAr2mb6YiUUxh+Zip5Q2MitkPs17LsEAYr
# bpcaVSlTuZa8Laz1m5u/AaQKaM0jQm7LxWIdtm6yHZubz8wT1KzjLhANiuk01qG3
# h7oWJ6qnAURGo3i0ze+85JaIgONu5fsF60XykLBOR6E5Jj46Iz8GhQZwXSswnl1a
# FZrnaRbGNB4Nq1qS1Z9PRb1VPdi7aqgPJKXmggm1kWRY6J1neguI9TVf5BF7dnT9
# 5c3Q3GpLU61M84y+qVth29DtfFgN2S9DNDlV1QDF7PGURkIfDncvnu8r5k5ilBAU
# ntpbno98nnfBJPEL0La52Fwcp/HlhNmtoXZoKl1vuZaxgF3juAiazVOY1COiUN+W
# soZE7V1gtMB9+xq1Vf5mgz2J9NFQb/TWlw/FzMbg/gJRFa0w5QnSGoCAfQo/95ni
# Rxn2wAEJ0oFxwpGUeyOnLZQAkaL/O8Ba6Dl++wUjWdwPeBViHdqrBrIRPclVZWBU
# M12nvLQxW+puTBJYRcrXnu9IAKQ+iiCJAgMBAAGjggHTMIIBzzAMBgNVHRMBAf8E
# AjAAMA4GA1UdDwEB/wQEAwIHgDA6BgNVHSUEMzAxBgorBgEEAYI3YQEABggrBgEF
# BQcDAwYZKwYBBAGCN2Hw+IcsobDAE+WG0RWDlKGiITAdBgNVHQ4EFgQU4dFi6p+O
# Y4HnaLzsC8XZ/cT3Ur0wHwYDVR0jBBgwFoAUmvFUd3UMhxY3RqCs3nn59H/BeOkw
# ZwYDVR0fBGAwXjBcoFqgWIZWaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9w
# cy9jcmwvTWljcm9zb2Z0JTIwSUQlMjBWZXJpZmllZCUyMENTJTIwRU9DJTIwQ0El
# MjAwNC5jcmwwdAYIKwYBBQUHAQEEaDBmMGQGCCsGAQUFBzAChlhodHRwOi8vd3d3
# Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NlcnRzL01pY3Jvc29mdCUyMElEJTIwVmVy
# aWZpZWQlMjBDUyUyMEVPQyUyMENBJTIwMDQuY3J0MFQGA1UdIARNMEswSQYEVR0g
# ADBBMD8GCCsGAQUFBwIBFjNodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3Bz
# L0RvY3MvUmVwb3NpdG9yeS5odG0wDQYJKoZIhvcNAQEMBQADggIBAHzlFYSQ0eks
# UrTEmtY1HO9IfM827i6pvP1Z8b9jrj4LsCuyuU/zREnjUHI7ABdz7rcm6ClHcXOr
# xsOgRypF5lJGVnvpeQOcrRomislpMxEpLak4tmC4OAwU6/q4zHhzQf/zK6DFH13+
# 5ZeCNkMgaa69eSGEQWMc9ENUCd5fdt7y2FdvRJiJVJj3jPSf7BzfEbuMpPNfBVW7
# 8ogSxjnFDz0nKxXOkA/AzOwQ1zZ3Ftloe18RhQPPvd5452iuD/PSME5sldwA6B8p
# 7ldQefKYqkZaePCk5EneEcPjgUNYZ4z3ROa6CNvipt0jQjo3KbnhtEpjuMrlCJxX
# 2UBDzR1LMDTh3aEWz39anKenkh129+PBMJlmGpBSy+A03OqPmb0twJIgY13kaLi8
# haXw6ZpJb7D+6kvvRxrFc+5G8XgEI60X/OVTXWrgN65elTIAsHXy7HIlsBEMcv12
# dBGLdM3qIsKPh5zkOeYyFsKvTGgEsqz90oP0Jl9dMNcX2iczANzSe7WYS7naF/q7
# 1Zqio5UuLbTbu28gSvfEmQIwxSlgRMng4gz1wvSHC3+gaFlxcacK8uFX6ASMNUkl
# 7ymcVMd3kZTiOIWp+IARHq0IOvaiibTs3fNhdJs+m5pRJqqx+0CXqvqz8WHD2+Fq
# rR3es24L1FldKDee7s4nDHzorHZ+vW7DMIIHKDCCBRCgAwIBAgITMwAAABcnRQkL
# i4evxgAAAAAAFzANBgkqhkiG9w0BAQwFADBjMQswCQYDVQQGEwJVUzEeMBwGA1UE
# ChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMTQwMgYDVQQDEytNaWNyb3NvZnQgSUQg
# VmVyaWZpZWQgQ29kZSBTaWduaW5nIFBDQSAyMDIxMB4XDTI2MDMyNjE4MTEzMVoX
# DTMxMDMyNjE4MTEzMVowWjELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29m
# dCBDb3Jwb3JhdGlvbjErMCkGA1UEAxMiTWljcm9zb2Z0IElEIFZlcmlmaWVkIENT
# IEVPQyBDQSAwNDCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAILHZP4D
# D2YqAZXMn5OrQ8yfj0beK0ixilvHsKUtJEcV7VEQt09xnWwipY6GxJ/LrLKoRqkK
# UYf0l70VcDVxCBm++lBuSD5AidUuv/QQ+tUELCsz3qVtEjY/E14LBcb0uzJbaEbo
# pCCKe0OY0IGjjOkMivfvumVV1KWJmbpQHusfCa8GdHTZBPq2euparaKHMHqVElVM
# TO6HQ5p/Mgx4ydgzT7H697kQ4sd1+Kr4deIx/0lvtgse1iDIciIkDttNYuoVIsZp
# OHtmVvFuwtcD3U46ugSm/s6PMW67e2SkL0V+UDgOnYS6rj6o+bFSp8an5NfSAtEm
# n00k7PMguNxMPeuQUUVvFS/XHKDpq+K8UMu2goGEzZN3Xfy6YTWk05pxqe5Ji08c
# h5AeYHqFoWLrhq8sEvBNMCb9FuK3zrRwVdHvbCr7lCHiFKZ7MeopcRFY+lUF74A+
# sngipz5o94yYiSgJZlA7bYecs0VQVJeOLDIhuC+Uf8sgAkSpNp9PPENmAqGUtTvO
# vqDCyrdY2lxhAjo27FafCHdVUMPIXuidCoqzkuXtuV5U3RjxW+qATjmmnIFu/Co3
# 9G6fl8wIJHPdpgxjSRmEo73Z4/u3jMepnltAwCBnS0TY/P+NvTCLKRQX89yg6qqT
# e9UuJENiy3q93cYQw3MylRS9By8Ebjr4I4hvAgMBAAGjggHcMIIB2DAOBgNVHQ8B
# Af8EBAMCAYYwEAYJKwYBBAGCNxUBBAMCAQAwHQYDVR0OBBYEFJrxVHd1DIcWN0ag
# rN55+fR/wXjpMFQGA1UdIARNMEswSQYEVR0gADBBMD8GCCsGAQUFBwIBFjNodHRw
# Oi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL0RvY3MvUmVwb3NpdG9yeS5odG0w
# GQYJKwYBBAGCNxQCBAweCgBTAHUAYgBDAEEwEgYDVR0TAQH/BAgwBgEB/wIBADAf
# BgNVHSMEGDAWgBTZQSmwDw9jbO9p1/XNKZ6kSGow5jBwBgNVHR8EaTBnMGWgY6Bh
# hl9odHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NybC9NaWNyb3NvZnQl
# MjBJRCUyMFZlcmlmaWVkJTIwQ29kZSUyMFNpZ25pbmclMjBQQ0ElMjAyMDIxLmNy
# bDB9BggrBgEFBQcBAQRxMG8wbQYIKwYBBQUHMAKGYWh0dHA6Ly93d3cubWljcm9z
# b2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0JTIwSUQlMjBWZXJpZmllZCUy
# MENvZGUlMjBTaWduaW5nJTIwUENBJTIwMjAyMS5jcnQwDQYJKoZIhvcNAQEMBQAD
# ggIBAJB1Whn9TSbfyXaIppkWWzFq+m2mg4vJpHVr1krZNIXWQ6cUmEwOx7oqQKCy
# 96iISNdNVzpe3zogoefvo2TmpkHQFe/aIxFDaCIAmZi9lyay2hmp8HYzcp3nCcmF
# Qk60X9voeypJ6VjqeGsXTrOivWUOYNCLEFlwsH3NHX5EpCyjWN6Q3Fi5ST4do3eT
# VLnuqTQ7/9huTBTSYQsJbTg3m8gIxnHlPlzs2r/u4u9tWEJ0Pt/ZtmkDhTu86QHW
# igHgBoRHemOgnQxp3ksXKLo1r2n1m7+Gst46NTkUi1LljGyq+V9fEBOEnXvoKaRi
# y0pGbK1IdnsmEpF9Xp71l+2T84Nv8IrikZUBWqw5/jffttAas4ccJDci832CadS4
# OHwl29uF6hY8fEg3UYHmxSJjnzi1c3vF0PwsJKxGom9Dx7treBlZOBWK6BGzVBar
# 43Qb02N7okeU3UKMl6GB74fk8aS0mNr6O4YSvQ/66RKRwvqppnEVBOHdIMjvWW9b
# 77duX8TN3pI7w31R3D6t6jK9EcLJOJKymVlBIFNUl0+ajeoKka7IcW0+jkIGff8U
# 9OKol3cz0Eeiop3Qb0qaDp8ZwC8XCcs1cDaSi/vbvBGWMvfKl+ovuIBP9ienG6Xp
# HAdGVw5/10MaDVFG+v3Y0/8JZVchvryB5Hau9T82x+a2MXXAMIIHnjCCBYagAwIB
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
# cm9zb2Z0IElEIFZlcmlmaWVkIENTIEVPQyBDQSAwNAITMwAFSDtbsN2UZQl5mAAA
# AAVIOzANBglghkgBZQMEAgEFAKBeMBAGCisGAQQBgjcCAQwxAjAAMBkGCSqGSIb3
# DQEJAzEMBgorBgEEAYI3AgEEMC8GCSqGSIb3DQEJBDEiBCCRv9doEnY48QbaW0Jj
# 7+9HyutPnfUr/4zXc/U6bYQLeTANBgkqhkiG9w0BAQEFAASCAYB7qrltJdLBvO78
# xAAXhpwZpcUsFuo+snAPxpHO+bQs+ncKggefZYDDW7eHMXTwr8Ur5m6d+WEglTER
# JGKeLOBLuInndAVnWgJdhvbeS++jwYSmpzUhQaBKu2U64EKUKUKHFGT5prQjAuhj
# SsZ/f4ivYMheIUsvPiX+kAsP2WSeGOHBrSO7OZOE+OHTdblm0HqlBF2SBuHjkynG
# d6txV881kIkWVl6OJPN1SCOFQR7HBgW78nz59sv37KwquUSOcpsZlIvTg+ci59N8
# lhyGshGmoOyJ/lzrelh7P0xWDtSLrgkMUmto0sTZn7FPRpv9uZUh0mCAxA1Rawur
# nN3tdCsciw/eP4UELbqf68MBXZHcVlPQDGsL1pO1W2J5l4axohZYW/kad5t5Tfb8
# rH93DGlni0vNH5hbGkdK9jPmgRfcbTC6a3NTadWGaqpXGAfK6tXsLE+kAQ7i0hzi
# ic5pw0wJ5XgWMhsqYRW8bwJfjs0JHCUpPMA7Pw6wlN6f+lhUIlihghgRMIIYDQYK
# KwYBBAGCNwMDATGCF/0wghf5BgkqhkiG9w0BBwKgghfqMIIX5gIBAzEPMA0GCWCG
# SAFlAwQCAQUAMIIBYgYLKoZIhvcNAQkQAQSgggFRBIIBTTCCAUkCAQEGCisGAQQB
# hFkKAwEwMTANBglghkgBZQMEAgEFAAQgMymsRxKyFssbM4WIRjySHA1aDpd6Q3A2
# u4ES7nCokfMCBmqEgt01uhgTMjAyNjA5MDIwMDEyNTkuMjU5WjAEgAIB9KCB4aSB
# 3jCB2zELMAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcT
# B1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjElMCMGA1UE
# CxMcTWljcm9zb2Z0IEFtZXJpY2EgT3BlcmF0aW9uczEnMCUGA1UECxMeblNoaWVs
# ZCBUU1MgRVNOOjdEMDAtMDVFMC1EOTQ3MTUwMwYDVQQDEyxNaWNyb3NvZnQgUHVi
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
# f6ADAgECAhMzAAAAVdndaSYo+fjiAAAAAABVMA0GCSqGSIb3DQEBDAUAMGExCzAJ
# BgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNV
# BAMTKU1pY3Jvc29mdCBQdWJsaWMgUlNBIFRpbWVzdGFtcGluZyBDQSAyMDIwMB4X
# DTI1MTAyMzIwNDY0OVoXDTI2MTAyMjIwNDY0OVowgdsxCzAJBgNVBAYTAlVTMRMw
# EQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVN
# aWNyb3NvZnQgQ29ycG9yYXRpb24xJTAjBgNVBAsTHE1pY3Jvc29mdCBBbWVyaWNh
# IE9wZXJhdGlvbnMxJzAlBgNVBAsTHm5TaGllbGQgVFNTIEVTTjo3RDAwLTA1RTAt
# RDk0NzE1MDMGA1UEAxMsTWljcm9zb2Z0IFB1YmxpYyBSU0EgVGltZSBTdGFtcGlu
# ZyBBdXRob3JpdHkwggIiMA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQC9uR+S
# HllIs/QwJRZp9rD8pmhVm72JDHyvknCFm92tSLzpSMIIVA42NBqesjEYX2FEYhkt
# BnnSAessL7h+lQQl9/m3ThXAHJYLb9tY66To2ZpOH0mk9kNwbM1H3lCWvKN8SO2X
# 6DGPXbM08R0AM+mVV/O3xxhFYUHH8Vt9yHTyTo/2nuNfarWMU9tTFZgn7E7IYLVo
# qEMZjlv7zAvf2/qoLQcUjH+/fL5t6n5oReigrxWh5Yr6zN9oWNejxhNy9DxQvizO
# 70cVO5k2/q++gnsm76jlpOPnWymH7T4VdbfxOUv+sMF3mJrv2OyQu054dsOORuWO
# KXDN6BzG/2Lj0XTlmtL/kQtkIJjVVqo7sQ4spVrHF0A7mjLW9vQHHRlFVfWbEWNj
# NrLYQLTnWTrIYkebnzLWh7YgpFr9IzX4FMax7q8c2LlDZ3lmehH0A4BQMPAkgipE
# jitnPYxKKeHXVatdMb26sXa6jJ3lV77yHF6z0AF4/Y9hAqVdhMDG91p5qcNND+/C
# acz7JNxbOtWbzhnfxdUXDgbun9k1naexy+/q6u7YB69dzJXW3yFruJaaGGBNYE0G
# tWK4OVzeI+87PZJU9s96qHJj81fA1kICBzYfmk7O27ozBDEMiO17dcz8WQoHEeh9
# LZps1P/Qcb7Fm0WpQkNrGBslrqU3XOHuymO5DwIDAQABo4IByzCCAccwHQYDVR0O
# BBYEFFYEXxBt3AgD8Mi/qckWysHXrGW2MB8GA1UdIwQYMBaAFGtpKDo1L0hjQM97
# 2K9J6T7ZPdshMGwGA1UdHwRlMGMwYaBfoF2GW2h0dHA6Ly93d3cubWljcm9zb2Z0
# LmNvbS9wa2lvcHMvY3JsL01pY3Jvc29mdCUyMFB1YmxpYyUyMFJTQSUyMFRpbWVz
# dGFtcGluZyUyMENBJTIwMjAyMC5jcmwweQYIKwYBBQUHAQEEbTBrMGkGCCsGAQUF
# BzAChl1odHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NlcnRzL01pY3Jv
# c29mdCUyMFB1YmxpYyUyMFJTQSUyMFRpbWVzdGFtcGluZyUyMENBJTIwMjAyMC5j
# cnQwDAYDVR0TAQH/BAIwADAWBgNVHSUBAf8EDDAKBggrBgEFBQcDCDAOBgNVHQ8B
# Af8EBAMCB4AwZgYDVR0gBF8wXTBRBgwrBgEEAYI3TIN9AQEwQTA/BggrBgEFBQcC
# ARYzaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9Eb2NzL1JlcG9zaXRv
# cnkuaHRtMAgGBmeBDAEEAjANBgkqhkiG9w0BAQwFAAOCAgEAUh7hklR72pQpxZ5b
# KlyEHnx9cT9kha/YPlc/n+T+0HssI30G+Y1JUpndV5yVAz3vzB8S+690xBJS/pjb
# RuggzwMrUrUhT1w/bUwbQTGIfFqqOuKR/apt+tciKngR/e/Zs1gpDELE3dJzOnVJ
# fQfu6orYvk6F8MSJd/XmKi7mGH4Q9pqqnj1zM1CkkM5H+98mCFRz+pyyUM+GgJml
# nHxvY4O/LAZA1fCqVuyYJLbi4aYSRDdQfklR43pz3XJqxVyFLvyuIyubpH1mkCI7
# ml80owZTYwubUDemnT3wNxsVMBz3keHpC+SH//bwX9d7ZswVvoMvtLDRk73m/SC/
# RlPIl/FL8sLF+tp4Qgj0VIU4oAwSnXM0VKza57QYaMG33IQQxTC/Gr0TEXPRpnNi
# byK8l99+khUOdf/6tVFNhzEiRDIViyUiFiVYX1KMLDmvj2pqSMxE2Hxb07tpqiiV
# JVmV5BmMa3QrwnMyXKnqGnaVtbpepHHZw4dtvEkPGYQ3OiEZTOIjXeUjaDYF/mqJ
# t8Lhso1Gkmj2VsTwdRtjSomITy7dJTx4NBrJI9c4SEmPFEJDDA696NiYEbk/sJyR
# A0FKeeXXb4UpEqA+iPQy/7Pk4yGP3PYy2luccsCR6nSh1AKUTLIIb+5Hm0rmtbqZ
# kfk6rnpRZLQ0jo1XUkZLsmuLqMUxggdDMIIHPwIBATB4MGExCzAJBgNVBAYTAlVT
# MR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jv
# c29mdCBQdWJsaWMgUlNBIFRpbWVzdGFtcGluZyBDQSAyMDIwAhMzAAAAVdndaSYo
# +fjiAAAAAABVMA0GCWCGSAFlAwQCAQUAoIIEnDARBgsqhkiG9w0BCRACDzECBQAw
# GgYJKoZIhvcNAQkDMQ0GCyqGSIb3DQEJEAEEMBwGCSqGSIb3DQEJBTEPFw0yNjA5
# MDIwMDEyNTlaMC8GCSqGSIb3DQEJBDEiBCD61Hs/5hTPb+4w26juRyHt0glsXsDK
# a5kbOdPBr4DZizCBuQYLKoZIhvcNAQkQAi8xgakwgaYwgaMwgaAEINi5PJdkhmK7
# v33+/g9qqyZ5LMHGHSuqRiruxhhq+P7NMHwwZaRjMGExCzAJBgNVBAYTAlVTMR4w
# HAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29m
# dCBQdWJsaWMgUlNBIFRpbWVzdGFtcGluZyBDQSAyMDIwAhMzAAAAVdndaSYo+fji
# AAAAAABVMIIDXgYLKoZIhvcNAQkQAhIxggNNMIIDSaGCA0UwggNBMIICKQIBATCC
# AQmhgeGkgd4wgdsxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAw
# DgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24x
# JTAjBgNVBAsTHE1pY3Jvc29mdCBBbWVyaWNhIE9wZXJhdGlvbnMxJzAlBgNVBAsT
# Hm5TaGllbGQgVFNTIEVTTjo3RDAwLTA1RTAtRDk0NzE1MDMGA1UEAxMsTWljcm9z
# b2Z0IFB1YmxpYyBSU0EgVGltZSBTdGFtcGluZyBBdXRob3JpdHmiIwoBATAHBgUr
# DgMCGgMVAB07VAGCZb+24FlXkQaOF+xXhw3qoGcwZaRjMGExCzAJBgNVBAYTAlVT
# MR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jv
# c29mdCBQdWJsaWMgUlNBIFRpbWVzdGFtcGluZyBDQSAyMDIwMA0GCSqGSIb3DQEB
# CwUAAgUA7kF2RDAiGA8yMDI2MDkwMTE2MDUyNFoYDzIwMjYwOTAyMTYwNTI0WjB0
# MDoGCisGAQQBhFkKBAExLDAqMAoCBQDuQXZEAgEAMAcCAQACAgorMAcCAQACAhIq
# MAoCBQDuQsfEAgEAMDYGCisGAQQBhFkKBAIxKDAmMAwGCisGAQQBhFkKAwKgCjAI
# AgEAAgMHoSChCjAIAgEAAgMBhqAwDQYJKoZIhvcNAQELBQADggEBACnfaSGXfZUv
# gAiJeoWhXKkgW0lNlol0ZqL7ogW2VvO+nIMNcoWIYsBn+H5uHn0X97ZJjKsDhNsd
# O61XcqS4/5oip0fFCeVWDBpslOYwv6S1+iJJi53tzUcXoI6RtQlfxzMHgxlN8EHs
# z4AZ/s8Nn/0Uva92REXiGvOTt99PyvOH2ksdeQdxJSv2LCx4eQgPXK+V7YRU6v08
# JXPtFyUOjhBWS53J5B6xMokN2agkPxkqGTWRUaYirU3yyIZ9cWOOlInjO8fOc8oq
# 8hL5uuZwcsie+u+MjfGHwQ1xq2IhhiO8/pQnN5DCMa6QD2S3izHqUFz2fsB1QndD
# v5PgGZwiGn4wDQYJKoZIhvcNAQEBBQAEggIAk8dGnBHxyyq8uJpgwJvfd51ezxrV
# jKvu290gxSN7gX4zJsmwf4v6+osNF/9gzTvBJ/r9p6ZUVpPQNN3HpIUEXBf2rRim
# KC39iRMxOVyPWLVhV56E5xmgxOGmWOnn3BzPGG51CqDlz41lr79oQeaVMFdZOa8X
# mDBvLigc1osaeuiJnd7a8ZpHqcj/6bAY/VM+Pmb0MELDgUPuVuU1y53KVlDg6KEQ
# aJsUWFOXKd79WHITVeHL+ViIODUwInraV0F/LVw2C3fT5jAhjK/Z6CT9AkoRJhbt
# ypEKCnjCVmSc2KEgOCbqtjMNtiSaxKRaJbxYuLogME2uKoUsy6n7nBlCi4ZplKi7
# Hj2JLhvbE3+GPAKmLyUXM/VPLVbVNJJsWVsumxlyjvNWe/FTnXy/dSYTOkFvjq9q
# YTPvpmdYC6AUnGNgwJAKs5pHV3bfnGp1DGIt2gjWE1cRLyXmnbwaNTS82iBBWx+O
# 3Y6uEYkqbQ35FTSTkCCwtEpeKZrTfL5Btwy/r9pn7QLbqpsz0cBSTG5+vrJ7uekJ
# dlVlrWtOTmmA/lXm5eNVmLFBIUYJZToI162aDZ8/KwD6FcaiqAso9+1BgjhPEVvs
# Bmg9xhs9OXaVbqSbda1D9Nnxe2Fi/w+ElnwKfV9gIkxVM0L/ziCvSc7462Ff0j+w
# 1+Z9Ledpzr23M3g=
# SIG # End signature block
