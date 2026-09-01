<#
.SYNOPSIS
    Generates a standalone Authenticode verifier from the kusto installer.

.DESCRIPTION
    The installer is the single source of truth for signer identity and
    certificate-chain validation. This script extracts that configuration and
    the SharedProvenanceFunctions region into a standalone verifier.
#>
[CmdletBinding()]
param(
    [string]$InstallerPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($InstallerPath))
{
    $InstallerPath = Join-Path $scriptRoot 'install\install-kusto-cli.ps1'
}
if ([string]::IsNullOrWhiteSpace($OutputPath))
{
    $OutputPath = Join-Path $scriptRoot 'verify-provenance.ps1'
}
$installerPath = [System.IO.Path]::GetFullPath($InstallerPath)
$outputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path $installerPath))
{
    throw "Cannot find install-kusto-cli.ps1 at '$installerPath'."
}

$content = Get-Content -Path $installerPath -Raw
$subjectMatch = [regex]::Match($content, "(?m)ExpectedSignerSubject\s*=\s*'([^']+)'")
if (-not $subjectMatch.Success)
{
    throw 'Could not extract ExpectedSignerSubject from install-kusto-cli.ps1.'
}

$issuerMatch = [regex]::Match(
    $content,
    '(?ms)^\$ExpectedSignerIssuerSha512Thumbprints\s*=\s*@\(.*?^\)')
$parentIssuerMatch = [regex]::Match(
    $content,
    '(?ms)^\$ExpectedSignerParentIssuerSha512Thumbprints\s*=\s*@\(.*?^\)')
$functionsMatch = [regex]::Match(
    $content,
    '(?ms)^#region SharedProvenanceFunctions\s*\r?\n(?<functions>.*?)^#endregion SharedProvenanceFunctions')
if (-not $issuerMatch.Success -or -not $parentIssuerMatch.Success -or -not $functionsMatch.Success)
{
    throw 'Could not extract installer trust constants or SharedProvenanceFunctions.'
}

$header = @"
<#
.SYNOPSIS
    Verifies Authenticode provenance for a kusto Windows executable payload.

.DESCRIPTION
    AUTO-GENERATED from install-kusto-cli.ps1 by Generate-VerifyProvenance.ps1.
    Edit the installer source rather than this generated file.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]`$BinaryPath
)

`$ErrorActionPreference = 'Stop'
`$ExpectedSignerSubject = '$($subjectMatch.Groups[1].Value)'
"@

$footer = @'

try
{
    $null = Assert-WindowsBinaryTrust `
        -BinaryPath $BinaryPath `
        -ExpectedSubject $ExpectedSignerSubject `
        -ExpectedIssuerSha512Thumbprints $ExpectedSignerIssuerSha512Thumbprints `
        -ExpectedParentIssuerSha512Thumbprints $ExpectedSignerParentIssuerSha512Thumbprints
    @{ success = $true; error = $null } | ConvertTo-Json -Compress
}
catch
{
    @{ success = $false; error = $_.Exception.Message } | ConvertTo-Json -Compress
    exit 1
}
'@

$output = @(
    $header.TrimEnd()
    $issuerMatch.Value.Trim()
    $parentIssuerMatch.Value.Trim()
    $functionsMatch.Groups['functions'].Value.Trim()
    $footer.TrimStart()
) -join "`n`n"
$output += "`n"

$outputDirectory = Split-Path -Parent $outputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$existing = if (Test-Path $outputPath) { Get-Content -Path $outputPath -Raw } else { $null }
if ($existing -ceq $output)
{
    Write-Host "verify-provenance.ps1 already up to date at '$outputPath'."
    return
}

Set-Content -Path $outputPath -Value $output -NoNewline -Encoding utf8
Write-Host "Generated verify-provenance.ps1 at '$outputPath'."
