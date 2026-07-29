<#
.SYNOPSIS
    Signs Stepwright.exe, and can create a certificate for you if you do not have one.

.DESCRIPTION
    Windows shows a warning for a program it has never seen before. What removes it:

      A certificate bought from a certificate authority  removes "Unknown publisher"
                                                          everywhere, for everyone
      A self signed certificate                          removes it only on machines
                                                          that have been told to trust it
      Nothing                                            the warning stays until enough
                                                          people have downloaded the file

    A self signed certificate is the right answer when you are pushing this to machines you
    manage, because you can deploy the certificate alongside it. It does nothing for a
    stranger downloading the file from the internet.

.EXAMPLE
    .\sign.ps1 -Create -Subject "Cooli IT" -PfxPath .\stepwright.pfx -Password (Read-Host -AsSecureString)

.EXAMPLE
    .\sign.ps1 -PfxPath .\stepwright.pfx -Password (Read-Host -AsSecureString) -File ..\publish\Stepwright.exe

.EXAMPLE
    .\sign.ps1 -TrustOnThisMachine -PfxPath .\stepwright.pfx
#>

[CmdletBinding()]
param(
    [switch] $Create,
    [string] $Subject = "Stepwright",
    [string] $PfxPath = ".\stepwright.pfx",
    [System.Security.SecureString] $Password,
    [string] $File,
    [switch] $TrustOnThisMachine,
    [string] $TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

function Find-SignTool {
    $candidates = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName
    if ($candidates) { return $candidates[-1].FullName }

    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    throw "signtool.exe was not found. Install the Windows SDK, or use the signing step in the build workflow instead."
}

if ($Create) {
    if (-not $Password) { throw "A password is needed to protect the certificate file." }

    Write-Host "Creating a code signing certificate for '$Subject' that lasts three years."

    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject "CN=$Subject" `
        -KeyUsage DigitalSignature `
        -FriendlyName "$Subject code signing" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter (Get-Date).AddYears(3) `
        -KeyExportPolicy Exportable `
        -KeyLength 3072 `
        -HashAlgorithm SHA256

    Export-PfxCertificate -Cert $certificate -FilePath $PfxPath -Password $Password | Out-Null
    Write-Host "Wrote $PfxPath"
    Write-Host ""
    Write-Host "For the build workflow, turn it into text and store it as a repository secret:"
    Write-Host "  [Convert]::ToBase64String([IO.File]::ReadAllBytes('$PfxPath')) | Set-Clipboard"
    Write-Host "  secret name SIGNING_CERT_BASE64, and SIGNING_CERT_PASSWORD for the password"
}

if ($TrustOnThisMachine) {
    if (-not (Test-Path $PfxPath)) { throw "No certificate file at $PfxPath" }

    Write-Host "Trusting the certificate on this machine. This needs an elevated prompt."

    $arguments = @("-PfxPath", $PfxPath)
    Import-PfxCertificate -FilePath $PfxPath -CertStoreLocation Cert:\LocalMachine\Root -Password $Password | Out-Null
    Import-PfxCertificate -FilePath $PfxPath -CertStoreLocation Cert:\LocalMachine\TrustedPublisher -Password $Password | Out-Null

    Write-Host "Done. Signed builds will no longer show an unknown publisher on this machine."
    Write-Host "Across a fleet, push the same certificate to Trusted Root and Trusted Publishers by policy."
}

if ($File) {
    if (-not (Test-Path $File)) { throw "No file at $File" }
    if (-not (Test-Path $PfxPath)) { throw "No certificate file at $PfxPath" }
    if (-not $Password) { throw "The certificate password is needed to sign." }

    $signtool = Find-SignTool
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password))

    & $signtool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $PfxPath /p $plain $File
    if ($LASTEXITCODE -ne 0) { throw "Signing failed with code $LASTEXITCODE" }

    & $signtool verify /pa /v $File
    Write-Host "Signed $File"
}

if (-not $Create -and -not $File -and -not $TrustOnThisMachine) {
    Get-Help $PSCommandPath -Detailed
}
