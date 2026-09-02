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

function Get-PowerShellCompletionProfileEntry
{
    param([Parameter(Mandatory)][string]$ScriptPath)

    $escapedScriptPath = $ScriptPath.Replace("'", "''")
    return "if ((Get-Command -Name 'kusto' -CommandType Application -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath '$escapedScriptPath')) { . '$escapedScriptPath' }"
}

function Update-PowerShellCompletionProfile
{
    param(
        [Parameter(Mandatory)][string]$ProfilePath,
        [Parameter(Mandatory)][string]$ScriptPath
    )

    $marker = '# Added by the kusto installer for shell completion'
    $profileEntry = Get-PowerShellCompletionProfileEntry -ScriptPath $ScriptPath
    $profileContent = if (Test-Path -LiteralPath $ProfilePath) { Get-Content -LiteralPath $ProfilePath -Raw } else { '' }
    $newline = if ($profileContent.Contains("`r`n")) { "`r`n" } else { [Environment]::NewLine }
    $profileLines = @($profileContent -split '\r?\n')
    $updatedLines = [System.Collections.Generic.List[string]]::new()
    $entryAdded = $false

    for ($index = 0; $index -lt $profileLines.Count; $index++)
    {
        if ($profileLines[$index].Trim() -eq $marker)
        {
            if (-not $entryAdded)
            {
                $updatedLines.Add($marker)
                $updatedLines.Add($profileEntry)
                $entryAdded = $true
            }

            if ($index + 1 -lt $profileLines.Count)
            {
                $index++
            }
            continue
        }

        $updatedLines.Add($profileLines[$index])
    }

    if (-not $entryAdded)
    {
        if ($updatedLines.Count -eq 1 -and $updatedLines[0] -eq '')
        {
            $updatedLines.Clear()
        }
        elseif ($updatedLines.Count -gt 0 -and $updatedLines[$updatedLines.Count - 1] -ne '')
        {
            $updatedLines.Add('')
        }

        $updatedLines.Add($marker)
        $updatedLines.Add($profileEntry)
    }

    $updatedContent = $updatedLines -join $newline
    if ($updatedContent -eq $profileContent)
    {
        return $false
    }

    $profileDirectory = Split-Path -Parent $ProfilePath
    if (-not [string]::IsNullOrWhiteSpace($profileDirectory))
    {
        New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null
    }
    Set-Content -LiteralPath $ProfilePath -Value $updatedContent -NoNewline -Encoding utf8
    return $true
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
        Set-Content -LiteralPath $scriptPath -Value $scriptContent -Encoding utf8
        $profileUpdated = Update-PowerShellCompletionProfile -ProfilePath $profilePath -ScriptPath $scriptPath

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
# MII9KQYJKoZIhvcNAQcCoII9GjCCPRYCAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCApWi2Lbm7Pc5b6
# XRrBOQr6Yb6A3grHg/UFGVWbFHgP46CCIewwggXMMIIDtKADAgECAhBUmNLR1FsZ
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
# 03u4aUoqlmZpxJTG9F9urJh4iIAGXKKy7aIwggajMIIEi6ADAgECAhMzAAW70pIU
# yEjdN1EIAAAABbvSMA0GCSqGSIb3DQEBDAUAMFoxCzAJBgNVBAYTAlVTMR4wHAYD
# VQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKzApBgNVBAMTIk1pY3Jvc29mdCBJ
# RCBWZXJpZmllZCBDUyBBT0MgQ0EgMDMwHhcNMjYwOTAxMDgzMzA5WhcNMjYwOTA0
# MDgzMzA5WjBnMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjERMA8G
# A1UEBxMISXNzYXF1YWgxFzAVBgNVBAoTDkRhbWlhbiBFZHdhcmRzMRcwFQYDVQQD
# Ew5EYW1pYW4gRWR3YXJkczCCAaIwDQYJKoZIhvcNAQEBBQADggGPADCCAYoCggGB
# AIqtVAdOGu//pfMTw1JCARJSMckpyEUMxyRzKAj2tExDTC+o0WzlEP4dG/BmR3dw
# BQRduts9uOQerh0sBB6slQ1rH05rnhnMQab2hTIwSpWLW/Qm/UZMH8REB5YgRgCg
# q4nLet/TqLPMpI0YiyzyhWigYhWLCcXH4Jc3dav4SHY4tXMR6BG2rB1bPY7wHIW8
# D2gpD5G9QR3HKSUrA4B1ZoW0oXjXlyE7l4JsTTEGSYSehYcGQi5xwaH0aDCUCn9l
# 5ZmycVtcc3FXgNsWjYYTC8ipMpnnFXDiUkQbRVlOdUMfO7lPlcfFm/LV2Tyk/6sF
# gLp3p8ouqXUL7SlTS291VrrXy5u6QznZyBGeoDjX/NisfJ7TRSQxZrP7rFhE4P+x
# +V1I1BjeM09e6e2Vk0OaSbbRIIkO1fys22GrAYn1Z7XlXONDVuDmu03Pa2ixQ/Br
# GUMyAeIvOxOaikxY1hJYGxPMsipJUwjzfnEOMJals4Llv1WAhCo0TJV7R0tpQFnn
# VQIDAQABo4IB0zCCAc8wDAYDVR0TAQH/BAIwADAOBgNVHQ8BAf8EBAMCB4AwOgYD
# VR0lBDMwMQYKKwYBBAGCN2EBAAYIKwYBBQUHAwMGGSsGAQQBgjdh8PiHLKGwwBPl
# htEVg5ShoiEwHQYDVR0OBBYEFB5Mdkx1V3sEyii5ZjXk71MJuMhuMB8GA1UdIwQY
# MBaAFKRDDH92WqWF5z6NKA8MF6JFaXDGMGcGA1UdHwRgMF4wXKBaoFiGVmh0dHA6
# Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY3JsL01pY3Jvc29mdCUyMElEJTIw
# VmVyaWZpZWQlMjBDUyUyMEFPQyUyMENBJTIwMDMuY3JsMHQGCCsGAQUFBwEBBGgw
# ZjBkBggrBgEFBQcwAoZYaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9j
# ZXJ0cy9NaWNyb3NvZnQlMjBJRCUyMFZlcmlmaWVkJTIwQ1MlMjBBT0MlMjBDQSUy
# MDAzLmNydDBUBgNVHSAETTBLMEkGBFUdIAAwQTA/BggrBgEFBQcCARYzaHR0cDov
# L3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9Eb2NzL1JlcG9zaXRvcnkuaHRtMA0G
# CSqGSIb3DQEBDAUAA4ICAQC9DdWWW+R350inwNh69mBpy2w//fJUaKrq1gM6GqXY
# dvOKUs7H6X9Sj3AN/H+51RSrIXEb8n71ZGtgHiEmK2dxfSJ9xsx2ITdsNlfjrZF/
# tpIIq+6RY8Pk1Dlh5T7cfcwwyItXTLCBY3AIOtFw/mK4yhNIt/EzNEjcTPQqByW1
# 5tgitCssM/jtwMa5MQmSUbMZSsofBAra9Z4uW9kwCsfDpva3dEAHXEq7w5N1YdP4
# U/T5+OI79fecmcTO42b1HMtC8Gqo+NHl8C+pHCfpNuV4p5JS3+SPJkkPZGlMLH6y
# cqzXRgz1+5/cgmwigOgIfG4ij5pX1QQwrZrjHCIM/3+/zYpNrFRqgL2j4bgcCNa/
# 3PgNt/SI9+23oO/tG9a9oWSrvuntXtIgUWmAkXk9Ijnf6eKUCGbETdlwBxgiIbLJ
# Ao9ARi+SlcwvrOmIhj/dkwKmUWWrTO9CNfM5k8fwMp8iy0mclCCrVOw5Itcvi68G
# E27S3wqTUzF+wgzfWj2J6nHUGgpceFtEUzwwI/g91iQCqVLRM3rQhoDe6Z8e/zC4
# jh56LcL0r0n1cu0r4xgtBXVtYl8bOiObOdKD89EiPu8ip86mMKrrftjbWtHuvrXt
# yF4jQRmhdGI39UDBN7/16qOXcddKePMPwGO0muYumCaheFD357XFzrs4xojMiv1/
# JjCCBqMwggSLoAMCAQICEzMABbvSkhTISN03UQgAAAAFu9IwDQYJKoZIhvcNAQEM
# BQAwWjELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlv
# bjErMCkGA1UEAxMiTWljcm9zb2Z0IElEIFZlcmlmaWVkIENTIEFPQyBDQSAwMzAe
# Fw0yNjA5MDEwODMzMDlaFw0yNjA5MDQwODMzMDlaMGcxCzAJBgNVBAYTAlVTMRMw
# EQYDVQQIEwpXYXNoaW5ndG9uMREwDwYDVQQHEwhJc3NhcXVhaDEXMBUGA1UEChMO
# RGFtaWFuIEVkd2FyZHMxFzAVBgNVBAMTDkRhbWlhbiBFZHdhcmRzMIIBojANBgkq
# hkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAiq1UB04a7/+l8xPDUkIBElIxySnIRQzH
# JHMoCPa0TENML6jRbOUQ/h0b8GZHd3AFBF262z245B6uHSwEHqyVDWsfTmueGcxB
# pvaFMjBKlYtb9Cb9RkwfxEQHliBGAKCrict639Oos8ykjRiLLPKFaKBiFYsJxcfg
# lzd1q/hIdji1cxHoEbasHVs9jvAchbwPaCkPkb1BHccpJSsDgHVmhbSheNeXITuX
# gmxNMQZJhJ6FhwZCLnHBofRoMJQKf2XlmbJxW1xzcVeA2xaNhhMLyKkymecVcOJS
# RBtFWU51Qx87uU+Vx8Wb8tXZPKT/qwWAunenyi6pdQvtKVNLb3VWutfLm7pDOdnI
# EZ6gONf82Kx8ntNFJDFms/usWETg/7H5XUjUGN4zT17p7ZWTQ5pJttEgiQ7V/Kzb
# YasBifVnteVc40NW4Oa7Tc9raLFD8GsZQzIB4i87E5qKTFjWElgbE8yyKklTCPN+
# cQ4wlqWzguW/VYCEKjRMlXtHS2lAWedVAgMBAAGjggHTMIIBzzAMBgNVHRMBAf8E
# AjAAMA4GA1UdDwEB/wQEAwIHgDA6BgNVHSUEMzAxBgorBgEEAYI3YQEABggrBgEF
# BQcDAwYZKwYBBAGCN2Hw+IcsobDAE+WG0RWDlKGiITAdBgNVHQ4EFgQUHkx2THVX
# ewTKKLlmNeTvUwm4yG4wHwYDVR0jBBgwFoAUpEMMf3ZapYXnPo0oDwwXokVpcMYw
# ZwYDVR0fBGAwXjBcoFqgWIZWaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9w
# cy9jcmwvTWljcm9zb2Z0JTIwSUQlMjBWZXJpZmllZCUyMENTJTIwQU9DJTIwQ0El
# MjAwMy5jcmwwdAYIKwYBBQUHAQEEaDBmMGQGCCsGAQUFBzAChlhodHRwOi8vd3d3
# Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NlcnRzL01pY3Jvc29mdCUyMElEJTIwVmVy
# aWZpZWQlMjBDUyUyMEFPQyUyMENBJTIwMDMuY3J0MFQGA1UdIARNMEswSQYEVR0g
# ADBBMD8GCCsGAQUFBwIBFjNodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3Bz
# L0RvY3MvUmVwb3NpdG9yeS5odG0wDQYJKoZIhvcNAQEMBQADggIBAL0N1ZZb5Hfn
# SKfA2Hr2YGnLbD/98lRoqurWAzoapdh284pSzsfpf1KPcA38f7nVFKshcRvyfvVk
# a2AeISYrZ3F9In3GzHYhN2w2V+OtkX+2kgir7pFjw+TUOWHlPtx9zDDIi1dMsIFj
# cAg60XD+YrjKE0i38TM0SNxM9CoHJbXm2CK0Kywz+O3AxrkxCZJRsxlKyh8ECtr1
# ni5b2TAKx8Om9rd0QAdcSrvDk3Vh0/hT9Pn44jv195yZxM7jZvUcy0Lwaqj40eXw
# L6kcJ+k25XinklLf5I8mSQ9kaUwsfrJyrNdGDPX7n9yCbCKA6Ah8biKPmlfVBDCt
# muMcIgz/f7/Nik2sVGqAvaPhuBwI1r/c+A239Ij37beg7+0b1r2hZKu+6e1e0iBR
# aYCReT0iOd/p4pQIZsRN2XAHGCIhsskCj0BGL5KVzC+s6YiGP92TAqZRZatM70I1
# 8zmTx/AynyLLSZyUIKtU7Dki1y+LrwYTbtLfCpNTMX7CDN9aPYnqcdQaClx4W0RT
# PDAj+D3WJAKpUtEzetCGgN7pnx7/MLiOHnotwvSvSfVy7SvjGC0FdW1iXxs6I5s5
# 0oPz0SI+7yKnzqYwqut+2Nta0e6+te3IXiNBGaF0Yjf1QME3v/Xqo5dx10p48w/A
# Y7Sa5i6YJqF4UPfntcXOuzjGiMyK/X8mMIIHKDCCBRCgAwIBAgITMwAAABgN65FV
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
# uPgNPa6EnTiOL60cPqfny+Fq8UiuZzGCGpMwghqPAgEBMHEwWjELMAkGA1UEBhMC
# VVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjErMCkGA1UEAxMiTWlj
# cm9zb2Z0IElEIFZlcmlmaWVkIENTIEFPQyBDQSAwMwITMwAFu9KSFMhI3TdRCAAA
# AAW70jANBglghkgBZQMEAgEFAKBeMBAGCisGAQQBgjcCAQwxAjAAMBkGCSqGSIb3
# DQEJAzEMBgorBgEEAYI3AgEEMC8GCSqGSIb3DQEJBDEiBCC4uty/m10FTEjrH7xp
# VHIZl6I2dqAXFKPzb+ADVR+LbjANBgkqhkiG9w0BAQEFAASCAYAQlla70cokFg30
# dlObJIiBFWQNCG64FZ/ebkeRUr4y73nYcrz7a1gxlQ+Z8AF4xKv3tLdiFHlASpsI
# UWKAfTRYNDHH8fIF/pcQjVoqs87xUmub4ju1boK2asXy9ymgISzWCao37od5kn0p
# cm3IG7CSMruhwHvNFQL3EzG6SVdsh9Fel8l57l3NOAjPrlp42VVmayyv3CuTlG5W
# 8Pc6IYuSEEk9vgm+ROaPUswuDieUIjEEsGt9xXV61HD+FlyH7UXVzyXqrk3t/Z4b
# jzzIohf4sK618gNY03kN8vQaR0bgdtt33F+QvZd/25DgLtvc6W14Z1QgwTnXuU5N
# mh+nSohRdX0zy2bLEAdiN8Wyn1iDyteNQ9lktw5nhbCQH1sNdCPantfvbnO4Lg4B
# tmEwXZAzXqwc6jpFp7ZkW6Ije4JG+1Ipp/oRaU1w+weIN1zdMcoi9nDq94zP0BYf
# bvf/sBlqL9/XE4peheBFabAuLORMRdHfEX6mK1Fc3OdCoMYW9ZChghgTMIIYDwYK
# KwYBBAGCNwMDATGCF/8wghf7BgkqhkiG9w0BBwKgghfsMIIX6AIBAzEPMA0GCWCG
# SAFlAwQCAQUAMIIBYQYLKoZIhvcNAQkQAQSgggFQBIIBTDCCAUgCAQEGCisGAQQB
# hFkKAwEwMTANBglghkgBZQMEAgEFAAQgWcp1XXqqTDeeFcUlxoXa30CUwhflnUe8
# GcbfguGZiqYCBmqESPj6CxgSMjAyNjA5MDIxODU2MzcuNjRaMASAAgH0oIHhpIHe
# MIHbMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMH
# UmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYDVQQL
# ExxNaWNyb3NvZnQgQW1lcmljYSBPcGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxk
# IFRTUyBFU046QTUwMC0wNUUwLUQ5NDcxNTAzBgNVBAMTLE1pY3Jvc29mdCBQdWJs
# aWMgUlNBIFRpbWUgU3RhbXBpbmcgQXV0aG9yaXR5oIIPITCCB4IwggVqoAMCAQIC
# EzMAAAAF5c8P/2YuyYcAAAAAAAUwDQYJKoZIhvcNAQEMBQAwdzELMAkGA1UEBhMC
# VVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjFIMEYGA1UEAxM/TWlj
# cm9zb2Z0IElkZW50aXR5IFZlcmlmaWNhdGlvbiBSb290IENlcnRpZmljYXRlIEF1
# dGhvcml0eSAyMDIwMB4XDTIwMTExOTIwMzIzMVoXDTM1MTExOTIwNDIzMVowYTEL
# MAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEyMDAG
# A1UEAxMpTWljcm9zb2Z0IFB1YmxpYyBSU0EgVGltZXN0YW1waW5nIENBIDIwMjAw
# ggIiMA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQCefOdSY/3gxZ8FfWO1BiKj
# HB7X55cz0RMFvWVGR3eRwV1wb3+yq0OXDEqhUhxqoNv6iYWKjkMcLhEFxvJAeNcL
# AyT+XdM5i2CgGPGcb95WJLiw7HzLiBKrxmDj1EQB/mG5eEiRBEp7dDGzxKCnTYoc
# DOcRr9KxqHydajmEkzXHOeRGwU+7qt8Md5l4bVZrXAhK+WSk5CihNQsWbzT1nRli
# VDwunuLkX1hyIWXIArCfrKM3+RHh+Sq5RZ8aYyik2r8HxT+l2hmRllBvE2Wok6IE
# aAJanHr24qoqFM9WLeBUSudz+qL51HwDYyIDPSQ3SeHtKog0ZubDk4hELQSxnfVY
# XdTGncaBnB60QrEuazvcob9n4yR65pUNBCF5qeA4QwYnilBkfnmeAjRN3LVuLr0g
# 0FXkqfYdUmj1fFFhH8k8YBozrEaXnsSL3kdTD01X+4LfIWOuFzTzuoslBrBILfHN
# j8RfOxPgjuwNvE6YzauXi4orp4Sm6tF245DaFOSYbWFK5ZgG6cUY2/bUq3g3bQAq
# Zt65KcaewEJ3ZyNEobv35Nf6xN6FrA6jF9447+NHvCjeWLCQZ3M8lgeCcnnhTFty
# QX3XgCoc6IRXvFOcPVrr3D9RPHCMS6Ckg8wggTrtIVnY8yjbvGOUsAdZbeXUIQAW
# Ms0d3cRDv09SvwVRd61evQIDAQABo4ICGzCCAhcwDgYDVR0PAQH/BAQDAgGGMBAG
# CSsGAQQBgjcVAQQDAgEAMB0GA1UdDgQWBBRraSg6NS9IY0DPe9ivSek+2T3bITBU
# BgNVHSAETTBLMEkGBFUdIAAwQTA/BggrBgEFBQcCARYzaHR0cDovL3d3dy5taWNy
# b3NvZnQuY29tL3BraW9wcy9Eb2NzL1JlcG9zaXRvcnkuaHRtMBMGA1UdJQQMMAoG
# CCsGAQUFBwMIMBkGCSsGAQQBgjcUAgQMHgoAUwB1AGIAQwBBMA8GA1UdEwEB/wQF
# MAMBAf8wHwYDVR0jBBgwFoAUyH7SaoUqG8oZmAQHJ89QEE9oqKIwgYQGA1UdHwR9
# MHsweaB3oHWGc2h0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY3JsL01p
# Y3Jvc29mdCUyMElkZW50aXR5JTIwVmVyaWZpY2F0aW9uJTIwUm9vdCUyMENlcnRp
# ZmljYXRlJTIwQXV0aG9yaXR5JTIwMjAyMC5jcmwwgZQGCCsGAQUFBwEBBIGHMIGE
# MIGBBggrBgEFBQcwAoZ1aHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9j
# ZXJ0cy9NaWNyb3NvZnQlMjBJZGVudGl0eSUyMFZlcmlmaWNhdGlvbiUyMFJvb3Ql
# MjBDZXJ0aWZpY2F0ZSUyMEF1dGhvcml0eSUyMDIwMjAuY3J0MA0GCSqGSIb3DQEB
# DAUAA4ICAQBfiHbHfm21WhV150x4aPpO4dhEmSUVpbixNDmv6TvuIHv1xIs174bN
# GO/ilWMm+Jx5boAXrJxagRhHQtiFprSjMktTliL4sKZyt2i+SXncM23gRezzsoOi
# Bhv14YSd1Klnlkzvgs29XNjT+c8hIfPRe9rvVCMPiH7zPZcw5nNjthDQ+zD563I1
# nUJ6y59TbXWsuyUsqw7wXZoGzZwijWT5oc6GvD3HDokJY401uhnj3ubBhbkR83Rb
# fMvmzdp3he2bvIUztSOuFzRqrLfEvsPkVHYnvH1wtYyrt5vShiKheGpXa2AWpsod
# 4OJyT4/y0dggWi8g/tgbhmQlZqDUf3UqUQsZaLdIu/XSjgoZqDjamzCPJtOLi2hB
# wL+KsCh0Nbwc21f5xvPSwym0Ukr4o5sCcMUcSy6TEP7uMV8RX0eH/4JLEpGyae6K
# i8JYg5v4fsNGif1OXHJ2IWG+7zyjTDfkmQ1snFOTgyEX8qBpefQbF0fx6URrYiar
# jmBprwP6ZObwtZXJ23jK3Fg/9uqM3j0P01nzVygTppBabzxPAh/hHhhls6kwo3QL
# J6No803jUsZcd4JQxiYHHc+Q/wAMcPUnYKv/q2O444LO1+n6j01z5mggCSlRwD9f
# aBIySAcA9S8h22hIAcRQqIGEjolCK9F6nK9ZyX4lhthsGHumaABdWzCCB5cwggV/
# oAMCAQICEzMAAABWfo+dWAiO6WAAAAAAAFYwDQYJKoZIhvcNAQEMBQAwYTELMAkG
# A1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEyMDAGA1UE
# AxMpTWljcm9zb2Z0IFB1YmxpYyBSU0EgVGltZXN0YW1waW5nIENBIDIwMjAwHhcN
# MjUxMDIzMjA0NjUxWhcNMjYxMDIyMjA0NjUxWjCB2zELMAkGA1UEBhMCVVMxEzAR
# BgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAcBgNVBAoTFU1p
# Y3Jvc29mdCBDb3Jwb3JhdGlvbjElMCMGA1UECxMcTWljcm9zb2Z0IEFtZXJpY2Eg
# T3BlcmF0aW9uczEnMCUGA1UECxMeblNoaWVsZCBUU1MgRVNOOkE1MDAtMDVFMC1E
# OTQ3MTUwMwYDVQQDEyxNaWNyb3NvZnQgUHVibGljIFJTQSBUaW1lIFN0YW1waW5n
# IEF1dGhvcml0eTCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBALSln5v7
# pdNu/3fEZW/DJ/4NEFL7y6mNlbMt7SPFNrRUrKU2aJmTg9wR0/C5Efka4TCYG9VY
# wChTcrGivXC0l4nzxkiAazwoLPT+MtuJayRJq1ekOc+AZqjISD62YRL2Z1qQkuBz
# u42Enov58Zgu/9RK/peS4Nz5ksW/HdiFXAEcUsNQeJsQelyNJ5HpfcGtXWG9sHxq
# aH62hZsWTsU/XjYbeCx9EQUlbnm2umTaY0v9ILX5u6oiIsj+qej0c002zJ1arB51
# f3f61tMx8fkPkDWecFKipk2SQfYVPOd/tqV+aw3yt9rjWPf1gTgJs26oKRHUJG4j
# Gr1DMlA0oZsnCL4B3UJ0ttO7E4/DPpCS97TnWoT7j6jMLGggoHX8MEMdDvUynuxU
# r2wBGLNQJ5XQpfyhxmQjlb1Dao8i9dCS3tP/hg/f8p6lxlhaVzo2rp72f3CkToYz
# eDOXuscdG9poqnD4ouP4otmYXimpZSRE+wipaRUobN8MoOhf36I0MULz521g+Dcs
# epYY1o8JqC3MesNRUgrWrywpct9wS0UpU1OKilMWmvHe2DexKqZ/VztEmNLpjryh
# V61h+68ZfvYmonIrXZ005LAJ0Y73pHSk95YO5UTH5n2VPL1zYjdFGCc0/RI6o0Zt
# Ljf4dKF8T4TXz2KnhW8j1xhsc2mFM+s8d6k3AgMBAAGjggHLMIIBxzAdBgNVHQ4E
# FgQUvrYz8rurWf4eRrMi78s9R/hTSFowHwYDVR0jBBgwFoAUa2koOjUvSGNAz3vY
# r0npPtk92yEwbAYDVR0fBGUwYzBhoF+gXYZbaHR0cDovL3d3dy5taWNyb3NvZnQu
# Y29tL3BraW9wcy9jcmwvTWljcm9zb2Z0JTIwUHVibGljJTIwUlNBJTIwVGltZXN0
# YW1waW5nJTIwQ0ElMjAyMDIwLmNybDB5BggrBgEFBQcBAQRtMGswaQYIKwYBBQUH
# MAKGXWh0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9z
# b2Z0JTIwUHVibGljJTIwUlNBJTIwVGltZXN0YW1waW5nJTIwQ0ElMjAyMDIwLmNy
# dDAMBgNVHRMBAf8EAjAAMBYGA1UdJQEB/wQMMAoGCCsGAQUFBwMIMA4GA1UdDwEB
# /wQEAwIHgDBmBgNVHSAEXzBdMFEGDCsGAQQBgjdMg30BATBBMD8GCCsGAQUFBwIB
# FjNodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL0RvY3MvUmVwb3NpdG9y
# eS5odG0wCAYGZ4EMAQQCMA0GCSqGSIb3DQEBDAUAA4ICAQAOA6gFxLDtuo/y2uxY
# J/0In4rfMbmXpKmee/mHvrB/4UU2xBIxmK2YLKsEf5VFHyghaW2RfJrGmT0CTkeG
# GFBFPF8oy65/GNYwkpqMYfZe7VokqHPyRQcN+eiQJsxhsXgQNhFksUbk69QLmXup
# 2GjfP8LRZIh3LPIDGncVwbOg8VYcruWJ4Sz0JH7pipt5RX7cBO6Ynle39ZbJJpYL
# AugHkhgsxj2VIAr3B+U7/0Hvc+2yCJkg90rs4TiMGj/nikE2H+u04n8iSpFkEnRn
# 0wOinLuNZPCweqDyvjC5NY28cSucD6i0i+tsYytOEgVxxCUhJ7BbdM8VpMT/5YHo
# 9Q8alJ5q2BHZMb8ykhyAKhVkmbpf+YSPrycbxT4bDUARJOHErpQ5CUKXHVYv4Jn/
# 5hxTmIQwY7GtebOC/trAYpd11f0/EYkeukPMWL0y0VsXdnVbKzqAsJ7FOFiHogtC
# Ypwr9VixxIe0Ms6/UUq+JCiS1naTWC4YI5KI05hJAIxTu++Ld8Qe3p27yBdBjrFd
# fcZwlM6vRBisrdIDLmqYSpTYyfmk6Y1jGQxqPhjirJ6fdx5n7ZpdEsqdxffjN8vs
# uliRlGaCGSattu4w44xJ3baVK4fQXT3VSH1SQ/wLvNUc4dOVBwIr6K0NzrPDxCxy
# IIjnfU1s23YJhs3CC7f3XVUBETGCB0YwggdCAgEBMHgwYTELMAkGA1UEBhMCVVMx
# HjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEyMDAGA1UEAxMpTWljcm9z
# b2Z0IFB1YmxpYyBSU0EgVGltZXN0YW1waW5nIENBIDIwMjACEzMAAABWfo+dWAiO
# 6WAAAAAAAFYwDQYJYIZIAWUDBAIBBQCgggSfMBEGCyqGSIb3DQEJEAIPMQIFADAa
# BgkqhkiG9w0BCQMxDQYLKoZIhvcNAQkQAQQwHAYJKoZIhvcNAQkFMQ8XDTI2MDkw
# MjE4NTYzN1owLwYJKoZIhvcNAQkEMSIEIC4JnDHz4Gh+u2bEPaZ9aQ2Z5ihDkW94
# A1t6BxDUNmiIMIG5BgsqhkiG9w0BCRACLzGBqTCBpjCBozCBoAQgtgwzJU2k4/CV
# d4k4OV56XuAkh+tNeN2fl/aOTQYDDKgwfDBlpGMwYTELMAkGA1UEBhMCVVMxHjAc
# BgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEyMDAGA1UEAxMpTWljcm9zb2Z0
# IFB1YmxpYyBSU0EgVGltZXN0YW1waW5nIENBIDIwMjACEzMAAABWfo+dWAiO6WAA
# AAAAAFYwggNhBgsqhkiG9w0BCRACEjGCA1AwggNMoYIDSDCCA0QwggIsAgEBMIIB
# CaGB4aSB3jCB2zELMAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAO
# BgNVBAcTB1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEl
# MCMGA1UECxMcTWljcm9zb2Z0IEFtZXJpY2EgT3BlcmF0aW9uczEnMCUGA1UECxMe
# blNoaWVsZCBUU1MgRVNOOkE1MDAtMDVFMC1EOTQ3MTUwMwYDVQQDEyxNaWNyb3Nv
# ZnQgUHVibGljIFJTQSBUaW1lIFN0YW1waW5nIEF1dGhvcml0eaIjCgEBMAcGBSsO
# AwIaAxUA/3P3KRUqkFmAXl4IMkSdmW72BBGgZzBlpGMwYTELMAkGA1UEBhMCVVMx
# HjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEyMDAGA1UEAxMpTWljcm9z
# b2Z0IFB1YmxpYyBSU0EgVGltZXN0YW1waW5nIENBIDIwMjAwDQYJKoZIhvcNAQEL
# BQACBQDuQo3dMCIYDzIwMjYwOTAyMTE1ODIxWhgPMjAyNjA5MDMxMTU4MjFaMHcw
# PQYKKwYBBAGEWQoEATEvMC0wCgIFAO5Cjd0CAQAwCgIBAAICG50CAf8wBwIBAAIC
# EvcwCgIFAO5D310CAQAwNgYKKwYBBAGEWQoEAjEoMCYwDAYKKwYBBAGEWQoDAqAK
# MAgCAQACAwehIKEKMAgCAQACAwGGoDANBgkqhkiG9w0BAQsFAAOCAQEAMANOmWMe
# 3barWhsVb87iuKRdZLmMAJLBe0D1hNuYyeQCpwo2ZN2VLXVzrxk2YOq36Ukji97w
# 2Yj62kgtTB0HHe9VOEj3NRkD/yZhLPUfAMcuRjUGAETR3F3qxXv8yKJXQ32In3Ea
# ZmtM9ACxZimkY2aQk++TY26Sh/n7FWY8oLnLt+CSXfRTeVSdeNnQ2rpgB47mNj0q
# QRdp17LQVXkI+uDRxh2+RJZ6P/r9QGbkO2JY6ny921rl3hMME2rwL+R3Lni48iJ7
# HuOFf2lgg4RuG3L0a3c7MLpt6Gkvl/RUk9Xta8rMhu0sUTNuj86BiSuYf79h0R7z
# eKKXHB59aC+CBTANBgkqhkiG9w0BAQEFAASCAgA49HMH6EwhWzHhOzSrOqgfHnv/
# lfn6gyKIKse3K0Jb6qTv9uN+vTbIxIcIgjC1jPrXPnBD5G6Yy/dlAAnWtNMZKJce
# Ty5g7MnsHyOu15HX7TY8jJQCw9F6CvCoWx0LvyCSeRWdD4Wc4ccimjQGcG2ZJ7VM
# YHBMCx/KwKSeJ07AJBQIHx36kQLUINu012xkSYKBESsFmJ7hW3YZjt0dpNMESOxj
# G3+vw207IrI++hj21qUDmUMyFCUWFAGuCgvPZfnH61paVztTJMgjoBeF4aRKROWx
# 71qHmu0GyRKu7ckhWZl5aXacxYCbTPsm4bI2qlRbi2SX6ZwfKEmpDFk2ZYP20PVu
# yvK6NO5g1sYbBAUy79JdXTQOj5coZ2DQhrNxmR5KxKyxzDKYgCcx6j9ogbiml7Vi
# HOcDjaSE44f/7y4WUK5D3qI0sNp+YUkJNUBv0dmWD+x9lTI6qOk3XNnWUxI/RqR7
# ehUwLoR56hZkUo/hvGOO5jcTVL0Lt8JRVaWnH+8HS4KrAIBtSJpSkW38W1PZu4b5
# KvRZ5dC4ZsiZCWpoGJ3+XnHwaJ1kJSJHU/I5R7NGYVE0Sf7B5983MxXvSlg1Y0XM
# v2opVFdZHQ0eF+EGzAK2IVrBILje9aWwC8vYvTxN/XIYNt1MttVrS3N3yhGQ5x+j
# apUdLtBgCzgr90JFsw==
# SIG # End signature block
