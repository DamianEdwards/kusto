[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = 'SingleBinary')]
    [string]$BinaryPath,

    [Parameter(Mandatory, ParameterSetName = 'PayloadDirectory')]
    [string]$PayloadDirectory,

    [string]$InstallerScriptPath = (Join-Path $PSScriptRoot 'install\install-kusto-cli.ps1')
)

$ErrorActionPreference = 'Stop'

$installerScriptPath = [System.IO.Path]::GetFullPath($InstallerScriptPath)

if (-not (Test-Path $installerScriptPath))
{
    throw "Installer script '$installerScriptPath' was not found."
}

Write-Verbose "Loading installer trust helpers from '$installerScriptPath'."
. $installerScriptPath -NoExecute

$config = Get-KustoInstallerTrustConfiguration
$expectedThumbprints = @($config.ExpectedSignerIssuerSha512Thumbprints)
$expectedParentThumbprints = @($config.ExpectedSignerParentIssuerSha512Thumbprints)

$binariesToVerify = @()
if ($PSCmdlet.ParameterSetName -eq 'SingleBinary')
{
    $resolvedBinaryPath = [System.IO.Path]::GetFullPath($BinaryPath)
    if (-not (Test-Path $resolvedBinaryPath))
    {
        throw "Signed binary '$resolvedBinaryPath' was not found."
    }
    $binariesToVerify = @($resolvedBinaryPath)
}
else
{
    $resolvedPayloadDirectory = [System.IO.Path]::GetFullPath($PayloadDirectory)
    if (-not (Test-Path $resolvedPayloadDirectory))
    {
        throw "Payload directory '$resolvedPayloadDirectory' was not found."
    }

    $expectedExecutableFiles = @($config.ExpectedExecutablePayloadFiles)
    if ($expectedExecutableFiles.Count -eq 0)
    {
        throw "Installer trust configuration does not list any expected executable payload files."
    }

    $missing = @()
    foreach ($name in $expectedExecutableFiles)
    {
        $candidate = Join-Path $resolvedPayloadDirectory $name
        if (-not (Test-Path $candidate))
        {
            $missing += $name
        }
        else
        {
            $binariesToVerify += $candidate
        }
    }

    if ($missing.Count -gt 0)
    {
        throw "Payload directory '$resolvedPayloadDirectory' is missing required executable file(s): $($missing -join ', ')."
    }
}

$formattedExpectedThumbprints = ($expectedThumbprints | ForEach-Object { "'$_'" }) -join ', '
$formattedExpectedParentThumbprints = ($expectedParentThumbprints | ForEach-Object { "'$_'" }) -join ', '

foreach ($binary in $binariesToVerify)
{
    $evidence = Assert-WindowsBinaryTrust `
        -BinaryPath $binary `
        -ExpectedSubject $config.ExpectedSignerSubject `
        -ExpectedIssuerSha512Thumbprints $expectedThumbprints `
        -ExpectedParentIssuerSha512Thumbprints $expectedParentThumbprints
    $issuerTrustMatch = $evidence.SignerIssuerTrustMatch

    $matchDescription = if ($issuerTrustMatch.UsedFallback)
    {
        "using parent issuer fallback '$($issuerTrustMatch.Certificate.Subject)' ($($issuerTrustMatch.Sha512Thumbprint))"
    }
    else
    {
        "using immediate issuer '$($issuerTrustMatch.Certificate.Subject)' ($($issuerTrustMatch.Sha512Thumbprint))"
    }

    Write-Host "Verified signer issuer chain for '$binary' $matchDescription. Allowed immediate SHA512 thumbprints: $formattedExpectedThumbprints. Allowed parent SHA512 thumbprints: $formattedExpectedParentThumbprints."
}
