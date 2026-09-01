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

    $manifestPayloadFiles = @(Assert-PayloadManifestMatches -ExtractDirectory $resolvedPayloadDirectory)
    Assert-ExtractedPayloadComplete `
        -ExtractDirectory $resolvedPayloadDirectory `
        -RequiredFileNames @('kusto.exe', 'payload-manifest.json')
    $executablePayloadFiles = @(Get-WindowsExecutablePayloadFiles -PayloadFiles $manifestPayloadFiles)
    if ($executablePayloadFiles.Count -eq 0)
    {
        throw "Payload directory '$resolvedPayloadDirectory' does not contain any executable files."
    }

    foreach ($name in $executablePayloadFiles)
    {
        $binariesToVerify += Join-Path $resolvedPayloadDirectory $name
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
