<#
.SYNOPSIS
  Generates the development signing key (AgentOffice_TemporaryKey.pfx)
  required by the VSTO build targets for ClickOnce manifest signing.

.DESCRIPTION
  Mirrors what Visual Studio does when you check "Sign the ClickOnce manifests"
  and click "Create Test Certificate": a self-signed, exportable, password-less
  code-signing certificate placed next to the .csproj, plus syncing its
  thumbprint into the csproj <ManifestCertificateThumbprint> element.

  The key is git-ignored on purpose (machine-specific). Re-run this script on
  any new dev machine. For production, the WiX MSI is what gets
  signed with a real code-signing certificate.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\New-DevCertificate.ps1
#>

$ErrorActionPreference = 'Stop'

$projectDir = Join-Path $PSScriptRoot '..\src'
$csprojPath = Join-Path $projectDir 'AgentOffice.csproj'
$pfxPath    = Join-Path $projectDir 'AgentOffice_TemporaryKey.pfx'
$subject    = 'CN=AgentOffice (development)'

function Get-DevCert {
    Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey } |
        Select-Object -First 1
}

function New-DevCert {
    Write-Host "Creating self-signed code-signing certificate ($subject)..."
    New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -CertStoreLocation 'Cert:\CurrentUser\My' -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5)
}

function Export-PasswordlessPfx($cert, $path) {
    $bytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx)
    [System.IO.File]::WriteAllBytes($path, $bytes)
}

function Get-CsprojThumbprint {
    $xml = Get-Content $csprojPath -Raw
    if ($xml -match '<ManifestCertificateThumbprint>([0-9A-Fa-f]*)</ManifestCertificateThumbprint>') {
        return $Matches[1]
    }
    return $null
}

function Set-CsprojThumbprint($thumbprint) {
    $xml = Get-Content $csprojPath -Raw
    $xml = $xml -replace '<ManifestCertificateThumbprint>[0-9A-Fa-f]*</ManifestCertificateThumbprint>', "<ManifestCertificateThumbprint>$thumbprint</ManifestCertificateThumbprint>"
    Set-Content $csprojPath $xml -NoNewline
}

$cert = Get-DevCert
if (-not $cert) { $cert = New-DevCert }

# Fast path: key file exists and the csproj already points at our cert.
$csprojThumb = Get-CsprojThumbprint
if ((Test-Path $pfxPath) -and ($csprojThumb -eq $cert.Thumbprint)) {
    Write-Host "Already set up: $pfxPath (thumbprint $csprojThumb)"
    exit 0
}

# Export the pfx (password-less, like VS temporary keys). Store-retrieved keys
# sometimes refuse export - recreate the cert once if that happens.
try {
    Export-PasswordlessPfx $cert $pfxPath
} catch {
    Write-Host "Existing key is not exportable - recreating the certificate..."
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force
    $cert = New-DevCert
    Export-PasswordlessPfx $cert $pfxPath
}
Write-Host "Key written: $pfxPath"

# Sync the thumbprint into the csproj (the VSTO targets use it to register the
# cert in CurrentUser\Trusted Publishers during build).
if ($null -eq $csprojThumb) {
    Write-Host "WARNING: no <ManifestCertificateThumbprint> element found in the csproj - add it manually: $($cert.Thumbprint)"
} elseif ($csprojThumb -ne $cert.Thumbprint) {
    Set-CsprojThumbprint $cert.Thumbprint
    Write-Host "csproj thumbprint updated: $csprojThumb -> $($cert.Thumbprint)"
} else {
    Write-Host "csproj thumbprint already in sync: $($cert.Thumbprint)"
}

Write-Host "You can now build: MSBuild AgentOffice.sln /p:Configuration=Debug"
